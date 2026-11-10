using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using LogContext = CivicSurvival.Core.Utils.LogContext;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// UI System for Equipment Wear data.
    /// Provides read-only access to plant wear status for UI panels.
    ///
    /// Separated from PlantWearSimulation (simulation) for SRP:
    /// - PlantWearSimulation: updates wear, hands off explosions to PlantExplosionService
    /// - PlantRepairRequestProcessor: drains resolved repair-budget results
    /// - EquipmentUISystem: caches and serves data for UI consumption
    ///
    /// THREAD-SAFE: Uses double-buffer pattern for UI reads.
    /// LAZY: Data refreshed only when requested after being marked dirty.
    ///
    /// Implements IEquipmentUIService for cross-domain access via ServiceRegistry.
    ///
    /// Upgrade-delta construction (delayed ramp-up of capacity added by a building upgrade) is
    /// modelled via the UnderConstruction sidecar, not a dedicated PlantState. An upgrading plant
    /// reports PlantState.UnderConstruction while its delta ramps; there is no separate Upgrading state.
    /// </summary>
    [ActIndependent]
    public partial class EquipmentUISystem : ThrottledSystemBase, IEquipmentUIService, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("EquipmentUISystem");
        private static readonly Comparison<PlantWearProducerData> s_DamageDescending = (a, b) =>
        {
            float totalA = a.WearPercent + a.OperationalDamagePercent + a.DisasterDamagePercent;
            float totalB = b.WearPercent + b.OperationalDamagePercent + b.DisasterDamagePercent;
            return totalB.CompareTo(totalA);
        };

        // PERF: UI data — 1Hz sufficient, was 60fps causing 76ms/frame sync point regression
        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        private NameSystem m_NameSystem = null!;
        private PrefabSystem m_PrefabSystem = null!;
        private ModSettings? m_Settings;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private EntityStorageInfoLookup m_EntityStorageInfoLookup;
        private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;

        // PERF: chunk iteration for plant query (avoids SystemAPI.Query dependency tracker leak — 30ms spikes)
        private EntityQuery m_PlantQuery;
        private EntityQuery m_EquipmentWearQuery;
        private EntityQuery m_UnderConstructionQuery;
        private ComponentTypeHandle<ElectricityProducer> m_ElectricityProducerTypeHandle;
        private ComponentTypeHandle<EquipmentWear> m_EquipmentWearTypeHandle;
        private ComponentTypeHandle<UnderConstruction> m_UnderConstructionTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;
        [System.NonSerialized] private CivicServiceLookups m_ServiceRefreshLookups = null!;

        // THREAD-SAFE: Double buffer for UI data
        private List<PlantWearProducerData> m_FrontBuffer = new();
        private List<PlantWearProducerData> m_BackBuffer = new();
        private readonly object m_BufferLock = new();

        // Version counter — incremented on each buffer swap so consumers can skip re-serialization
        [System.NonSerialized] private int m_PlantsVersion;
        public int PlantsVersion
        {
            get
            {
                RefreshIfMarkedDirty();
                Touch();
                return System.Threading.Volatile.Read(ref m_PlantsVersion);
            }
        }

        // PERF: Demand-driven refresh — skip 26-48ms SystemAPI.Query work when nobody reads the data.
        // Set true by GetPlantsSnapshot/Touch, consumed by OnThrottledUpdate.
        // Ephemeral flag — no serialization needed. The system instance is reused across loads,
        // so this field is NOT reset on load (the field initializer only runs once per process).
        // First-refresh after load is instead guaranteed unconditionally by ValidateAfterLoad's seed.
#pragma warning disable CIVIC150 // Ephemeral demand flag — not game state, intentionally not serialized
        private int m_DataRequested = 1; // 1 on first run to populate initial data
