using System.Collections.Generic;
using Colossal.Logging;
using CivicSurvival.Core.Features.Wellbeing;
using Colossal.Serialization.Entities;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Components.Domain.NeighborEnvy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Domains.NeighborEnvy.Data;
using CivicSurvival.Domains.NeighborEnvy.Logic;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.NeighborEnvy.Systems
{
    /// <summary>
    /// Neighbor Envy System — THIN ORCHESTRATOR
    ///
    /// Detects when blacked-out buildings have powered neighbors within 100m.
    /// Citizens in affected buildings experience wellbeing penalty.
    ///
    /// Architecture:
    /// - System: lifecycle, queries, coordination
    /// - Logic classes: pure functions for rebuild, incremental, wellbeing
    ///
    /// Trigger: dual-path update
    /// - Event-driven: DistrictStateChangedEvent → incremental update (dirty district + spatial neighbors)
    /// - Periodic: full rebuild every 5s (10 × 500ms throttle) as safety net for changes
    ///   not covered by events (new buildings, vanilla power flow, city-wide schedule changes)
    ///
    /// SERIALIZATION BEHAVIOR (FIX P0-NE-003):
    /// - PERSISTED: Only FeatureEnabled flag is saved/loaded
    /// - TRANSIENT: All spatial data (entity indices, positions, power states) is rebuilt on load
    /// - REASON: Entity.Index is unstable across save/load - cannot be reliably persisted
    /// - RECOVERY: Full rebuild triggered via NeedsFullRebuild=true in Deserialize()
    /// </summary>
    [ActIndependent]
    public partial class NeighborEnvySystem : ThrottledSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("NeighborEnvy");

        // ============================================================================
        // CONSTANTS (delegated to Logic classes)
        // ============================================================================

        // GAMEPLAY constants - accessed via BalanceConfig.Current.NeighborEnvy.*
        // ENGINE constants (CELL_SIZE, SPATIAL_HASH_PRIME_*) - stay in Balance class

        // ============================================================================
        // STATE
        // ============================================================================

        public bool FeatureEnabled { get; set; } = true;
        public int AffectedCount { get; private set; } = 0;
        public int ProcessedCount { get; private set; } = 0;

        // ============================================================================
        // ECS INFRASTRUCTURE
        // ============================================================================

        private EntityQuery m_ResidentialQuery;
        private EntityQuery m_EnvyAffectedQuery;
        private NeighborEnvyData m_EnvyData;

        // NOTE: PsyPressure writing moved to MentalHealthResolverSystem (Logic Composition pattern)
        // NeighborEnvySystem only maintains EnvyAffected tags on buildings

        // Type handles for Burst Jobs
        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<Transform> m_TransformType;
        private ComponentTypeHandle<CurrentDistrict> m_DistrictType;
        private ComponentLookup<ElectricityConsumer> m_ConsumerLookup;
        private ComponentLookup<BlackoutState> m_BlackoutStateLookup;

        // Full rebuild every 5s = 10 throttle fires at 500ms interval
        private int m_ThrottleCounter;
        private const int FULL_REBUILD_EVERY_N = 10;

        // NOTE: District penalty tracking REMOVED - happiness now via PsyPressure only

        // ASYNC REBUILD: pending state between frames
        private PendingRebuildState m_PendingRebuild;
        [System.NonSerialized] private bool m_PendingBootDefaultRebuildCleanup;

        // M2 FIX: Cache services (avoid lookup in hot path)
        private ModSettings? m_Settings;
        private IDistrictStateReader? m_DistrictState;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;

        protected override bool ShouldSkipUpdate()
        {
            return m_Settings == null || m_DistrictState == null || !(FeatureEnabled && m_Settings.NeighborEnvyEnabled);
        }

        /// <summary>
        /// FIX S7-07: Clear EnvyAffected tags when feature disabled.
        /// Prevents phantom 10% envy pressure from stale tags.
        /// </summary>
        protected override void OnBecameEnabled()
        {
            // M-32 FIX: Force full rebuild on re-enable to avoid up to 5s stale spatial data
            m_EnvyData.NeedsFullRebuild = true;
            m_ThrottleCounter = 0;
        }

        protected override void OnBecameDisabled()
        {
            // Complete and dispose pending async rebuild to prevent native memory leak
            // and stale data application on re-enable (same pattern as OnDestroy)
            if (m_PendingRebuild.IsValid)
            {
                m_PendingRebuild.FinalJobHandle.Complete();
                m_PendingRebuild.DisposeBuffers();
                m_PendingRebuild = default;
            }

            ClearAllEnvyTags();
        }

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            if (SingletonRegistry.IsInitialized)
                SingletonRegistry.Instance.Register<NeighborEnvySystem>(this, "NeighborEnvySystem.OnCreate");

            // BUG-NE-002: Reset static state to prevent cross-session persistence
            FeatureEnabled = true;
            AffectedCount = 0;
            ProcessedCount = 0;

            // No self-registration: accessed via World.GetExistingSystemManaged<NeighborEnvySystem>()

            PressureRegistry.RegisterProducer(PressureChannel.Envy, nameof(NeighborEnvySystem));

            Log.Info($"{nameof(NeighborEnvySystem)} created (THIN ORCHESTRATOR)");

            // Query: Residential buildings with electricity.
            // Temp excluded: tool previews carry Building/ResidentialProperty and must not be
            // registered in the spatial grid (they would feed phantom power states into
            // HasPoweredNeighbor) — matches EnvyAffectedSetupSystem's seed query, same precedent
            // as the phantom-plant Exclude<Temp> fix.
            m_ResidentialQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<ResidentialProperty>(),
                    ComponentType.ReadOnly<ElectricityConsumer>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<CurrentDistrict>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // Query: Buildings with EnvyAffected tag (counts only ENABLED tags — drives AffectedCount)
            m_EnvyAffectedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<EnvyAffected>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            // Native containers
            m_EnvyData = NeighborEnvyData.Create(Engine.DataStructures.LARGE_CAPACITY);

            // Type handles
            m_EntityType = GetEntityTypeHandle();
            m_TransformType = GetComponentTypeHandle<Transform>(true);
            m_DistrictType = GetComponentTypeHandle<CurrentDistrict>(true);
            m_ConsumerLookup = GetComponentLookup<ElectricityConsumer>(true);
            m_BlackoutStateLookup = GetComponentLookup<BlackoutState>(true);

            // Subscribe to district changes
            SubscribeRequired<DistrictStateChangedEvent>(OnDistrictStateChanged);

            // NOTE: m_PenaltyRequestQuery REMOVED - no longer registering district penalties
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_DistrictState ??= ServiceRegistry.Instance.Require<IDistrictStateReader>();
        }

        [CompletesDependency("OnThrottledUpdate envy stats publish: AffectedCount read after rebuild/incremental sweep to feed UI; CalculateEntityCount runs once per throttle tick on bounded m_EnvyAffectedQuery, sync amortised over throttle interval")]
        protected override void OnThrottledUpdate()
        {
            // ================================================================
            // STEP 1: Complete pending async rebuild from previous frame
            // ================================================================
            if (m_PendingRebuild.IsValid)
            {
                // Save flag before complete — OnDistrictStateChanged may have set it
                // between schedule (frame N) and now (frame N+1)
                bool rebuildRequestedDuringAsync = m_EnvyData.NeedsFullRebuild;

                var (_, processed) = EnvyRebuildLogic.CompleteRebuild(
                    ref m_PendingRebuild,
                    ref m_EnvyData,
                    EntityManager
                );

                AffectedCount = m_EnvyAffectedQuery.CalculateEntityCount();
                ProcessedCount = processed;

                // Only clear if no new rebuild was requested during async period
                m_EnvyData.NeedsFullRebuild = rebuildRequestedDuringAsync;
                m_ThrottleCounter = 0;

                // Dirty districts are cleared before scheduling a rebuild. Any entries present now
                // arrived while the async snapshot was in flight and must run incrementally below.
            }

            m_ThrottleCounter++;

            // ================================================================
            // STEP 2: Check if we need to schedule new rebuild
            // ================================================================
            bool needsFullRebuild = m_EnvyData.NeedsFullRebuild ||
                                    (m_ThrottleCounter >= FULL_REBUILD_EVERY_N);
            bool hasDirtyDistricts = m_EnvyData.HasDirtyDistricts;

            m_ConsumerLookup.Update(this);
            m_BlackoutStateLookup.Update(this);

            // ================================================================
            // STEP 3: Schedule async rebuild OR do incremental update
            // ================================================================
            if (needsFullRebuild && !m_PendingRebuild.IsValid)
            {
                // Update type handles for collection job
                m_EntityType.Update(this);
                m_TransformType.Update(this);
                m_DistrictType.Update(this);

                // Clear flag BEFORE scheduling — only new requests during async should re-set it
                m_EnvyData.NeedsFullRebuild = false;

                // Schedule rebuild (non-blocking) - results available next frame.
                // Dependency goes in so the chunk-reading collect job waits for this frame's
                // vanilla Transform/CurrentDistrict writers instead of racing them.
                m_PendingRebuild = EnvyRebuildLogic.ScheduleRebuild(
                    m_ResidentialQuery,
                    m_EntityType,
                    m_TransformType,
                    m_DistrictType,
                    m_ConsumerLookup,
                    m_BlackoutStateLookup,
                    BalanceConfig.Current.PowerGrid.GridPowerThreshold,
                    Dependency
                );
                // If no buildings exist, ScheduleRebuild returns default (IsValid=false).
                // Reset throttle counter so we don't retry every frame (would loop until buildings appear).
                if (!m_PendingRebuild.IsValid)
                {
                    m_ThrottleCounter = 0;
                    m_EnvyData.NeedsFullRebuild = true;
                }
                else
                {
                    m_EnvyData.ClearDirtyDistricts();

                    // SAFETY-LOCK: register the chunk-reading collect job with the ECS dependency
                    // manager (mirror of ThreatMovementSystem.PublishBuildingCacheReadDependency).
                    // (1) AddDependency — a structural change to residential chunks now completes
                    // the worker instead of migrating/freeing the chunk under it (the UAF class
                    // behind the cross-version wave/calm AVs — NEIGHBORENVY_FENCE_FIX_PLAN.md);
                    // (2) fold into Dependency — the engine's load purge completes only
                    // Dependency-registered handles (precedent: ResidentPopulationModelSystem's
                    // flatten-job comment). Only the collect handle is folded — phases 2-5 run on
                    // copied Persistent arrays and stay off the ECS graph (async pattern intact).
                    m_ResidentialQuery.AddDependency(m_PendingRebuild.CollectJobHandle);
                    Dependency = JobHandle.CombineDependencies(Dependency, m_PendingRebuild.CollectJobHandle);
                }
            }
            else if (hasDirtyDistricts && !needsFullRebuild)
            {
                // FIX S7-03: Expand dirty set with spatially adjacent districts (single O(N) pass)
                m_EnvyData.ExpandDirtyDistrictsWithNeighbors();

                // Incremental update (sync, fast)
                var (_, processed) = EnvyIncrementalLogic.Execute(
                    ref m_EnvyData,
                    EntityManager
                );

                AffectedCount = m_EnvyAffectedQuery.CalculateEntityCount();
                ProcessedCount = processed;

                // H1 FIX: Reset throttle counter after incremental — prevents periodic rebuild
                // from swallowing dirty districts on the next tick by coincidence
                m_ThrottleCounter = 0;
            }

            // NOTE: STEP 4 (WritePressureJob) REMOVED
            // PsyPressure.Envy now written by MentalHealthResolverSystem via EnvyCalculator
            // This system only maintains EnvyAffected tags on buildings
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<DistrictStateChangedEvent>(OnDistrictStateChanged);
            PressureRegistry.DeregisterProducer(PressureChannel.Envy, nameof(NeighborEnvySystem));
            Log.Info($"{nameof(NeighborEnvySystem)} destroyed");

            // Dispose pending async rebuild if any
            if (m_PendingRebuild.IsValid)
            {
                m_PendingRebuild.FinalJobHandle.Complete();
                m_PendingRebuild.DisposeBuffers();
            }

            m_EnvyData.Dispose();

            if (SingletonRegistry.IsInitialized)
            {
                SingletonRegistry.Instance.Unregister<NeighborEnvySystem>();
            }

            base.OnDestroy();
        }

        /// <summary>
        /// FIX S7-07: Disable all EnvyAffected tags when feature is toggled off.
        /// Prevents phantom 10% envy pressure from stale tags.
        /// </summary>
        private void ClearAllEnvyTags()
        {
            int cleared = 0;
            foreach (var (_, entity) in SystemAPI.Query<RefRO<EnvyAffected>>().WithEntityAccess())
            {
                EntityManager.SetComponentEnabled<EnvyAffected>(entity, false);
                cleared++;
            }
            AffectedCount = 0;
            if (cleared > 0)
                Log.Info($"Cleared {cleared} EnvyAffected tags (feature disabled)");
        }

        // ============================================================================
        // EVENT HANDLERS
        // ============================================================================

        private void OnDistrictStateChanged(DistrictStateChangedEvent evt)
        {
            ForceNextUpdate();

            // No spatial data yet (first run or after deserialize) — fallback to full rebuild
            if (m_EnvyData.BuildingPositions.Count() == 0)
            {
                m_EnvyData.NeedsFullRebuild = true;
            }
            else
            {
                // O(1) — neighbor expansion deferred to OnThrottledUpdate (single batched pass)
                m_EnvyData.MarkDistrictDirty(evt.DistrictIndex);
            }

            if (ShouldSkipUpdate())
            {
                if (Log.IsDebugEnabled) Log.Debug($"[NeighborEnvy] District {evt.DistrictIndex} changed while system is inactive — state marked for next enabled tick");
                return;
            }

            ForceNextUpdate();
            if (Log.IsDebugEnabled) Log.Debug($"[NeighborEnvy] District {evt.DistrictIndex} changed — {(m_EnvyData.NeedsFullRebuild ? "full rebuild" : "incremental")} scheduled");
        }

        // NOTE: SyncDistrictPenalties and ClearAllDistrictPenalties REMOVED
        // Happiness penalties now via PsyPressure → WellbeingResolverSystem only

    }
}
