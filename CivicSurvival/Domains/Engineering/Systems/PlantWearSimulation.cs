using System;
using System.Threading;
using Game;
using Game.Buildings;
using Game.Prefabs;
using Game.Common;
using Game.Events;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Engineering.Jobs;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Domains.Engineering.Services;
using CivicSurvival.Core.Systems;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Orchestrates equipment wear lifecycle for power plants.
    ///
    /// Wear rules:
    /// - Below 90% load: no wear
    /// - 90-100% load: 0.1% per hour
    /// - Above 100% load: 1% per hour (10x)
    ///
    /// At 50% wear, explosion risk begins (up to 5%/hour at 100% wear).
    /// Explosion = fire + 80% capacity loss via EquipmentWearModifier.ExplosionDamagePercent.
    ///
    /// Uses async N-1 job pattern: Burst job scheduled on frame N,
    /// results applied on frame N+1 (no main-thread blocking).
    ///
    /// S13a-6 ACCEPTED: Wear accumulation during grid collapse is harmless — collapsed plants have capacity=0 (no load=no wear).
    /// S13a-11 ACCEPTED: ThrottledSystemBase latency (at most 1 update interval) — inherent; no gameplay impact.
    /// </summary>
    [SingletonOwner(typeof(EquipmentWear))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None,
        AllowAsymmetry = true,
        Justification = "EquipmentWear lives on plant sidecar entities. This system owns the persisted StablePlantId counter, the runtime StablePlantId→entity map, the wear-job lifecycle, expired-repair completion, post-load reconcile, and the IPlantWearReader service surface. Pause-safe plant repair reads this system's map via the RefreshPlantIdMap entry point at intake/commit boundaries.")]
    public partial class PlantWearSimulation : ThrottledSystemBase, IPostLoadValidation, IPlantWearReader, IActGatedSystem
    {
        public void PurgeAfterLoad() { }

        // --- ECB diagnostics ---
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private static readonly LogContext Log = new("PlantWearSimulation");

        protected override int UpdateInterval => BalanceConfig.Current.EquipmentWear.UpdateIntervalFrames;
        public Act MinActiveAct => Act.Crisis;
        public ActGateState GateState => m_Gate?.State ?? ActGateState.AwaitingActState;

        // --- Dependencies ---
        private PrefabSystem m_PrefabSystem = null!;
        // ShadowWallet: deductions now via ShadowWalletService static + ECB (single-writer migration)
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ModSettings? m_Settings;
        private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;
        private IVanillaWriteBarrier? m_VanillaWriteBarrier;

        // --- Queries ---
        private EntityQuery m_PowerGridQuery;
        private EntityQuery m_WaveStateQuery; // FIX S18-05
        private EntityQuery m_CurrentActQuery;
        [System.NonSerialized] private ActGateController m_Gate = null!;
        // Cached for RefreshPlantIdMap (which is public — SystemAPI.Query in
        // a non-private method would trigger CIVIC281 because the call site
        // is in another system's update context).
        private EntityQuery m_AllWearQuery;

        // --- ComponentLookups (cached in OnCreate, updated each frame) ---
        private ComponentLookup<EquipmentWear> m_EquipmentWearLookup;
        private ComponentLookup<ElectricityProducer> m_ElectricityProducerLookup;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentLookup<OnFire> m_OnFireLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        // Shared cross-system Ignite/Destroy dedup. Per-system m_IgniteQueuedThisFrame
        // retired in favour of IFrameMutationDedup published by Mod.OnLoad.
        private IFrameMutationDedup m_FrameMutationDedup = null!;
        [System.NonSerialized] private CivicServiceLookups m_RepairLookups = null!;

        // --- Serialized state (save/load via partial class) ---
        private int m_NextPlantId = 1;
        private double m_GameHour;
        private double m_LastGameHour = -1.0;
        [System.NonSerialized] private bool m_SuppressInitialEnabledRebase;

        /// <summary>Allocate next unique StablePlantId. Main-thread only; used by EquipmentWearAssignSystem.</summary>
        public int AllocateNextPlantId() => Interlocked.Increment(ref m_NextPlantId) - 1;

        // --- StablePlantId map ---
        // Owned here; consumers (PlantRepairRequestProcessor) force-refresh via
        // RefreshPlantIdMap on every schedule/drain pass so they never depend
        // on this system's throttle interval.
        [NonEntityIndex] private NativeHashMap<int, Entity> m_PlantIdToEntity;
        [System.NonSerialized] private int m_PlantMapCleanupCounter;
        private const int PLANT_MAP_CLEANUP_INTERVAL = 60;
        private const float MAX_LOAD_RATIO = 1.5f;

        /// <summary>
        /// Live StablePlantId → wear-entity map. Read-only to consumers; the
        /// only writer is this system. Call <see cref="RefreshPlantIdMap"/>
        /// before reading if you need a guaranteed-fresh view.
        /// </summary>
        internal NativeHashMap<int, Entity> PlantIdToEntity => m_PlantIdToEntity;

        /// <summary>
        /// Clear and rebuild the StablePlantId map from live <see cref="EquipmentWear"/>
        /// entities. Used by <see cref="PlantRepairRequestProcessor"/> at the
        /// schedule/drain service boundary so the processor never depends on
        /// this system's throttled per-tick population.
        /// </summary>
        [CompletesDependency("RefreshPlantIdMap: m_AllWearQuery.ToEntityArray/ToComponentDataArray scan; main-thread, called from consumer paths after RegisterAfter chains have run.")]
        public void RefreshPlantIdMap()
        {
            if (!m_PlantIdToEntity.IsCreated)
                return;
            m_PlantIdToEntity.Clear();
            // Refresh liveness lookups: this method is public and called from another
            // system's update context, so they may be stale relative to this frame.
            // Lookup.Update does not touch Dependency (no sync point).
            m_StorageInfoLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
            // Use cached EntityQuery rather than SystemAPI.Query: this method
            // is public and called from another system's update context
            // (PlantRepairRequestProcessor), so SystemAPI.Query would trip
            // CIVIC281.
            using var entities = m_AllWearQuery.ToEntityArray(Allocator.Temp);
            using var wears = m_AllWearQuery.ToComponentDataArray<EquipmentWear>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                int stableId = wears[i].StablePlantId;
                if (stableId <= 0)
                    continue;
                // Only resolvable plants enter the map (D1 root fix): a StablePlantId shared
                // by a stale/dead-building sidecar and a live one must resolve to the live one
                // regardless of iteration order, or the resolver rejects a repairable plant as
                // UI_PLANT_REPAIR_NOT_FOUND. Same liveness contract the resolver uses.
                if (!IsLivePlantEntity(entities[i], wears[i]))
                    continue;
                // Grow before insert — initial capacity of 64 (OnCreate) would
                // otherwise throw when the live plant set exceeds it after
                // load or organic city growth.
                EnsurePlantMapCapacity(m_PlantIdToEntity.Count + 1);
                m_PlantIdToEntity[stableId] = entities[i];
            }
        }

        // --- Async N-1 double-buffer ---
        private NativeList<Entity> m_WearEntities;
        private NativeList<EquipmentWearInput> m_WearInputs;