#pragma warning restore CIVIC150
        private int m_PlantsDirty;

        // Pre-allocated lookup maps (cleared each frame to avoid GC).
        // Key includes entity version so reused BuildingIndex cannot bind stale mod data.
        private readonly Dictionary<long, EquipmentWear> m_WearByBuilding = new();
        private readonly Dictionary<long, UnderConstruction> m_ConstructionByBuilding = new();
        private readonly Dictionary<long, PowerCapacityPlantSnapshot> m_CapacityByBuilding = new();

        // RefreshCachedData runs in OnThrottledUpdate (1Hz) in correct ECS context.
        // Previously lazy from GetPlantsSnapshot → wrong system context for SystemAPI.Query.

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_EntityStorageInfoLookup = GetEntityStorageInfoLookup();

            // PERF: chunk iteration for plant query (avoids SystemAPI.Query dependency tracker leak)
            m_PlantQuery = GetEntityQuery(
                ComponentType.ReadOnly<ElectricityProducer>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Game.Net.OutsideConnection>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()
            );
            m_EquipmentWearQuery = GetEntityQuery(
                ComponentType.ReadOnly<EquipmentWear>(),
                ComponentType.Exclude<Deleted>()
            );
            m_UnderConstructionQuery = GetEntityQuery(
                ComponentType.ReadOnly<UnderConstruction>(),
                ComponentType.Exclude<Deleted>()
            );
            m_ElectricityProducerTypeHandle = GetComponentTypeHandle<ElectricityProducer>(true);
            m_EquipmentWearTypeHandle = GetComponentTypeHandle<EquipmentWear>(true);
            m_UnderConstructionTypeHandle = GetComponentTypeHandle<UnderConstruction>(true);

            m_EntityTypeHandle = GetEntityTypeHandle();
            m_ServiceRefreshLookups = new CivicServiceLookups(() =>
            {
                m_PrefabRefLookup.Update(this);
                m_EntityStorageInfoLookup.Update(this);
                m_ElectricityProducerTypeHandle.Update(this);
                m_EquipmentWearTypeHandle.Update(this);
                m_UnderConstructionTypeHandle.Update(this);
                m_EntityTypeHandle.Update(this);
            });

            // Register in ServiceRegistry for cross-domain access
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IEquipmentUIService>(this);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IEquipmentUIService>(this);
            base.OnDestroy();
        }

        protected override void OnThrottledUpdate()
        {
            bool wearEnabled = m_Settings == null || m_Settings.EquipmentWearEnabled;

            // PERF: Demand-driven — only refresh if someone read the data since last update.
            // Skips 26-48ms of SystemAPI.Query sync points when PowerGrid UI panel is closed.
            if (System.Threading.Interlocked.Exchange(ref m_DataRequested, 0) == 0)
                return;

            RefreshCachedData(wearEnabled);
        }

        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_READER;

        // PowerGridDataSystem.ValidateAfterLoad calls AggregateSnapshot so the UI sees fresh
        // Production/Demand on the first post-load frame even on pause. EquipmentUISystem
        // owes the same: PowerGridUISystem.OnPanelUpdate reads GenerationSourcesJson off
        // m_LastGenerationSourcesJson, which only fills when RefreshCachedData populates
        // FrontBuffer. Without a post-load seed the buffer stays empty until the demand cycle
        // breaks (PowerGridUI Touch → next throttle tick), which on a paused or low-FPS load
        // never lands in time — INFRA panel shows 0 plants / 0 MW indefinitely. Mirror the
        // PowerGridDataSystem pattern: rebuild the snapshot immediately.
        public void ValidateAfterLoad()
        {
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            bool wearEnabled = m_Settings == null || m_Settings.EquipmentWearEnabled;
            RefreshCachedData(wearEnabled);
        }

        public void Touch()
        {
            System.Threading.Interlocked.Exchange(ref m_DataRequested, 1);
        }

        public void MarkPlantsDirty()
        {
            System.Threading.Interlocked.Exchange(ref m_PlantsDirty, 1);
            System.Threading.Interlocked.Exchange(ref m_DataRequested, 1);
        }

        /// <summary>
        /// Get snapshot of all plants with wear data.
        /// THREAD-SAFE: Returns front buffer copy, data refreshed in OnUpdateImpl.
        /// </summary>
        public IReadOnlyList<PlantWearProducerData> GetPlantsSnapshot()
        {
            m_ServiceRefreshLookups.RefreshIfStale();
            RefreshIfMarkedDirty();
            Touch();
            lock (m_BufferLock)
            {
                return new List<PlantWearProducerData>(m_FrontBuffer);
            }
        }

