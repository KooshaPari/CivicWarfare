using Game;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Services;
using System.Threading;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Domains.AirDefense.Logic;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Releases manpower when AA installations are lost to ENEMY ACTION — combat destruction,
    /// driven by DestroyedBuildingEvent (survivor/KIA crew split, no refund). Runs in
    /// GameSimulation, where those events are produced; combat only happens unpaused.
    /// Both this system and WorldShockReactionSystem use GameSimulationEndBarrier — ECB playback
    /// happens after SimulationSystemGroup, so both see DestroyedBuildingEvent entities
    /// regardless of relative execution order within the group.
    ///
    /// PLAYER demolition (bulldozer) is handled separately by AAPlayerDemolitionSystem in the
    /// pause-safe ModificationEnd phase (full crew return + synchronous cash refund), because the
    /// player can bulldoze on pause when GameSimulation does not tick.
    /// </summary>
    [ActIndependent]
    public partial class AACrewReleaseSystem : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount(int count = 1) { for (int i = 0; i < count; i++) Interlocked.Increment(ref s_EcbCommandCount); }

        private static readonly LogContext Log = new("AACrewReleaseSystem");

        private EntityQuery m_AAQuery;
        private EntityQuery m_DestroyedBuildingEventQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<AirDefenseInstallation> m_AALookup;
        private NativeHashMap<long, float3> m_BuildingsDestroyedByThreat;
        // PERF-LOCK: building->AA index. Release is event-driven (DestroyedBuildingEvent -> index
        // -> AA), O(destroyed buildings), NOT a per-tick scan of all AirDefenseInstallation. The
        // index is rebuilt only when the AA set changes (count or structural order version),
        // never per tick. Per-tick full scan + per-AA work was the dominant wave cost.
#pragma warning disable CIVIC150 // Derived cache — rebuilt from ECS query via order-version invalidation, not serialized by design
        private NativeHashMap<long, Entity> m_AAByBuilding;
#pragma warning restore CIVIC150
        private int m_AAIndexLastCount = -1;
        [EntityQueryOrderCursor("Invalidates m_AAByBuilding when the AirDefenseInstallation archetype set changes structurally.")]
        private int m_AAIndexLastOrderVersion;
        private bool m_LoadGate;
#pragma warning disable CIVIC229 // System reference — UI stats cache is owned by AirDefenseStateSystem.
        private AirDefenseStateSystem m_StateSystem = null!;