#pragma warning disable CIVIC278 // NativeArray has no Clear(); guarded by m_PendingCount reset in ResetState
        [System.NonSerialized] private NativeArray<EquipmentWearOutput> m_WearOutputs;
#pragma warning restore CIVIC278
        private NativeList<Entity> m_PendingEntities;
        private JobHandle m_PendingJobHandle;
        private bool m_HasPendingResults;
        private int m_PendingCount;
        [System.NonSerialized] private uint m_FrameSeed;

        // --- Repair ---
        // BuildRepairContext is used here only for expired-repair completion
        // (PlantRepairService.CompleteRepair). Schedule and drain live in
        // PlantRepairRequestProcessor; this system holds no reference back
        // to the processor.
        [System.NonSerialized] private PlantRepairContext m_RepairContext;

        protected override void OnCreate()
        {
            base.OnCreate();
            Log.Info("Created");

            m_EquipmentWearLookup = GetComponentLookup<EquipmentWear>(false);
            m_ElectricityProducerLookup = GetComponentLookup<ElectricityProducer>(true);
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_OnFireLookup = GetComponentLookup<OnFire>(true);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            // m_IgniteQueuedThisFrame replaced by shared
            // IFrameMutationDedup (resolved in OnStartRunning).
            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_RepairLookups = new CivicServiceLookups(() =>
            {
                m_EquipmentWearLookup.Update(this);
                m_ElectricityProducerLookup.Update(this);
                m_BaseCapacityLookup.Update(this);
                m_StorageInfoLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                UpdateGameTime();
            });

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            // ShadowWallet: resolved via ShadowWalletService static (single-writer migration)
            m_PowerGridQuery = GetEntityQuery(ComponentType.ReadOnly<PowerGridSingleton>());
            m_WaveStateQuery = GetEntityQuery(ComponentType.ReadOnly<WaveStateSingleton>()); // FIX S18-05
            // m_Random removed — explosion RNG migrated to per-seed Burst job Random

            m_PlantIdToEntity = new NativeHashMap<int, Entity>(64, Allocator.Persistent);
            m_AllWearQuery = GetEntityQuery(
                ComponentType.ReadOnly<EquipmentWear>(),
                ComponentType.Exclude<Deleted>());
            m_WearEntities = new NativeList<Entity>(64, Allocator.Persistent);
            m_WearInputs = new NativeList<EquipmentWearInput>(64, Allocator.Persistent);
            m_PendingEntities = new NativeList<Entity>(64, Allocator.Persistent);

            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            InitializeGate();

            // FIX S5-05: Immediate hydration after load — no stale ExplosionDamagePercent

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IPlantWearReader>(this);
            }

        }

        protected override bool ShouldSkipUpdate()
        {
            m_Gate.ReconcileFromSingleton(m_CurrentActQuery);
            return m_Gate.State != ActGateState.Active;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            m_VanillaWriteBarrier ??= ServiceRegistry.Instance.Require<IVanillaWriteBarrier>();
            m_FrameMutationDedup ??= ServiceRegistry.Instance.Require<IFrameMutationDedup>();
        }

        // FIX S5-05: Immediate ExplosionDamagePercent + IsUnderRepair hydration after load
        // Order 20: same tier as Disaster (independent fields), before GridStress(30)
        // +1 ensures EWS runs after PPDS: expired-repair zero-write overrides PPDS re-arm
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_WEAR;
#pragma warning disable CIVIC231 // ValidateAfterLoad (hydration) — runs every load regardless of act
        public void ValidateAfterLoad()
        {
            // Deferred boot-leg reset (CIVIC462: ResetToBootDefaults itself must stay
            // field-only, so the job completion + container disposal run here instead).
            if (m_PendingBootDefaultRuntimeReset)
            {
                CompleteAndClearRuntimeBuffers();
                m_PendingBootDefaultRuntimeReset = false;
            }

            m_EquipmentWearLookup.Update(this);

            // m_GameHour is 0 during ValidateAfterLoad (UpdateGameTime hasn't run yet),
            // so read actual game time directly for repair expiry check
            var timeProvider = GameTimeSystem.Instance;
            bool canExpireRepairs = timeProvider != null;
            float gameHour = canExpireRepairs ? timeProvider!.Current.TotalGameHours : 0f;
            if (!canExpireRepairs)
                Log.Warn("ValidateAfterLoad: GameTimeSystem unavailable; deferred expired-repair completion");

            // Collect expired-repair wear entities, then complete each through the
            // canonical PlantRepairService.CompleteRepair — the single repair-transaction
            // owner. It clears the wear sidecar AND stamps the DURABLE damage sinks
            // (RepairedThroughHour on DisabledByDisaster via IDisasterRepairSink, with the
            // CreatedHour >= repairHour guard), with the correct CauseMask derived from the
            // damage actually present. The previous inline copy here only deleted the
            // disaster sidecar transiently (no durable stamp) and hardcoded an Operational
            // CauseMask, so a re-save before the ECB played back resurrected the disaster.
            var expiredWearEntities = new System.Collections.Generic.List<Entity>(4);

            foreach (var (wear, wearEntity) in SystemAPI.Query<RefRO<EquipmentWear>>().WithNone<Deleted>().WithEntityAccess())
            {
                if (!wear.ValueRO.IsUnderRepair)
                    continue;
                bool expired = canExpireRepairs && wear.ValueRO.RepairEndHour > 0f && gameHour >= wear.ValueRO.RepairEndHour;
                if (expired)
                    expiredWearEntities.Add(wearEntity);
            }

            if (expiredWearEntities.Count > 0)
            {
                // GameHour overridden below: m_GameHour is still 0 during ValidateAfterLoad
                // (UpdateGameTime has not run), so the context must carry the real load-time hour.
                var repairContext = BuildRepairContext(m_GameSimulationEndBarrier.CreateCommandBuffer());
                repairContext.GameHour = gameHour;
                foreach (var wearEntity in expiredWearEntities)
                    PlantRepairService.CompleteRepair(ref repairContext, wearEntity);
            }
            Log.Info("ValidateAfterLoad: EquipmentWear sidecar repair state reconciled");
        }