#pragma warning disable CIVIC433 // Pause-safe repair commit explicitly forces this service-boundary refresh before returning a UI snapshot.
        private void RefreshIfMarkedDirty()
        {
            if (System.Threading.Interlocked.Exchange(ref m_PlantsDirty, 0) == 0)
                return;

            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            m_ServiceRefreshLookups.RefreshIfStale();
            bool wearEnabled = m_Settings == null || m_Settings.EquipmentWearEnabled;
            RefreshCachedData(wearEnabled);
            System.Threading.Interlocked.Exchange(ref m_DataRequested, 0);
        }
#pragma warning restore CIVIC433

        /// <summary>
        /// Refresh cached data using double-buffer pattern.
        /// THREAD-SAFE: Writes to back buffer, swaps atomically.
        /// </summary>
        private void RefreshCachedData(bool wearEnabled)
        {
            m_BackBuffer.Clear();

            // Get current game hour
            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider == null) { Log.Error("[EquipmentUISystem] TimeProvider unavailable"); return; }
            float gameHour = timeProvider.Current.TotalGameHours;

            // Config cache (CIVIC347: read BalanceConfig.Current once per refresh)
            var config = BalanceConfig.Current;
            int minCapacityKw = config.Engineering.SmallPlantCapacityKw;
            // Missile damage is discrete and NAMEPLATE-SCALED: the per-hit fraction depends on
            // each plant's live nameplate (PlantHitMath), so the step/ceiling are per-plant and
            // derived inside the loop — only the config knobs are resolved once here.
            var repairCfg = config.Repair;
            // Fleet-share hit slice — same formula and fleet nameplate source as
            // OperationalDamageSystem's accrual, so the "N/M hits" counter can never drift
            // from the actual damage. No snapshot yet → fall back to the absolute slice.
            int fleetNameplateKW = m_PowerCapacitySnapshotReader != null
                && m_PowerCapacitySnapshotReader.TryGetSnapshot(out var fleetSnapshot)
                ? fleetSnapshot.NameplateKW
                : 0;
            float hitSliceMW = PlantHitMath.EffectiveHitSliceMW(repairCfg.HitDamageMW, repairCfg.HitFleetSharePercent, fleetNameplateKW);
            float currentGameDay = gameHour / GameRate.HOURS_PER_DAY;

            // Build map of EquipmentWear by building entity identity (mod entities use separate Entity)
            m_WearByBuilding.Clear();
            using (Core.Utils.PerformanceProfiler.MeasureDebug("SP:EqUI.LookupSync"))
            {
                m_EntityStorageInfoLookup.Update(this);
            }
            // S13b-2 FIX: Filter out Deleted entities (same as UnderConstruction query below)
            using (Core.Utils.PerformanceProfiler.MeasureDebug("EqUI.WearQuery"))
            {
                m_EquipmentWearTypeHandle.Update(this);
                using var wearChunks = m_EquipmentWearQuery.ToArchetypeChunkArray(Allocator.Temp);
                for (int ci = 0; ci < wearChunks.Length; ci++)
                {
                    var wears = wearChunks[ci].GetNativeArray(ref m_EquipmentWearTypeHandle);
                    for (int i = 0; i < wearChunks[ci].Count; i++)
                    {
                        var wear = wears[i];
                        var buildingEntity = wear.GetBuildingEntity();
                        if (!m_EntityStorageInfoLookup.Exists(buildingEntity)) continue;
                        m_WearByBuilding[BuildingIdentityKey.Pack(buildingEntity)] = wear;
                    }
                }
            }

            // Build map of UnderConstruction by building entity identity (for construction days left)
            m_ConstructionByBuilding.Clear();
            using (Core.Utils.PerformanceProfiler.MeasureDebug("EqUI.ConstructionQuery"))
            {
                m_UnderConstructionTypeHandle.Update(this);
                using var constructionChunks = m_UnderConstructionQuery.ToArchetypeChunkArray(Allocator.Temp);
                for (int ci = 0; ci < constructionChunks.Length; ci++)
                {
                    var constructions = constructionChunks[ci].GetNativeArray(ref m_UnderConstructionTypeHandle);
                    for (int i = 0; i < constructionChunks[ci].Count; i++)
                    {
                        var construction = constructions[i];
                        var buildingEntity = construction.GetBuildingEntity();
                        if (!m_EntityStorageInfoLookup.Exists(buildingEntity)) continue;
                        m_ConstructionByBuilding[BuildingIdentityKey.Pack(buildingEntity)] = construction;
                    }
                }
            }

            using (Core.Utils.PerformanceProfiler.MeasureDebug("EqUI.PowerCapacitySnapshot"))
            {
                m_PrefabRefLookup.Update(this);
                RefreshCapacitySnapshotMap();
            }

            // PERF: chunk iteration — avoids SystemAPI.Query dependency tracker leak (30ms spikes over session)
            using (Core.Utils.PerformanceProfiler.MeasureDebug("EqUI.PlantQuery"))
            {
                m_ElectricityProducerTypeHandle.Update(this);
                m_DestroyedLookup.Update(this);

                m_EntityTypeHandle.Update(this);
                using var chunks = m_PlantQuery.ToArchetypeChunkArray(Unity.Collections.Allocator.Temp);
                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    var producers = chunk.GetNativeArray(ref m_ElectricityProducerTypeHandle);
                    var entities = chunk.GetNativeArray(m_EntityTypeHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var producer = producers[i];
                        var entity = entities[i];
                        long buildingKey = BuildingIdentityKey.Pack(entity);

                        // Get wear data from mod entity by building index + version
                        m_WearByBuilding.TryGetValue(buildingKey, out var wear);

                        // Skip plants not yet tracked by EquipmentWearAssignSystem — StablePlantId=0 is uninitialized
                        if (wear.StablePlantId == 0)
                            continue;

                        // Skip small producers (transformers, etc.)
                        // Use OriginalCapacity (immutable) — m_Capacity can be 0 during collapse/damage
                        int filterCapacity = wear.OriginalCapacity > 0 ? wear.OriginalCapacity : producer.m_Capacity;
                        if (filterCapacity < minCapacityKw)
                            continue;

                        float wearPercent = wearEnabled ? wear.WearPercent : 0f;
                        bool hasExploded = wearEnabled && wear.HasExploded;
                        bool isRepairing = wearEnabled && wear.IsUnderRepair;
                        float repairHoursLeft = isRepairing
                            ? math.max(0f, wear.RepairEndHour - gameHour)
                            : 0f;

                        bool hasCapacitySnapshot = m_CapacityByBuilding.TryGetValue(buildingKey, out var capacitySnapshot);
                        float operationalDamage = hasCapacitySnapshot ? capacitySnapshot.OperationalDamagePercent : 0f;
                        float disasterDamage = hasCapacitySnapshot ? capacitySnapshot.DisasterDamagePercent : 0f;
                        // Same explosion source the repair-transaction layer bills against
                        // (PlantRepairService/PlantRepairIntakeSystem.GetBillableRepairPercent):
                        // saved explosion damage when exploded, raised to the snapshot value if present.
                        // Folding it into the billable max keeps the UI percent from drifting below what
                        // the transaction charges for an exploded plant whose accumulated wear is low.
                        float explosionDamage = hasExploded ? wear.SavedExplosionDamage : 0f;
                        if (hasCapacitySnapshot)
                            explosionDamage = math.max(explosionDamage, capacitySnapshot.ExplosionDamagePercent);
                        // Vanilla Destroyed = a knocked-out ruin (reached DestructionThreshold or
                        // burned down). The building keeps its ElectricityProducer (vanilla DestroySystem
                        // strips only consumer-side components), so it stays in m_PlantQuery and we can
                        // mark it; but the mod's capacity-modifier repair cannot restore a vanilla-
                        // destroyed building, so a destroyed plant is NOT repairable — surfaced as
                        // IsDestroyed so the UI lists it under the collapsed "Destroyed" group instead of
                        // offering a repair that resolves NOT_FOUND.
                        bool isDestroyed = m_DestroyedLookup.HasComponent(entity);
                        // Wear-axis repair is offered only once the plant reads as Worn
                        // (same boundary as the WEAR indicator); otherwise a sub-1% plant
                        // shows no wear yet still offers a 24h-offline repair billed at the
                        // 1% Ceiling floor. Combat/disaster damage stays repairable from the
                        // first percent — its own indicator shows immediately.
                        bool isRepairable = !isDestroyed
                            && (wearPercent > EquipmentWearUtils.WornThreshold
                                || hasExploded
                                || operationalDamage > 0f
                                || disasterDamage > 0f);

                        // Live nameplate incl. installed upgrades: the capacity snapshot mirrors
                        // PlantBaseCapacity (re-published by the index system on upgrade); the wear
                        // sidecar's OriginalCapacity is the assign-time value and lags upgrades —
                        // fallback only.
                        int nameplateKW;
                        if (hasCapacitySnapshot && capacitySnapshot.OriginalCapacityKW > 0)
                            nameplateKW = capacitySnapshot.OriginalCapacityKW;
                        else
                            nameplateKW = wear.OriginalCapacity > 0 ? wear.OriginalCapacity : producer.m_Capacity;

                        // Surface the discrete missile-hit count so the UI shows "N/M hits" (symmetric
                        // with civilian buildings) instead of a bare percent. The step is nameplate-
                        // scaled (PlantHitMath) — same math as OperationalDamageSystem's accrual.
                        float lossPerHit = PlantHitMath.LossPerHit(
                            nameplateKW, hitSliceMW, repairCfg.MinHitLossPercent, repairCfg.MaxHitLossPercent);
                        int operationalHitMax = PlantHitMath.HitsToDestroy(lossPerHit, repairCfg.DestructionThreshold);
                        int operationalHitCount = PlantHitMath.HitCount(operationalDamage, lossPerHit, operationalHitMax);

                        // Construction status
                        bool isUnderConstruction = m_ConstructionByBuilding.TryGetValue(buildingKey, out var construction);
                        float constructionDaysLeft = isUnderConstruction
                            ? math.max(0f, construction.CompletionDay - currentGameDay)
                            : 0f;

                        // R11-S09-06 FIX: Use modifier value instead of entity existence.
                        // DisabledByDisaster entity lags 2 frames behind repair completion (2-hop ECB chain),
                        // while DisasterDamageModifier is zeroed immediately by PlantRepairService.
                        bool hasDisasterDamage = disasterDamage > 0.001f;

                        m_BackBuffer.Add(new PlantWearProducerData
                        {
                            PlantId = wear.StablePlantId,
                            Name = GetPlantName(entity),
                            // Same upgrade-aware nameplate as the hit math — MAX column follows upgrades
                            CapacityMW = nameplateKW / 1000,
                            CurrentOutputMW = ResolveCurrentOutputMW(
                                hasCapacitySnapshot,
                                capacitySnapshot,
                                isRepairing,
                                isUnderConstruction,
                                producer.m_Capacity,
                                isUnderConstruction ? construction.BaseCapacityKW : 0),
                            WearPercent = wearPercent,
                            // Max of the four damage sources the transaction layer bills against
                            // (wear / explosion / operational / disaster) — same set as
                            // RepairPaymentHelper.BillableRepairPercent. Kept as a 0..1 fraction here;
                            // PowerGridUISystem clamps it to a billable int via the shared helper.
                            RepairBillablePercent = math.max(
                                math.max(wearPercent, explosionDamage),
                                math.max(operationalDamage, disasterDamage)),
                            IsRepairable = isRepairable,
                            IsDestroyed = isDestroyed,
                            IsRepairing = isRepairing,
                            RepairHoursLeft = repairHoursLeft,
                            HasExploded = hasExploded,
                            IsUnderConstruction = isUnderConstruction,
                            ConstructionDaysLeft = constructionDaysLeft,
                            OperationalDamagePercent = operationalDamage,
                            OperationalHitCount = operationalHitCount,
                            OperationalHitMax = operationalHitMax,
                            DisasterDamagePercent = disasterDamage,
                            State = EquipmentWearUtils.GetPlantState(
                                wearPercent, hasExploded, isRepairing, isUnderConstruction, hasDisasterDamage),
                            // No snapshot ⇒ neutral defaults: 1f = no degradation / full fuel, 0f = no pending recovery.
                            SaturationFactor = hasCapacitySnapshot ? capacitySnapshot.SaturationFactor : 1f,
                            FuelAvailabilityPercent = hasCapacitySnapshot ? capacitySnapshot.FuelAvailability : 1f,
                            FuelFactor = hasCapacitySnapshot ? capacitySnapshot.FuelFactor : 1f,
                            RecoveryHours = hasCapacitySnapshot ? capacitySnapshot.RecoveryHours : 0f,
                        });
                    }
                }
            }

            // Sort by total damage descending (problems first)
            m_BackBuffer.Sort(s_DamageDescending);

            // FIX S16_CODE1:167: Diagnostic log should be Debug, not Info (fires every refresh cycle)
            if (Log.IsDebugEnabled) Log.Debug($"[DIAG] Refreshed plants={m_BackBuffer.Count} (query entities iterated, after capacity filter)");

            // Atomic swap — increment version so UI can skip re-serialization on unchanged data
            lock (m_BufferLock)
            {
                (m_FrontBuffer, m_BackBuffer) = (m_BackBuffer, m_FrontBuffer);
                System.Threading.Interlocked.Increment(ref m_PlantsVersion);
            }
        }

        private void RefreshCapacitySnapshotMap()
        {
            m_CapacityByBuilding.Clear();
            if (m_PowerCapacitySnapshotReader == null
                || !m_PowerCapacitySnapshotReader.TryGetSnapshot(out var snapshot))
            {
                return;
            }

            var plants = snapshot.Plants;
            for (int i = 0; i < plants.Count; i++)
                m_CapacityByBuilding[BuildingIdentityKey.Pack(plants[i].Plant)] = plants[i];
        }

        private static int ResolveCurrentOutputMW(
            bool hasCapacitySnapshot,
            PowerCapacityPlantSnapshot capacitySnapshot,
            bool isRepairing,
            bool isUnderConstruction,
            int producerCapacityKW,
            int constructionBaseKW)
        {
            if (hasCapacitySnapshot)
                // Lesser of the weather-accurate output (vanilla m_Capacity — actual production,
                // but lags a collapse by the single-writer ProceduralUpload latency) and the
                // instant EffectiveCapacityKW knockout ceiling (factor ⇒ 0 the tick a plant
                // collapses/repairs). min keeps renewables weather-accurate in calm weather
                // (CurrentOutputKW wins, ~0) while a collapsed thermal plant reads 0 immediately
                // instead of holding stale output until vanilla folds the factor into m_Capacity.
                return System.Math.Min(capacitySnapshot.CurrentOutputKW, capacitySnapshot.EffectiveCapacityKW) / 1000;
            // Repair-without-snapshot stays 0 (knockout) — matches the resolver's repair rule even
            // when the plant is simultaneously under upgrade construction (the two states are
            // independent components, so a plant can be both). Check repair first.
            if (isRepairing)
                return 0;
            // An upgrade-delta plant keeps producing its base capacity while only the added
            // capacity is under construction — surface that live base instead of a flat 0.
            if (isUnderConstruction)
                return constructionBaseKW > 0 ? constructionBaseKW / 1000 : 0;
            return producerCapacityKW / 1000;
        }

        private string GetPlantName(Entity buildingEntity)
        {
            // Use vanilla NameSystem for localized building name.
            // Falls back to prefab-based PowerPlantUtils if NameSystem returns
            // an internal ID or empty string (e.g. during construction).
            string name = m_NameSystem?.GetRenderedLabelName(buildingEntity)!;
            if (!string.IsNullOrEmpty(name) && !name.StartsWith("Assets."))
                return name;
            // Fallback: prefab-based display name
            if (m_PrefabRefLookup.TryGetComponent(buildingEntity, out var prefabRef))
                return PowerPlantUtils.GetDisplayName(PowerPlantUtils.GetPlantType(m_PrefabSystem, prefabRef));
            return "Power Plant";
        }
    }
}