#pragma warning restore CIVIC229

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AAQuery = GetEntityQuery(
                ComponentType.ReadOnly<AirDefenseInstallation>(),
                ComponentType.Exclude<Deleted>()
            );
            m_DestroyedBuildingEventQuery = GetEntityQuery(
                ComponentType.ReadOnly<DestroyedBuildingEvent>()
            );
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_AALookup = GetComponentLookup<AirDefenseInstallation>(true);
            m_BuildingsDestroyedByThreat = new NativeHashMap<long, float3>(16, Allocator.Persistent);
            m_AAByBuilding = new NativeHashMap<long, Entity>(16, Allocator.Persistent);
            RequireForUpdate(m_AAQuery);
            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_StateSystem ??= FeatureRegistry.Instance.Require<AirDefenseStateSystem>();
        }

        /// <summary>
        /// Load-window gate. Raised on the load boundary (before deserialize), cleared
        /// in PLVS Phase 2 (after ModEntityCleanup ran on the authoritative set).
        /// While gated this system must NOT run: between OnGamePreload and Phase 2 the
        /// only AA visible are pre-load stale entities (cleared by AAPlacementLifecycle)
        /// or freshly deserialized ones not yet validated — releasing manpower on those
        /// is the post-load leak.
        /// </summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LoadGate = true;
            Log.Info("[AA-GATE] OnGamePreload: load gate RAISED");
        }

        /// <summary>PLVS Phase 2 (HydrationOrder DEFAULT — runs after CLEANUP_FIRST).
        /// Authoritative post-load AA set now exists with valid remapped refs.</summary>
        public void ValidateAfterLoad()
        {
            m_LoadGate = false;
            Log.Info("[AA-GATE] ValidateAfterLoad: load gate CLEARED");
        }

        protected override void OnUpdateImpl()
        {
            if (m_LoadGate) return;

            // PERF-LOCK: event-driven release. Releases are driven by DestroyedBuildingEvent only;
            // on non-event ticks the system early-returns before touching any ECS data.
            if (m_DestroyedBuildingEventQuery.IsEmptyIgnoreFilter)
                return;

            m_BuildingsDestroyedByThreat.Clear();
            foreach (var evt in SystemAPI.Query<RefRO<DestroyedBuildingEvent>>())
                m_BuildingsDestroyedByThreat.TryAdd(evt.ValueRO.Building.Packed, evt.ValueRO.Position);

            if (m_BuildingsDestroyedByThreat.IsEmpty)
                return;

            m_DeletedLookup.Update(this);
            m_AALookup.Update(this);

            using (PerformanceProfiler.Measure("AACrewRelease.OnUpdate"))
            {
                EntityCommandBuffer ecb = default;
                bool ecbCreated = false;
                float survivalRate = BalanceConfig.Current.Mobilization.CasualtySurvivalRate;

                // THREAT PATH: O(destroyed buildings) via index, NOT O(all AA) scan.
                RefreshAAIndexIfNeeded();
                foreach (var destroyed in m_BuildingsDestroyedByThreat)
                {
                    // Guards (B1/B2): index may be stale for 1 frame (AA created same frame),
                    // or point to an entity already deleted — HasComponent/Deleted catch both.
                    if (!m_AAByBuilding.TryGetValue(destroyed.Key, out Entity aaEntity)
                        || !m_AALookup.HasComponent(aaEntity)
                        || m_DeletedLookup.HasComponent(aaEntity))
                        continue;

                    ReleaseThreatDestroyedAA(ref ecb, ref ecbCreated, aaEntity, m_AALookup[aaEntity], destroyed.Value, survivalRate);
                }

                if (ecbCreated)
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            }
        }

        /// <summary>
        /// Rebuild the building->AA index only when the AA set changed. Order version bumps on
        /// structural changes (AA create/destroy/add/remove); the count guards re-add churn.
        /// Post-load building-ref rebind is value-only (no order bump) but is covered by
        /// m_AAIndexLastCount = -1 (OnCreate + ResetState) forcing one rebuild after the boundary.
        /// </summary>
        private void RefreshAAIndexIfNeeded()
        {
            int currentCount = m_AAQuery.CalculateEntityCountWithoutFiltering();
            int currentOrderVersion = m_AAQuery.GetCombinedComponentOrderVersion(includeEntityType: true);
            if (currentCount == m_AAIndexLastCount && currentOrderVersion == m_AAIndexLastOrderVersion)
                return;

            m_AAIndexLastCount = currentCount;
            m_AAIndexLastOrderVersion = currentOrderVersion;
            m_AAByBuilding.Clear();
            if (currentCount <= 0)
                return;
            if (m_AAByBuilding.Capacity < currentCount)
                m_AAByBuilding.Capacity = currentCount;

            foreach (var (aa, aaEntity) in
                SystemAPI.Query<RefRO<AirDefenseInstallation>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                m_AAByBuilding[aa.ValueRO.Building.Packed] = aaEntity;
            }
        }

        private void ReleaseThreatDestroyedAA(ref EntityCommandBuffer ecb, ref bool ecbCreated,
            Entity aaEntity, AirDefenseInstallation aa, float3 destructionPosition, float survivalRate)
        {
            if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }

            if (aa.CrewAssigned > 0)
            {
                // XSTEP: crew casualty split routes through Core CrewMath — the survivor/KIA partition is the
                // same transition Mobilization's committed accumulator consumes; inline rounding here would fork it.
                var (survivors, casualties) = CrewMath.ReleaseSplit(aa.CrewAssigned, survivalRate);
                if (survivors > 0) IncrementEcbCount(AACrewRequests.CreateReleaseRequest(ecb, survivors, aa.Type, aaEntity));
                if (casualties > 0) IncrementEcbCount(AACrewRequests.CreateCasualtyRequest(ecb, casualties, aa.Type, aaEntity));

                EventBus?.SafePublish(new ThreatNarrativeEvent(ThreatNarrativeEventType.AAInstallationLost, Position: destructionPosition), "AACrewReleaseSystem");
                Log.Info($"{aa.Type} destroyed by threat: {survivors} survivors, {casualties} KIA");
            }
            m_StateSystem.RecordUiStatsInstallationRemoved(in aa);
            ecb.AddComponent<Deleted>(aaEntity);
            IncrementEcbCount();
        }

        // R9-L8+L10: Reset static counters on save/load
        public void ResetState()
        {
            ResetCounters();
            if (m_BuildingsDestroyedByThreat.IsCreated) m_BuildingsDestroyedByThreat.Clear();
            // Force index rebuild after the load/world boundary (building refs may be rebound
            // value-only, which does not bump the order version).
            if (m_AAByBuilding.IsCreated) m_AAByBuilding.Clear();
            m_AAIndexLastCount = -1;
            m_AAIndexLastOrderVersion = 0;
            // Boundary reset = treat as entering a load window. Gate stays RAISED until
            // PLVS Phase 2 ValidateAfterLoad clears it (guaranteed after any OnGameLoaded,
            // incl. new game / incompatible save). Setting false here could open the gate
            // mid-load-window — exactly the stale-ref release race this gate prevents.
            m_LoadGate = true;
            Log.Info("[AA-GATE] ResetState: load gate RAISED (boundary)");
        }

        protected override void OnDestroy()
        {
            if (m_BuildingsDestroyedByThreat.IsCreated) m_BuildingsDestroyedByThreat.Dispose();
            if (m_AAByBuilding.IsCreated) m_AAByBuilding.Dispose();
            base.OnDestroy();
            Log.Info("Destroyed");
        }
    }
}