#pragma warning restore CIVIC231

        protected override void OnThrottledUpdate()
        {
            try
            {
                OnThrottledUpdateCore();
            }
            catch (Exception ex)
            {
                // Log full PDB stack before CS2's Harmony Update_Patch5 wrapper
                // truncates it to two (wrapper dynamic-method) frames.
                Log.Error($"OnThrottledUpdate failed: {ex}");
                throw;
            }
        }

        private void OnThrottledUpdateCore()
        {
            if (m_Settings == null) { Log.Error("[PlantWearSimulation] ModSettings unavailable"); return; }
            // Settings toggle-off: expired repairs still complete; only wear
            // calculation (SinglePassUpdate) is gated below. Drain of resolved
            // budget results runs in PlantRepairRequestProcessor independently.
            bool shouldCalculateWear = m_Settings.EquipmentWearEnabled;
            m_StorageInfoLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);

            if (++m_PlantMapCleanupCounter >= PLANT_MAP_CLEANUP_INTERVAL)
            {
                m_PlantMapCleanupCounter = 0;
                CleanupStalePlantEntries();
            }

            // GameTimeSystem must be ready (prevents instant repairs on init race)
#pragma warning disable CIVIC256 // Static singleton — null before GameTimeSystem.OnCreate
            if (GameTimeSystem.Instance == null)
                return;
