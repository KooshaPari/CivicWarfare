using System;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Diagnostics;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Waves.Systems
{
    /// <summary>
    /// Caches building targets for <see cref="ThreatSpawnSystem"/> off the
    /// per-tick hot path. Producer system owns
    /// the queries, consumer reads via <see cref="IThreatTargetSource"/>
    /// without ever registering a Transform-touching query of its own.
    ///
    /// THE KEY: queries are created via <see cref="EntityManager.CreateEntityQuery"/>
    /// rather than <see cref="ComponentSystemBase.GetEntityQuery"/>. The
    /// former does NOT register the query against this system's
    /// <c>Dependency</c> chain, so Unity does not run
    /// <c>CompleteDependencyBeforeRO&lt;Transform&gt;</c> before our
    /// <c>OnUpdate</c>. The expensive sync only fires inside
    /// <see cref="ToEntityArray"/> calls, which happen once per refresh
    /// (every <c>UpdateInterval</c> simulation frames) instead of every tick.
    ///
    /// Cadence: 5-second refresh. Building lists change slowly relative to
    /// threat-spawning frequency; one-cycle staleness is acceptable.
    /// </summary>
    [ActIndependent]
    public partial class ThreatTargetCacheSystem : ThrottledSystemBase, IThreatTargetSource, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ThreatTargetCache");

        private EntityQuery m_PowerPlantQuery;
        private EntityQuery m_TransformerQuery;
        private EntityQuery m_HospitalQuery;
        private EntityQuery m_WaterPumpQuery;
        private EntityQuery m_FireStationQuery;
        private EntityQuery m_PoliceStationQuery;
        private EntityQuery m_ResidentialQuery;

        // Peacetime gate (Part 2): in Calm nobody actively consumes the target catalogue,
        // so the 5-second building-query sweep is dead work. Registered singleton query —
        // reading it is not a Transform sync (auto-disposed, not a CreateEntityQuery handle).
        private EntityQuery m_WaveStateQuery;

        // Static building positions, keyed by entity identity (Index,Version). Buildings do
        // not move, so a position is read from Transform exactly once — when the building first
        // appears in a refresh (new construction, or first refresh after load). Demolished
        // buildings fall out: after a full sweep we keep only keys seen this refresh.
        // NonSerialized so a save/load does not resurrect stale identities — ValidateAfterLoad
        // clears it (CS2 reuses system instances; this Dictionary survives an in-process load).
        [System.NonSerialized] private readonly Dictionary<Entity, float3> m_TargetPositions = new();
        // Scratch set of identities seen in the current refresh, used to evict demolished
        // buildings from m_TargetPositions after all seven sets are processed.
        [System.NonSerialized] private readonly HashSet<Entity> m_SeenThisRefresh = new();
        // Pre-allocated scratch for the eviction pass — a Dictionary cannot be mutated while
        // enumerated, so stale keys are gathered here first. Cleared per refresh, never per frame.
        [System.NonSerialized] private readonly List<Entity> m_StaleScratch = new();

        // Weighted-targeting inputs: a plant's selection weight is its residual nameplate
        // (nameplate minus operational/disaster damage), so heavier stations attract
        // proportionally more strikes than wind turbines. Damage is read through the
        // capacity snapshot — direct OperationalDamageModifier/DisasterDamageModifier
        // probes are reserved for the PowerCapacityPipeline (CIVIC451).
        // Process-lifetime service handle, lazy-resolved at the refresh site — not save state.
        [System.NonSerialized] private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;
        // Render-write barrier: main-thread Transform reads on building entities go through a
        // BuildingTransform ticket. Zero civic producers write building Transform out-of-band, so
        // Consume(BuildingTransform) completes instantly — it does NOT drain the in-flight threat
        // movement jobs (those write ThreatTransform on a disjoint archetype). Process-lifetime
        // service handle, lazy-resolved at the refresh site — not save state.
        [System.NonSerialized] private IRenderWriteBarrier? m_RenderWriteBarrier;
        // Cached Transform lookup for the rare new-building path. .Update(this) is called ONLY
        // when a building is missing from the position cache — that .Update is the single place
        // Transform is fenced, so a refresh with no new buildings never drains a Transform writer.
        private ComponentLookup<Transform> m_TransformLookup;
        // Entity → residual nameplate (MW, min 1); rebuilt from the snapshot each refresh.
        private readonly Dictionary<Entity, int> m_PlantResidualMW = new();

        private NativeList<TargetData> m_Energy;
        private NativeList<TargetData> m_Critical;
        private NativeList<TargetData> m_Service;
        private NativeList<TargetData> m_Civilian;

        private bool m_IsReady;

        public bool IsReady => m_IsReady;

        public NativeArray<TargetData>.ReadOnly Energy => m_Energy.AsArray().AsReadOnly();
        public NativeArray<TargetData>.ReadOnly Critical => m_Critical.AsArray().AsReadOnly();
        public NativeArray<TargetData>.ReadOnly Service => m_Service.AsArray().AsReadOnly();
        public NativeArray<TargetData>.ReadOnly Civilian => m_Civilian.AsArray().AsReadOnly();

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_5_SECONDS;

        /// <summary>
        /// Unconditional rebuild requested by the spawner immediately before a wave reads the
        /// catalogue, so the peacetime gate's last (stale) Calm-time snapshot is replaced with
        /// fresh building positions for the wave about to launch. Doubles as the cold-start build
        /// (it also marks <see cref="m_IsReady"/>), so no separate ensure-ready entry point is needed.
        /// </summary>
        public void ForceRefreshForWave() => RefreshTargets();

        protected override bool ShouldSkipUpdate()
        {
            // PERF-LOCK: peacetime gate — in Calm nobody actively consumes the target catalogue
            // (spawn forces ForceRefreshForWave before reading; scale-down is resilient to
            // IsReady==false). Without the gate the seven building queries fire a Transform sync
            // once every 5 s in peacetime (~the whole weight of the Waves domain at idle).
            // Removing this restores idle sync in Calm.
            if (!m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var wave))
                return false; // singleton not created yet (early boot) — do not gate
            return wave.CurrentPhase == GamePhase.Calm;
        }

        public void ValidateAfterLoad()
        {
            ResetTransientRuntimeStateAfterLoad();
        }

        private void ResetTransientRuntimeStateAfterLoad()
        {
            if (m_Energy.IsCreated) m_Energy.Clear();
            if (m_Critical.IsCreated) m_Critical.Clear();
            if (m_Service.IsCreated) m_Service.Clear();
            if (m_Civilian.IsCreated) m_Civilian.Clear();
            m_PlantResidualMW.Clear();
            // CRITICAL: cached positions are keyed by (Index,Version); after load those keys
            // point at DIFFERENT entities. Without this clear the first post-load refresh would
            // hand drones stale coordinates for whatever now occupies the recycled slots.
            m_TargetPositions.Clear();
            m_SeenThisRefresh.Clear();
            m_StaleScratch.Clear();
            m_IsReady = false;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PowerPlantQuery = BuildBuildingQuery<ElectricityProducer>();
            m_TransformerQuery = BuildBuildingQuery<Transformer>();
            m_HospitalQuery = BuildBuildingQuery<Hospital>();
            m_WaterPumpQuery = BuildBuildingQuery<WaterPumpingStation>();
            m_FireStationQuery = BuildBuildingQuery<FireStation>();
            m_PoliceStationQuery = BuildBuildingQuery<PoliceStation>();
            m_ResidentialQuery = BuildBuildingQuery<ResidentialProperty>();

            // Registered singleton query for the peacetime gate. GetEntityQuery (not
            // CreateEntityQuery) — a singleton read carries no Transform sync, and the
            // registered handle is auto-disposed, so it must NOT be disposed in OnDestroy.
            m_WaveStateQuery = GetEntityQuery(ComponentType.ReadOnly<WaveStateSingleton>());

            m_TransformLookup = GetComponentLookup<Transform>(true);

            // Initial capacities mirror the previous List<TargetData> sizing in
            // ThreatTargetSelector (Energy 128, Critical/Service 64, Civilian 512).
            m_Energy = new NativeList<TargetData>(128, Allocator.Persistent);
            m_Critical = new NativeList<TargetData>(64, Allocator.Persistent);
            m_Service = new NativeList<TargetData>(64, Allocator.Persistent);
            m_Civilian = new NativeList<TargetData>(512, Allocator.Persistent);

#pragma warning disable CIVIC098 // ServiceRegistry.Instance is initialized in Mod.OnLoad before any system OnCreate runs
            ServiceRegistry.Instance.Register<IThreatTargetSource>(this);
#pragma warning restore CIVIC098
            Log.Info("Created — registered as IThreatTargetSource");
        }

        /// <summary>
        /// Builds an unregistered building+marker query. Uses
        /// <see cref="EntityManager.CreateEntityQuery"/> so the resulting
        /// query is not added to this system's <c>Dependency</c> chain —
        /// avoiding a per-tick <c>CompleteDependencyBeforeRO</c> on
        /// <c>Transform</c>.
        /// </summary>
        private EntityQuery BuildBuildingQuery<TMarker>() where TMarker : unmanaged, IComponentData
        {
            return EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<TMarker>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Destroyed>(),
                ComponentType.Exclude<Deleted>()
            );
        }

        protected override void OnThrottledUpdate()
        {
            RefreshTargets();
        }

        private void RefreshTargets()
        {
            RefreshPlantResidualMap();

            m_Energy.Clear();
            m_Critical.Clear();
            m_Service.Clear();
            m_Civilian.Clear();
            m_SeenThisRefresh.Clear();

            // One BuildingTransform ticket for the whole refresh. Zero civic producers write
            // building Transform out-of-band, so this completes instantly and does NOT drain the
            // in-flight threat-movement jobs (those write ThreatTransform on a disjoint archetype).
            m_RenderWriteBarrier ??= ServiceRegistry.Instance.Require<IRenderWriteBarrier>();
            RenderWriteTicket renderTicket = m_RenderWriteBarrier.Consume(GetType(), RenderWriteComponentMask.BuildingTransform);

            FillFromQuery(m_PowerPlantQuery, TargetCategory.Energy, m_Energy, renderTicket, isHighValue: true);
            FillFromQuery(m_TransformerQuery, TargetCategory.Energy, m_Energy, renderTicket);
            FillFromQuery(m_HospitalQuery, TargetCategory.Critical, m_Critical, renderTicket);
            FillFromQuery(m_WaterPumpQuery, TargetCategory.Critical, m_Critical, renderTicket);
            FillFromQuery(m_FireStationQuery, TargetCategory.Service, m_Service, renderTicket);
            FillFromQuery(m_PoliceStationQuery, TargetCategory.Service, m_Service, renderTicket);
            FillFromQuery(m_ResidentialQuery, TargetCategory.Civilian, m_Civilian, renderTicket);

            EvictDemolishedPositions();

            if (!m_IsReady)
            {
                m_IsReady = true;
                Log.Info($"First refresh: Energy={m_Energy.Length}, Critical={m_Critical.Length}, Service={m_Service.Length}, Civilian={m_Civilian.Length}");
            }

            // Footprint diagnostic: the four target lists never shrink (Clear keeps Capacity) and
            // scale with city building count — main-thread .Capacity reads, no sync point.
            NativeFootprintTracker.ReportTargetCache(
                (long)(m_Energy.Capacity + m_Critical.Capacity + m_Service.Capacity + m_Civilian.Capacity)
                * UnsafeUtility.SizeOf<TargetData>());
        }

        [CompletesDependency("FillFromQuery: throttled (5 s) producer for IThreatTargetSource; ToEntityArray is a structural barrier only (does NOT complete Transform writers) — Transform is read solely for buildings missing from the position cache, inside the ticket-typed TryResolveStaticPosition")]
        private void FillFromQuery(EntityQuery query, TargetCategory category, NativeList<TargetData> dest, RenderWriteTicket renderTicket, bool isHighValue = false)
        {
            if (query.IsEmpty) return;

            int nonPlantWeightMW = math.max(BalanceConfig.Current.Waves.NonPlantTargetWeightMW, 1);

            // ToEntityArray is a structural barrier only — it does NOT complete Transform
            // writers, so the in-flight drone/missile movement jobs keep running.
            var entities = query.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    m_SeenThisRefresh.Add(entity);

                    if (!TryResolveStaticPosition(entity, renderTicket, out float3 position))
                        continue; // missing Transform — already warned; do not aim a drone at (0,0,0)

                    dest.Add(new TargetData
                    {
                        Entity = entity,
                        Position = position,
                        Category = category,
                        IsHighValue = isHighValue,
                        // Only the power-plant query is marked high-value; everything else
                        // (transformers included) carries the flat non-plant weight.
                        WeightMW = isHighValue
                            ? ComputePlantWeightMW(entity, nonPlantWeightMW)
                            : nonPlantWeightMW,
                    });
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }
        }

        /// <summary>
        /// Static building position from the cache, reading <see cref="Transform"/> only for a
        /// building absent from the cache (new construction, or first refresh after load). A cache
        /// hit costs nothing; a miss is the only path that touches a Transform writer, and only for
        /// the missing entity rather than the whole query. The <see cref="RenderWriteTicket"/> proves
        /// the BuildingTransform barrier was consumed before the main-thread read (CIVIC445).
        /// </summary>
        private bool TryResolveStaticPosition(Entity entity, RenderWriteTicket renderTicket, out float3 position)
        {
            if (m_TargetPositions.TryGetValue(entity, out position))
                return true;

            if (!renderTicket.Covers(RenderWriteComponentMask.BuildingTransform))
                throw new InvalidOperationException("Render write ticket does not cover BuildingTransform");

            // PERF-LOCK: fence Transform only for buildings missing from the position cache
            // (new/post-load); static building positions are cached, fencing every refresh
            // re-drains in-flight drone Transform writers during a wave. .Update(this) is the sole
            // Transform fence and runs only on a miss (rare), so a refresh with no new buildings
            // never drains a Transform writer.
            m_TransformLookup.Update(this);

            if (!m_TransformLookup.TryGetComponent(entity, out Transform transform))
            {
                Log.Warn($"Target building {entity.Index}:{entity.Version} has no Transform — skipping");
                position = default;
                return false;
            }

            position = transform.m_Position;
            m_TargetPositions[entity] = position;
            return true;
        }

        /// <summary>
        /// Drops cached positions for buildings not seen in the just-completed refresh
        /// (demolished or otherwise no longer matching any building query), so the cache
        /// tracks the live identity set instead of growing without bound.
        /// </summary>
        private void EvictDemolishedPositions()
        {
            if (m_TargetPositions.Count == m_SeenThisRefresh.Count)
                return; // every cached key was seen — nothing to evict

            // Collect stale keys first; mutating a Dictionary while enumerating it throws.
            m_StaleScratch.Clear();
            foreach (var key in m_TargetPositions.Keys)
            {
                if (!m_SeenThisRefresh.Contains(key))
                    m_StaleScratch.Add(key);
            }
            for (int i = 0; i < m_StaleScratch.Count; i++)
                m_TargetPositions.Remove(m_StaleScratch[i]);
        }

        /// <summary>
        /// Rebuilds the Entity → residual-nameplate map from the power capacity snapshot:
        /// per plant, nameplate (kW, mirrors live <c>PlantBaseCapacity</c> incl. installed
        /// upgrades) scaled by what missile/disaster damage has left of it, in MW, minimum 1 —
        /// a near-dead station stays selectable but barely attractive. The snapshot is the
        /// sanctioned cross-domain read path for plant damage (modifier components are
        /// pipeline-internal, CIVIC451).
        /// </summary>
        private void RefreshPlantResidualMap()
        {
            const float KW_PER_MW = 1000f;

            m_PlantResidualMW.Clear();

            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            if (!m_PowerCapacitySnapshotReader.TryGetSnapshot(out var snapshot))
                return;

            var plants = snapshot.Plants;
            for (int i = 0; i < plants.Count; i++)
            {
                var plant = plants[i];
                if (plant.OriginalCapacityKW <= 0)
                    continue;
                float residualFactor = math.clamp(
                    1f - plant.OperationalDamagePercent - plant.DisasterDamagePercent, 0f, 1f);
                int residualMW = (int)math.round(plant.OriginalCapacityKW / KW_PER_MW * residualFactor);
                m_PlantResidualMW[plant.Plant] = math.max(residualMW, 1);
            }
        }

        /// <summary>
        /// Residual nameplate weight of a power plant in MW (see <see cref="RefreshPlantResidualMap"/>).
        /// Falls back to the non-plant weight when the plant is not in the snapshot yet
        /// (first frames after construction, or no snapshot published on a fresh world).
        /// </summary>
        private int ComputePlantWeightMW(Entity plant, int fallbackWeightMW)
        {
            return m_PlantResidualMW.TryGetValue(plant, out int residualMW)
                ? residualMW
                : fallbackWeightMW;
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IThreatTargetSource>(this);

            if (m_PowerPlantQuery != default) m_PowerPlantQuery.Dispose();
            if (m_TransformerQuery != default) m_TransformerQuery.Dispose();
            if (m_HospitalQuery != default) m_HospitalQuery.Dispose();
            if (m_WaterPumpQuery != default) m_WaterPumpQuery.Dispose();
            if (m_FireStationQuery != default) m_FireStationQuery.Dispose();
            if (m_PoliceStationQuery != default) m_PoliceStationQuery.Dispose();
            if (m_ResidentialQuery != default) m_ResidentialQuery.Dispose();

            if (m_Energy.IsCreated) m_Energy.Dispose();
            if (m_Critical.IsCreated) m_Critical.Dispose();
            if (m_Service.IsCreated) m_Service.Dispose();
            if (m_Civilian.IsCreated) m_Civilian.Dispose();

            base.OnDestroy();
        }
    }
}