#pragma warning restore CIVIC256

            m_EquipmentWearLookup.Update(this);
            m_ElectricityProducerLookup.Update(this);
            m_BaseCapacityLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_OnFireLookup.Update(this);
            m_StorageInfoLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
            // Shared IFrameMutationDedup frame-cleared by
            // FrameMutationDedupClearSystem; nothing to clear here.

            // UpdateGameTime must run BEFORE expiry detection so m_GameHour is fresh.
            UpdateGameTime();

            // Build repair context for the expiry-completion path. Pending
            // repair dedup is owned by PlantRepairRequestProcessor.
            m_RepairLookups.RefreshIfStale();
            bool repairContextReady = false;

            // Reconcile EquipmentWear sidecars; PowerCapacityPipeline hydrates modifier flags.
            foreach (var (wear, wearEntity) in
                SystemAPI.Query<RefRO<EquipmentWear>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Populate map incrementally so the next consumer-side
                // RefreshPlantIdMap is mostly a no-op when nothing changed.
                // Live-building only (D1 root fix): never map an id onto a dead-building
                // sidecar — same liveness contract the resolver applies.
                if (wear.ValueRO.StablePlantId > 0 && IsLivePlantEntity(wearEntity, wear.ValueRO))
                {
                    EnsurePlantMapCapacity(m_PlantIdToEntity.Count + 1);
                    m_PlantIdToEntity[wear.ValueRO.StablePlantId] = wearEntity;
                }

                if (wear.ValueRO.IsUnderRepair)
                {
                    bool expired = wear.ValueRO.RepairEndHour > 0f && m_GameHour >= wear.ValueRO.RepairEndHour;
                    if (expired)
                    {
                        // Repair finished — complete directly (covers settings-off path
                        // where EquipmentWearJob is not scheduled to detect expiry).
                        if (!repairContextReady)
                        {
#pragma warning disable CIVIC486 // Repair expiry is the intent gate; ECB allocation happens only after work is known.
                            m_RepairContext = BuildRepairContext(m_GameSimulationEndBarrier.CreateCommandBuffer(), lookupsFresh: true);
#pragma warning restore CIVIC486
                            repairContextReady = true;
                        }
                        PlantRepairService.CompleteRepair(ref m_RepairContext, wearEntity);
                    }
                }
            }

            // Apply previous frame's job results before the time-delta guard — explosions and
            // repair completions must not be delayed by a deltaHours=0 frame (e.g. first post-load tick)
            // Complete pending job and apply results regardless of settings toggle
            if (m_HasPendingResults)
            {
                m_PendingJobHandle.Complete();
                ApplyPendingResults(ref repairContextReady);
                m_HasPendingResults = false;
            }

            // Wear calculation gated by settings — expiry completion above always runs
            if (!shouldCalculateWear)
                return;

            float deltaHours = CalculateDeltaHours();
            if (deltaHours <= 0f)
                return;

            using (PerformanceProfiler.Measure("EquipmentWear.OnUpdate"))
            {
                float cityLoadRatio = 0f;
#pragma warning disable CIVIC070 // Power load ratio changes gradually; 1-frame lag invisible for wear calc
                if (m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid) && grid.Production > 0)
#pragma warning restore CIVIC070
                {
                    // H09+H06 FIX: Clamp ratio to prevent wear spike from stale Consumption
                    // during partial recovery (one plant online, Consumption not yet updated)
                    cityLoadRatio = math.clamp((float)grid.Consumption / grid.Production, 0f, MAX_LOAD_RATIO);
                }

                SinglePassUpdate(deltaHours, cityLoadRatio);
            }
        }

        /// <summary>
        /// Async N-1 pattern: complete previous frame's job, then schedule next.
        /// Results are delayed by 1 throttle interval (~1 second).
        /// </summary>
        private void SinglePassUpdate(float deltaHours, float cityLoadRatio)
        {
            // Note: pending results already applied earlier in OnThrottledUpdate (before deltaHours guard).
            // Completion now happens unconditionally before collecting inputs for the next job.

            // Step 2: Collect inputs for new job
            m_WearEntities.Clear();
            m_WearInputs.Clear();
            m_FrameSeed = Unity.Mathematics.math.hash(new Unity.Mathematics.uint2(
                (uint)UnityEngine.Time.frameCount, Unity.Mathematics.math.asuint((float)m_GameHour)));

            // Drain the vanilla ElectricityProducer write fence before reading
            // producer.m_Capacity below (the fallback knockout source). The loop iterates
            // EquipmentWear sidecar entities and per-entity reads producer.m_Capacity via
            // ComponentLookup — on the frame %128 == 0 boundary this would otherwise race
            // vanilla PowerPlantTickJob. Steady-state cost: instant fence drain.
            var vanillaTicket = m_VanillaWriteBarrier!.Consume(
                EntityManager,
                typeof(PlantWearSimulation),
                VanillaWriteComponentMask.ElectricityProducer);

            // Synchronous knockout signal. The resolver folds grid-producer damage into the
            // vanilla Efficiency buffer, so vanilla m_Capacity now lags the mod factor by up
            // to ~158 frames. Gating wear/explosion on that lagging value would let a freshly
            // collapsed plant keep accruing wear. The resolver also publishes the factor-based
            // EffectiveCapacityKW in the same tick it changes; prefer it for the gate and fall
            // back to vanilla m_Capacity only when the plant is not yet in the snapshot.
            using var snapshotCaps = BuildSnapshotCapacityMap();

            foreach (var (wearRef, wearEntity) in
                SystemAPI.Query<RefRO<EquipmentWear>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                var wear = wearRef.ValueRO;
                var buildingEntity = wear.GetBuildingEntity();

                if (!m_StorageInfoLookup.Exists(buildingEntity))
                    continue;
                if (!TryGetKnockoutCapacity(vanillaTicket, snapshotCaps, buildingEntity, out int capacityKW))
                    continue;

                // Incrementally populate StablePlantId map for sibling consumers.
                // Live-building only (D1 root fix): the Exists check above rejects a missing
                // building, but a Deleted/Destroyed one must not be mapped either — route
                // through the shared resolvability contract, same as the resolver.
                if (wear.StablePlantId > 0 && IsLivePlantEntity(wearEntity, wear))
                {
                    EnsurePlantMapCapacity(m_PlantIdToEntity.Count + 1);
                    m_PlantIdToEntity[wear.StablePlantId] = wearEntity;
                }

                m_WearEntities.Add(wearEntity);
                m_WearInputs.Add(new EquipmentWearInput
                {
                    WearPercent = wear.WearPercent,
                    OverloadHours = wear.OverloadHours,
                    HasExploded = wear.HasExploded,
                    IsUnderRepair = wear.IsUnderRepair,
                    RepairEndHour = wear.RepairEndHour,
                    RepairEpoch = wear.RepairEpoch,
                    Capacity = capacityKW,
                    RandomSeed = math.max(1u, math.hash(new uint3((uint)wearEntity.Index, (uint)wearEntity.Version, m_FrameSeed)))
                });
            }

            if (m_WearEntities.Length == 0)
                return;

            // Step 3: Schedule job (no Complete — read next frame)
            EnsureOutputBuffer(m_WearInputs.Length);

            var wearCfg = BalanceConfig.Current.EquipmentWear;
            var wearJob = new EquipmentWearJob
            {
                DeltaHours = deltaHours,
                CityLoadRatio = cityLoadRatio,
                GameHour = (float)m_GameHour,
                Config = new Jobs.EquipmentWearConfig
                {
                    HighLoadThreshold = wearCfg.HighLoadThreshold,
                    OverloadThreshold = wearCfg.OverloadThreshold,
                    BaseWearRate = wearCfg.BaseWearRate,
                    OverloadMultiplier = wearCfg.OverloadMultiplier,
                    MaxWearPercent = wearCfg.MaxWearPercent,
                    DangerZoneThreshold = wearCfg.ExplosionThreshold,
                    MaxExplosionRisk = wearCfg.ExplosionChanceMax
                },
                Inputs = m_WearInputs.AsArray(),
                Outputs = m_WearOutputs
            };

            if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre EquipmentWearJob.Schedule inputs={m_WearInputs.IsCreated}/{m_WearInputs.Length} entities={m_WearEntities.IsCreated}/{m_WearEntities.Length} outputs={m_WearOutputs.IsCreated}/{m_WearOutputs.Length} plantMap={m_PlantIdToEntity.IsCreated}/count={m_PlantIdToEntity.Count}/capacity={m_PlantIdToEntity.Capacity} deltaHours={deltaHours:F3} load={cityLoadRatio:F3}");
            m_PendingJobHandle = wearJob.Schedule(m_WearInputs.Length, 8);
            if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post EquipmentWearJob.Schedule inputs={m_WearInputs.IsCreated}/{m_WearInputs.Length} outputs={m_WearOutputs.IsCreated}/{m_WearOutputs.Length}");
            Dependency = JobHandle.CombineDependencies(Dependency, m_PendingJobHandle);

            // Snapshot entities for next frame (buildings may be demolished)
            m_PendingEntities.Clear();
            m_PendingEntities.AddRange(m_WearEntities.AsArray());
            m_PendingCount = m_WearEntities.Length;
            m_HasPendingResults = true;
        }

        /// <summary>
        /// Snapshot building-key → synchronous factor-based <c>EffectiveCapacityKW</c> map for
        /// the wear knockout gate. Built once per wear pass from the resolver snapshot so the
        /// gate reflects collapse/repair in the same tick (not after vanilla m_Capacity
        /// catches up). Empty when no snapshot has been published yet — callers fall back to
        /// vanilla m_Capacity.
        /// </summary>
        private NativeHashMap<long, int> BuildSnapshotCapacityMap()
        {
            var map = new NativeHashMap<long, int>(16, Allocator.Temp);
            if (m_PowerCapacitySnapshotReader == null
                || !m_PowerCapacitySnapshotReader.TryGetSnapshot(out var snapshot))
                return map;

            var plants = snapshot.Plants;
            for (int i = 0; i < plants.Count; i++)
            {
                var plant = plants[i];
                map[BuildingIdentityKey.Pack(plant.Plant)] = plant.EffectiveCapacityKW;
            }

            return map;
        }

        /// <summary>
        /// Synchronous knockout capacity for the wear gate. Prefers the resolver's
        /// factor-based <c>EffectiveCapacityKW</c> (published the same tick the factor
        /// changes); falls back to the ticket-gated vanilla
        /// <see cref="ElectricityProducer.m_Capacity"/> read when the plant is not yet in the
        /// snapshot. The ticket parameter documents the fence requirement and unlocks the
        /// analyzer guard (CIVIC452) on the producer field access in the fallback.
        /// </summary>
        private bool TryGetKnockoutCapacity(
            VanillaWriteTicket vanillaTicket,
            NativeHashMap<long, int> snapshotCaps,
            Entity buildingEntity,
            out int capacityKW)
        {
            if (snapshotCaps.TryGetValue(BuildingIdentityKey.Pack(buildingEntity), out capacityKW))
                return true;

            if (!vanillaTicket.Covers(VanillaWriteComponentMask.ElectricityProducer))
                throw new InvalidOperationException("TryGetKnockoutCapacity fallback requires a VanillaWriteTicket covering ElectricityProducer.");

            if (!m_ElectricityProducerLookup.TryGetComponent(buildingEntity, out var producer))
            {
                capacityKW = 0;
                return false;
            }

            capacityKW = producer.m_Capacity;
            return true;
        }

        /// <summary>
        /// Apply results from previous frame's Burst job.
        /// Skips demolished entities; defers explosions and repair completions.
        /// </summary>
        private void ApplyPendingResults(ref bool repairContextReady)
        {
            var toExplode = new NativeList<Entity>(Allocator.Temp);
            var toComplete = new NativeList<Entity>(Allocator.Temp);

            try
            {
                for (int i = 0; i < m_PendingCount; i++)
                {
                    var entity = m_PendingEntities[i];
                    var output = m_WearOutputs[i];

                    if (!m_StorageInfoLookup.Exists(entity) || m_DeletedLookup.HasComponent(entity) || m_DestroyedLookup.HasComponent(entity))
                        continue;

                    if (m_EquipmentWearLookup.TryGetComponent(entity, out var wear))
                    {
                        if (!IsLivePlantEntity(entity, wear))
                            continue;

                        // Skip stale job output if repair/hydration advanced the
                        // component generation after this async job was scheduled.
                        if (output.RepairEpoch != wear.RepairEpoch)
                            continue;

                        if (output.Status == WearStatus.RepairComplete)
                        {
                            if (wear.IsUnderRepair)
                                toComplete.Add(entity);
                            continue;
                        }

                        if (output.Status == WearStatus.ShouldExplode)
                        {
                            if (!wear.IsUnderRepair)
                                toExplode.Add(entity);
                            continue;
                        }

                        wear.WearPercent = output.NewWearPercent;
                        wear.OverloadHours = output.NewOverloadHours;
                        m_EquipmentWearLookup[entity] = wear;
                    }
                }

                if (toExplode.Length > 0)
                {
                    var explosionCtx = BuildExplosionContext();
                    foreach (var entity in toExplode)
                    {
                        if (!m_EquipmentWearLookup.TryGetComponent(entity, out var wear))
                            continue;
                        explosionCtx.OnCollapsedProducer = HasActiveCollapsedProducer(wear.GetBuildingEntity());
                        int ecbWrites = PlantExplosionService.Trigger(ref explosionCtx, entity);
                        for (int w = 0; w < ecbWrites; w++)
                            IncrementEcbCount();
                    }
                }
                foreach (var entity in toComplete)
                {
                    if (!repairContextReady)
                    {
                        m_RepairContext = BuildRepairContext(m_GameSimulationEndBarrier.CreateCommandBuffer());
                        repairContextReady = true;
                    }
                    PlantRepairService.CompleteRepair(ref m_RepairContext, entity);
                }
            }
            finally
            {
                if (toExplode.IsCreated) toExplode.Dispose();
                if (toComplete.IsCreated) toComplete.Dispose();
            }
        }

        private PlantExplosionContext BuildExplosionContext()
        {
            return new PlantExplosionContext
            {
                World = World,
                Ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(),
                EventBus = EventBus,
                PrefabSystem = m_PrefabSystem,
                FrameMutationDedup = m_FrameMutationDedup,
                WearLookup = m_EquipmentWearLookup,
                BaseCapacityLookup = m_BaseCapacityLookup,
                PrefabRefLookup = m_PrefabRefLookup,
                OnFireLookup = m_OnFireLookup,
                DeletedLookup = m_DeletedLookup,
                DestroyedLookup = m_DestroyedLookup,
                StorageInfoLookup = m_StorageInfoLookup,
                // OnCollapsedProducer is set per-target by the caller.
            };
        }

        // ================================================================
        // Explosion (side-effects in PlantExplosionService)
        // ================================================================

        private bool HasActiveCollapsedProducer(Entity buildingEntity)
        {
            long key = BuildingIdentityKey.Pack(buildingEntity);
            foreach (var collapsed in SystemAPI.Query<RefRO<CollapsedProducer>>().WithNone<Deleted>())
            {
                if (BuildingIdentityKey.Pack(collapsed.ValueRO.Building.Index, collapsed.ValueRO.Building.Version) == key)
                    return true;
            }

            return false;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private PlantRepairContext BuildRepairContext(EntityCommandBuffer ecb, bool lookupsFresh = false)
        {
            if (!lookupsFresh)
                m_RepairLookups.RefreshIfStale();

            var wavePhase = (m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var ws)
                    ? ws : WaveStateSingleton.Default)
                .CurrentPhase;

            // BuildRepairContext here is used by ApplyPendingResults to call
            // PlantRepairService.CompleteRepair on expired repairs. CompleteRepair
            // does not consult PlantIdToEntity (the wear entity is already known
            // from the caller's foreach), but we pass the map for consistency.
            // The pending-id set is owned by PlantRepairRequestProcessor and is
            // not exposed through the context.
            return new PlantRepairContext
            {
                GameHour = (float)m_GameHour,
                World = World,
                EventBus = EventBus,
                PlantIdToEntity = m_PlantIdToEntity,
                WearLookup = m_EquipmentWearLookup,
                ProducerLookup = m_ElectricityProducerLookup,
                BaseCapacityLookup = m_BaseCapacityLookup,
                StorageInfoLookup = m_StorageInfoLookup,
                DeletedLookup = m_DeletedLookup,
                DestroyedLookup = m_DestroyedLookup,
                OperationalDamageRepairSink = ServiceRegistry.IsInitialized
                    ? ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullOperationalDamageRepairSink.Instance)
                    : null,
                DisasterRepairSink = ServiceRegistry.IsInitialized
                    ? ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullDisasterRepairSink.Instance)
                    : null,
                PowerCapacitySnapshotReader = m_PowerCapacitySnapshotReader,
                Ecb = ecb,
                CurrentPhase = wavePhase
            };
        }

        private void UpdateGameTime()
        {
            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider == null)
            {
                Log.Error("[PlantWearSimulation] GameTimeSystem unavailable — skipping time update");
                return;
            }
            double newHour = timeProvider.Current.TotalGameHours;
            if (double.IsNaN(newHour) || double.IsInfinity(newHour) || newHour <= 0.0)
                return;
            m_GameHour = newHour;
        }

        private float CalculateDeltaHours()
        {
            if (m_LastGameHour < 0.0)
            {
                m_LastGameHour = m_GameHour;
                return 0f;
            }

            float delta = (float)(m_GameHour - m_LastGameHour);
            m_LastGameHour = m_GameHour;
            // Cap at 1h: prevents wear/explosion burst after long pause or save/load time jump.
            // At 2x speed ~7.2 game-hours/real-minute, throttle interval ensures delta ≤ 0.12h normally.
            return math.clamp(delta, 0f, 1f);
        }

        private void EnsureOutputBuffer(int inputCount)
        {
            int requiredSize = inputCount + 32;
            bool needsResize = !m_WearOutputs.IsCreated
                            || m_WearOutputs.Length < inputCount
                            || m_WearOutputs.Length > requiredSize * 2;

            if (needsResize)
            {
                if (m_WearOutputs.IsCreated) m_WearOutputs.Dispose();
                m_WearOutputs = default;
                m_WearOutputs = new NativeArray<EquipmentWearOutput>(requiredSize, Allocator.Persistent);
            }
        }

        // Plant is resolvable iff BOTH its sidecar and the building it points at are live,
        // via the one shared liveness contract (TargetLiveness.IsLiveTarget). Single source
        // shared with PlantRepairService's resolver so the StablePlantId map, the resolver,
        // and cleanup all answer "is this plant repairable" the same way.
        private bool IsLivePlantEntity(Entity wearEntity, in EquipmentWear wear)
            => TargetLiveness.IsLiveTarget(wearEntity, m_StorageInfoLookup, m_DeletedLookup, m_DestroyedLookup)
               && TargetLiveness.IsLiveTarget(wear.GetBuildingEntity(), m_StorageInfoLookup, m_DeletedLookup, m_DestroyedLookup);

        // ================================================================
        // IPlantWearReader + StablePlantId map helpers
        // ================================================================

        (bool found, PlantWearView view) IPlantWearReader.GetWearState(int stablePlantId)
        {
            // W2 row 163: every public read-side path must refresh the full
            // liveness/capacity bundle before consulting StablePlantId maps.
            m_RepairLookups.RefreshIfStale();
            RefreshPlantIdMap();
            return TryGetWearStateCore(stablePlantId, out var view) ? (true, view) : (false, default);
        }

        private bool TryGetWearStateCore(int stablePlantId, out PlantWearView view)
        {
            view = default;
            if (stablePlantId <= 0)
                return false;
            if (!m_PlantIdToEntity.IsCreated
                || !m_PlantIdToEntity.TryGetValue(stablePlantId, out var wearEntity)
                || !m_EquipmentWearLookup.TryGetComponent(wearEntity, out var wear))
            {
                return false;
            }
            if (!IsLivePlantEntity(wearEntity, wear))
                return false;
            view = new PlantWearView
            {
                StablePlantId = wear.StablePlantId,
                Building = wear.Building,
                WearPercent = wear.WearPercent,
                IsUnderRepair = wear.IsUnderRepair,
                RepairEndHour = wear.RepairEndHour
            };
            return true;
        }

        private void EnsurePlantMapCapacity(int required)
        {
            if (!m_PlantIdToEntity.IsCreated || required <= m_PlantIdToEntity.Capacity)
                return;
            int nextCapacity = math.max(required, math.max(16, m_PlantIdToEntity.Capacity * 2));
            m_PlantIdToEntity.Capacity = nextCapacity;
        }

        private void CleanupStalePlantEntries()
        {
            if (!m_PlantIdToEntity.IsCreated || m_PlantIdToEntity.Count == 0)
                return;

            // StorageInfo/Deleted/Destroyed are refreshed by the caller this frame; the wear
            // lookup is refreshed later (post GameTimeSystem gate), so refresh it here — needed
            // to resolve each sidecar's building. Lookup.Update is not a sync point.
            m_EquipmentWearLookup.Update(this);
            var keys = m_PlantIdToEntity.GetKeyArray(Allocator.Temp);
            int removedCount = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (!m_PlantIdToEntity.TryGetValue(keys[i], out Entity entity))
                    continue;
                // Drop the entry when the sidecar is gone OR its building is no longer live
                // (D1 root fix): a dead-building sidecar previously survived here — the entry
                // then resolved to an unrepairable plant. One liveness contract with the map
                // populate and the resolver.
                bool stale = !m_EquipmentWearLookup.TryGetComponent(entity, out var wear)
                    || !IsLivePlantEntity(entity, wear);
                if (stale)
                {
                    m_PlantIdToEntity.Remove(keys[i]);
                    removedCount++;
                }
            }
            if (keys.IsCreated) keys.Dispose();

            if (removedCount > 0 && Log.IsDebugEnabled)
                Log.Debug($"Cleaned {removedCount} stale plant entries, map size: {m_PlantIdToEntity.Count}");
        }

        // ================================================================
        // Lifecycle
        // ================================================================

        protected override void OnBecameEnabled()
        {
            if (m_SuppressInitialEnabledRebase)
            {
                m_SuppressInitialEnabledRebase = false;
                return;
            }

            m_LastGameHour = -1.0;
            m_PlantMapCleanupCounter = 0;
        }

        protected override void OnBecameDisabled()
        {
            m_SuppressInitialEnabledRebase = false;

            if (m_HasPendingResults)
            {
                m_PendingJobHandle.Complete();
                m_HasPendingResults = false;
            }
        }

        private void InitializeGate()
        {
            m_Gate = new ActGateController(
                isOpenFor: act => act >= Act.Crisis,
                onTransition: HandleGateTransition);
        }

        private void HandleGateTransition(ActGateState _, ActGateState next, bool isInitial)
        {
            if (next == ActGateState.Active)
            {
                if (isInitial)
                {
                    m_SuppressInitialEnabledRebase = true;
                    return;
                }

                ResetThrottleCounter();
                ForceNextUpdate();
                Log.Info("[EquipmentWear] Gate opened");
            }
            else if (next == ActGateState.Inactive && !isInitial)
            {
                m_SuppressInitialEnabledRebase = false;
                Log.Info("[EquipmentWear] Gate closed");
            }
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IPlantWearReader>(this);
            }

            if (m_HasPendingResults)
            {
                m_PendingJobHandle.Complete();
                m_HasPendingResults = false;
            }

            if (m_PlantIdToEntity.IsCreated) m_PlantIdToEntity.Dispose();
            if (m_WearEntities.IsCreated) m_WearEntities.Dispose();
            if (m_WearInputs.IsCreated) m_WearInputs.Dispose();
            if (m_WearOutputs.IsCreated) m_WearOutputs.Dispose();
            if (m_PendingEntities.IsCreated) m_PendingEntities.Dispose();
            // FrameMutationDedup is process-lifetime singleton owned by Mod — do not dispose.

            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }
}
