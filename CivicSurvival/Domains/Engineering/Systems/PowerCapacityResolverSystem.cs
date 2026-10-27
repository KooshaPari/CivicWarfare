using System;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Logic;
using CivicSurvival.Domains.Engineering.Pipelines.PowerCapacity;

namespace CivicSurvival.Domains.Engineering.Systems
{
    [ActIndependent]
    [HotPathSystem]
    [SingletonOwner(typeof(DemandPeakSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class PowerCapacityResolverSystem : ThrottledSystemBase, IPowerCapacityPipeline, IPowerCapacitySnapshotReader, IImportCapVersionReader, IPostLoadValidation, ICivicSingletonOwner<DemandPeakSingleton>
    {
        public void PurgeAfterLoad() { }

        private const uint FLOW_CYCLE_FRAMES = 128u;
        private const int FLOW_APPLY_PHASE_START = 126;

        // Efficiency-buffer slot the mod folds its grid-producer reduction into. 26 =
        // CityModifierHospitalEfficiency, which vanilla never writes on power plants (decompile-
        // verified) and which survives PowerPlantAISystem's per-tick round-trip (it rewrites only
        // slots 17-20). Must stay < EfficiencyFactor.Count (32) — vanilla's stackalloc float[32].
        // The slot carries ourFactor / Π max(1, slot_i) — the foreign-boost compensation, not the
        // raw factor; snapshots and effectiveCapacity stay on the raw factor (allowed semantics).
        private const EfficiencyFactor ModDamageEfficiencyFactor = (EfficiencyFactor)26;
        // SetEfficiencyFactor itself treats |factor - 1| <= 0.001 as the neutral/remove case;
        // mirror that tolerance so the no-op fast path matches vanilla's idempotency boundary.
        private const float EfficiencyEpsilon = 0.001f;
        // kW→MW divisor for the saturation fleet aggregates (demand / nameplate fed to SaturationLogic in MW).
        private const float KwPerMw = 1000f;

        // SURPLUS dead-band: hold width = max(MIN_MW, value / FRACTION) — wide enough to
        // swallow weather/boost wobble (a few MW), narrow enough to pass real fleet moves.
        private const int CITY_DISPATCHABLE_DEADBAND_MIN_MW = 10;
        private const int CITY_DISPATCHABLE_DEADBAND_FRACTION = 200;

        private static readonly LogContext Log = new("PowerCapacityResolverSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;

        private readonly VersionedView<PowerCapacitySnapshot> m_View = new(PowerCapacitySnapshot.Empty);
        private readonly VersionedView<int> m_ImportCapView = new(0);
        private PowerCapacitySnapshot m_LatestSnapshot = PowerCapacitySnapshot.Empty;
        private bool m_HasPublishedSnapshot;
        // Flow-edge retry latch. Still live: the OutsideConnection and the
        // grid-producer-without-Efficiency-buffer fallback paths keep a direct
        // ElectricityFlowEdge.m_Capacity write that must be retried off the solver
        // snapshot frame. Grid producers WITH an Efficiency buffer no longer write
        // the flow edge — vanilla folds the mod factor into producer capacity itself.
        [System.NonSerialized] private bool m_FlowEdgeDirty;
        [System.NonSerialized] private int m_LastObservedImportCapKW = int.MinValue;
        [System.NonSerialized] private bool m_LastObservedImportCapKnown;
        // Per-tick channel-correct nameplate aggregate (Σ PlantBaseCapacity.OriginalCapacity over
        // GridProducer-channel plants), kW. Derived state — recomputed every throttle tick, not
        // persisted. Authored by the resolve pass (PlantResolveJob → PublishResolveResults; always,
        // regardless of the saturation toggle) so
        // Фаза 7 (surplus-attracts-strikes) sees nameplate even when degradation is disabled.
        [System.NonSerialized] private int m_FleetNameplateKW;
        // Damage-residual companion of m_FleetNameplateKW (Σ nameplate × damage multiplier over
        // the same plants) — the saturation ratio base. Diagnostic-only mirror for the
        // [Saturation] debug line; the authoritative value is computed inside
        // ComputeFleetTargetFactor each tick.
        [System.NonSerialized] private long m_FleetResidualKW;
        // Per-tick fleet saturation TARGET factor published into the aggregate snapshot (Фаза 4 КПД).
        [System.NonSerialized] private float m_FleetTargetFactor = 1f;

        // --- Daily Peak (Фаза 3) ---
        // Restored ring buffer staged in Deserialize, written into the singleton in OnLoadRestore
        // (the singleton entity isn't guaranteed to exist during Deserialize). Mirrors
        // GridStressSystem.m_RestoredGridStressData.
        [System.NonSerialized] private DemandPeakPersistState m_RestoredDemandPeak;
        [System.NonSerialized] private bool m_HasRestoredDemandPeak;
        // Lazy reconcile latch: set in Deserialize/OnLoadRestore, drained at the top of the first
        // OnThrottledUpdate after load. The reconcile CANNOT run in ValidateAfterLoad because the
        // resolver hydrates (HydrationOrder=25) BEFORE PowerGridDataSystem republishes Demand
        // (HydrationOrder=100), so Demand reads 0 there. Mirrors
        // MaintenanceContractSystem.m_ReconcilePendingProcurementAfterLoad.
        [System.NonSerialized] private bool m_DemandPeakReconcilePending;

        private EntityQuery m_ResolvedPlantQuery;
        private EntityQuery m_PowerGridQuery;
        private EntityQuery m_GridStressQuery;
        private EntityQuery m_UnderConstructionQuery;
        private EntityQuery m_DisabledByDisasterQuery;
        private EntityQuery m_EquipmentWearQuery;
        private EntityQuery m_PowerPlantDamageQuery;
        private EntityQuery m_CollapsedProducerQuery;
        private EntityQuery m_DistrictPowerQuery;
        private EntityQuery m_ExternalPowerInputQuery;
        private EntityQuery m_ShadowExportStateQuery;
        private EntityQuery m_DemandPeakQuery;
        private EntityQuery m_TradeMarkerQuery;

        private ComponentLookup<ElectricityProducer> m_ProducerLookup;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<PowerPlantKind> m_PlantKindLookup;
        private ComponentLookup<PowerCapacityIndexState> m_IndexStateLookup;
        private ComponentLookup<GridStressModifier> m_GridStressModifierLookup;
        private ComponentLookup<ConstructionModifier> m_ConstructionModifierLookup;
        private ComponentLookup<EquipmentWearModifier> m_WearModifierLookup;
        private ComponentLookup<OperationalDamageModifier> m_OperationalDamageModifierLookup;
        private ComponentLookup<DisasterDamageModifier> m_DisasterDamageModifierLookup;
        private ComponentLookup<SaturationModifier> m_SaturationModifierLookup;
        private ComponentLookup<ImportCapModifier> m_ImportCapModifierLookup;
        private ComponentLookup<Game.Net.OutsideConnection> m_OutsideConnectionLookup;
        private ComponentLookup<ElectricityBuildingConnection> m_ConnectionLookup;
        private ComponentLookup<ElectricityFlowEdge> m_FlowEdgeLookup;
        private ComponentLookup<ElectricityNodeConnection> m_NodeConnectionLookup;
        private ComponentLookup<Game.Common.Owner> m_OwnerLookup;
        private BufferLookup<ConnectedFlowEdge> m_FlowConnectionLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private BufferLookup<SubObject> m_SubObjectLookup;
        private BufferLookup<InstalledUpgrade> m_InstalledUpgradeLookup;
        private BufferLookup<Efficiency> m_EfficiencyLookup;
        private BufferLookup<DemandPeakBucket> m_DemandPeakBucketLookup;
        private ComponentLookup<DemandPeakSingleton> m_DemandPeakLookup;
        private EntityStorageInfoLookup m_EntityStorageInfoLookup;
        private ComponentLookup<PowerPlantData> m_PowerPlantDataLookup;
        private ComponentLookup<EmergencyGeneratorData> m_EmergencyGeneratorDataLookup;
        private ComponentLookup<BatteryData> m_BatteryDataLookup;
        private ComponentLookup<WindPoweredData> m_WindPoweredDataLookup;
        private ComponentLookup<SolarPoweredData> m_SolarPoweredDataLookup;
        private ComponentLookup<GarbagePoweredData> m_GarbagePoweredDataLookup;
        private ComponentLookup<WaterPoweredData> m_WaterPoweredDataLookup;
        private ComponentLookup<Game.Buildings.WaterPowered> m_WaterPoweredLookup;
        private ComponentLookup<GroundWaterPoweredData> m_GroundWaterPoweredDataLookup;
        private ComponentLookup<EquipmentWear> m_EquipmentWearLookup;
        private ComponentLookup<PowerPlantDamage> m_PowerPlantDamageLookup;
        // Сырьё-сигмоида (Фаза 2): RO byte 0-255, доля заполнения топливного склада ТЭЦ.
        // Читается на лету в PlantResolveJob (RO в job-графе); не инерционно, не persisted
        // (мгновенная физика котла).
        private ComponentLookup<Game.Buildings.ResourceConsumer> m_ResourceConsumerLookup;

        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ElectricityFlowSystem m_ElectricityFlowSystem = null!;
        // --- PlantResolveJob plumbing (frame N → N+1, Axiom 9) ---
        // The job scheduled at tick N is consumed at tick N+1 (~500 ms later, long finished on
        // workers), so the Complete() in ConsumePendingResolve is dependency bookkeeping, not a
        // graph drain. All containers are Persistent, owned OnCreate→OnDestroy.
        [System.NonSerialized] private JobHandle m_ResolveJobHandle;
        [System.NonSerialized] private bool m_HasPendingResolveResults;
        [System.NonSerialized] private bool m_PendingWasAfterLoad;
        private NativeList<PlantWork> m_PlantWorkInput;
        private NativeList<PowerCapacityPlantSnapshot> m_PendingPlantRows;
        private NativeList<PendingFlowEdgeWrite> m_PendingEdgeWrites;
        private NativeReference<ResolveAggregates> m_PendingAggregates;
        // Export-edge lookup diagnostics: a persistent Unresolved streak means the
        // vanilla route (marker -> owner node -> edge to sinkNode) is broken and the
        // cap silently stops applying — that must reach the log exactly once.
        [System.NonSerialized] private int m_ExportEdgeUnresolvedStreak;
        [System.NonSerialized] private bool m_ExportEdgeWarned;
        // Dead-band hold for the published SURPLUS aggregate: the live per-tick sum
        // inherits vanilla weather slots (wind/sun recomputed continuously) and the
        // drifting EmployeeHappiness boost chased with a 500 ms lag, so the raw value
        // wobbles by a few MW every tick and the UI number flickers. Consumers get the
        // held value; real moves (plant lost/built, saturation ramp) exceed the band
        // and snap through. Identity with INFRA OUTPUT holds within the band width.
        [System.NonSerialized] private int m_PublishedCityDispatchableMW;
        // Vanilla hard dependency. World.GetOrCreateSystemManaged returns the existing
        // system or creates one (Unity.Entities.World.GetOrCreateSystemInternal); it
        // cannot return null — failure to instantiate throws at OnCreate, which is
        // why frameIndex reads in OnThrottledUpdate are unguarded. Vanilla systems
        // (AudioManager, EffectFlagSystem, …) read frameIndex the same way after
        // the same OnCreate pattern.
        private SimulationSystem m_SimulationSystem = null!;
        private PrefabSystem m_PrefabSystem = null!;
        private ModSettings m_Settings = null!;

        private static int s_LastResolvedCount;
        private static int s_LastNewPlantsCount;
        public static int LastResolvedCount => s_LastResolvedCount;
        public static int LastNewPlantsCount => s_LastNewPlantsCount;

        public PowerCapacitySnapshot LatestSnapshot => m_LatestSnapshot;
        public int PublishedVersion => m_View.Version;
        public int Version => PublishedVersion;
        public int DispatchableMW => m_LatestSnapshot.DispatchableMW;
        public IVersionedView<int> ImportCapView => m_ImportCapView;

        public bool TryGetSnapshot(out PowerCapacitySnapshot snapshot)
        {
            snapshot = m_LatestSnapshot;
            return m_HasPublishedSnapshot;
        }

        private static void SetLastNewPlantsCount(int count) => s_LastNewPlantsCount = count;
        private static void SetLastResolvedCount(int count) => s_LastResolvedCount = count;

        protected override void OnCreate()
        {
            base.OnCreate();

            SetLastResolvedCount(0);
            SetLastNewPlantsCount(0);

#pragma warning disable CIVIC403 // PowerCapacity requires fail-loud service registration; Mod.OnLoad registers these before SystemRegistrar.RegisterAll.
            m_Settings = ServiceRegistry.Instance.Require<ModSettings>();
#pragma warning restore CIVIC403
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ElectricityFlowSystem = World.GetOrCreateSystemManaged<ElectricityFlowSystem>();

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IPowerCapacityPipeline>(this);
                ServiceRegistry.Instance.Register<IPowerCapacitySnapshotReader>(this);
                ServiceRegistry.Instance.Register<IImportCapVersionReader>(this);
            }

            m_ResolvedPlantQuery = GetEntityQuery(
                ComponentType.ReadWrite<ElectricityProducer>(),
                ComponentType.ReadOnly<PlantBaseCapacity>(),
                ComponentType.ReadOnly<PowerCapacityIndexState>(),
                ComponentType.Exclude<Deleted>());
            m_PowerGridQuery = GetEntityQuery(ComponentType.ReadOnly<PowerGridSingleton>());
            m_GridStressQuery = GetEntityQuery(ComponentType.ReadOnly<GridStressData>());
            m_UnderConstructionQuery = GetEntityQuery(ComponentType.ReadOnly<UnderConstruction>(), ComponentType.Exclude<Deleted>());
            m_DisabledByDisasterQuery = GetEntityQuery(ComponentType.ReadOnly<DisabledByDisaster>(), ComponentType.Exclude<Deleted>());
            m_EquipmentWearQuery = GetEntityQuery(ComponentType.ReadWrite<EquipmentWear>(), ComponentType.Exclude<Deleted>());
            m_PowerPlantDamageQuery = GetEntityQuery(ComponentType.ReadOnly<PowerPlantDamage>(), ComponentType.Exclude<Deleted>());
            m_CollapsedProducerQuery = GetEntityQuery(ComponentType.ReadOnly<CollapsedProducer>(), ComponentType.Exclude<Deleted>());
            m_DistrictPowerQuery = GetEntityQuery(ComponentType.ReadOnly<DistrictPowerBufferSingleton>());
            m_ExternalPowerInputQuery = GetEntityQuery(ComponentType.ReadOnly<ExternalPowerInput>());
            m_ShadowExportStateQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowExportState>());
            m_DemandPeakQuery = GetEntityQuery(ComponentType.ReadWrite<DemandPeakSingleton>());
            // ExportCap: outside-connection trade markers. Excluding Temp/Deleted is
            // mandatory — vanilla excludes Temp on both legs of the same route
            // (ElectricityOutsideConnectionGraphSystem and the legacy load path in
            // ElectricityFlowSystem). A tool-preview marker (dragging a line near the
            // map edge) has an owner without ElectricityNodeConnection and would feed
            // a false Unresolved streak, burning the one-shot warn — the same bug
            // class as the phantom plants from tool previews in PowerCapacityIndexSystem.
            m_TradeMarkerQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Objects.ElectricityOutsideConnection>(),
                ComponentType.ReadOnly<Game.Common.Owner>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());

            m_ProducerLookup = GetComponentLookup<ElectricityProducer>(false);
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(false);
            m_PlantKindLookup = GetComponentLookup<PowerPlantKind>(false);
            m_IndexStateLookup = GetComponentLookup<PowerCapacityIndexState>(true);
            m_GridStressModifierLookup = GetComponentLookup<GridStressModifier>(false);
            m_ConstructionModifierLookup = GetComponentLookup<ConstructionModifier>(false);
            m_WearModifierLookup = GetComponentLookup<EquipmentWearModifier>(false);
            m_OperationalDamageModifierLookup = GetComponentLookup<OperationalDamageModifier>(false);
            m_DisasterDamageModifierLookup = GetComponentLookup<DisasterDamageModifier>(false);
            m_SaturationModifierLookup = GetComponentLookup<SaturationModifier>(false);
            m_ImportCapModifierLookup = GetComponentLookup<ImportCapModifier>(false);
            m_OutsideConnectionLookup = GetComponentLookup<Game.Net.OutsideConnection>(true);
            m_ConnectionLookup = GetComponentLookup<ElectricityBuildingConnection>(true);
            m_FlowEdgeLookup = GetComponentLookup<ElectricityFlowEdge>(true);
            m_NodeConnectionLookup = GetComponentLookup<ElectricityNodeConnection>(true);
            m_OwnerLookup = GetComponentLookup<Game.Common.Owner>(true);
            m_FlowConnectionLookup = GetBufferLookup<ConnectedFlowEdge>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_SubObjectLookup = GetBufferLookup<SubObject>(true);
            m_InstalledUpgradeLookup = GetBufferLookup<InstalledUpgrade>(true);
            m_EfficiencyLookup = GetBufferLookup<Efficiency>(false);
            m_DemandPeakBucketLookup = GetBufferLookup<DemandPeakBucket>(false);
            m_DemandPeakLookup = GetComponentLookup<DemandPeakSingleton>(false);
            m_EntityStorageInfoLookup = GetEntityStorageInfoLookup();
            m_PowerPlantDataLookup = GetComponentLookup<PowerPlantData>(true);
            m_EmergencyGeneratorDataLookup = GetComponentLookup<EmergencyGeneratorData>(true);
            m_BatteryDataLookup = GetComponentLookup<BatteryData>(true);
            m_WindPoweredDataLookup = GetComponentLookup<WindPoweredData>(true);
            m_SolarPoweredDataLookup = GetComponentLookup<SolarPoweredData>(true);
            m_GarbagePoweredDataLookup = GetComponentLookup<GarbagePoweredData>(true);
            m_WaterPoweredDataLookup = GetComponentLookup<WaterPoweredData>(true);
            m_WaterPoweredLookup = GetComponentLookup<Game.Buildings.WaterPowered>(true);
            m_GroundWaterPoweredDataLookup = GetComponentLookup<GroundWaterPoweredData>(true);
            m_EquipmentWearLookup = GetComponentLookup<EquipmentWear>(false);
            m_PowerPlantDamageLookup = GetComponentLookup<PowerPlantDamage>(true);
            m_ResourceConsumerLookup = GetComponentLookup<Game.Buildings.ResourceConsumer>(true);

            // Daily Peak (Фаза 3): own the persisted 24h demand-peak ring. Ensure the singleton
            // entity + DynamicBuffer<DemandPeakBucket> exists from the start (UI / Waves may read it).
            DemandPeakSingleton.EnsureExists(EntityManager);

            m_PlantWorkInput = new NativeList<PlantWork>(64, Allocator.Persistent);
            m_PendingPlantRows = new NativeList<PowerCapacityPlantSnapshot>(64, Allocator.Persistent);
            m_PendingEdgeWrites = new NativeList<PendingFlowEdgeWrite>(16, Allocator.Persistent);
            m_PendingAggregates = new NativeReference<ResolveAggregates>(Allocator.Persistent);

            Log.Info("Created (grid-producer reduction via Efficiency factor; vanilla owns m_Capacity)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE: OnCreate doesn't re-run on new-game; re-ensure the
            // owned Daily Peak ring here so a fresh city always has the 24-bucket buffer.
            DemandPeakSingleton.EnsureExists(EntityManager);
        }

        protected override void OnThrottledUpdate()
        {
            RefreshLookups();
            ObserveImportCapVersion();

            // Lazy post-load reconcile of the demand-peak ring — runs once, here (NOT in
            // ValidateAfterLoad): PowerGridSingleton.Demand is fresh by now (PowerGridDataSystem
            // hydrated at order 100, after the resolver's 25), whereas it reads 0 in ValidateAfterLoad.
            ReconcileDemandPeakAfterLoadIfPending();

            var ctx = CreatePipelineContext(IsSafeFrameForFlowEdgeWrite());
            try
            {
                // Frame N → N+1 (Axiom 9): publish the resolve scheduled on the PREVIOUS
                // throttled tick, then run the main-thread phases, then schedule the next one.
                // The retired layout drained the vanilla ElectricityProducer/Efficiency/
                // ResourceConsumer fences here instead — measured at 78% of the system's
                // 13 ms/throttled-tick main-thread cost (SP:PCR.* split, 2026-06-12);
                // PlantResolveJob now reads those components under job-graph ordering.
                ConsumePendingResolve(ref ctx);

                // Export trade edges are capped on every safe-frame tick regardless of the
                // fleet (matches the retired layout, where this ran inside ResolveAndPublish
                // before the empty-fleet early return).
                EnforceExportCaps(ref ctx);

                ApplyGridStressModifier(ref ctx);
                ApplyConstructionDelay(ref ctx, afterLoad: false);
                ApplyDisasterModifier(ref ctx);
                ApplyWearAndRepair(ref ctx);
                ApplySaturationInertia(ref ctx, afterLoad: false);

                ScheduleResolve(ref ctx, afterLoad: false);

                // The ECB (flow-edge applies + export caps) is filled on the MAIN THREAD above,
                // so the producer handle is default. The resolve job handle is deliberately NOT
                // registered with the barrier: AddJobHandleForProducer(m_ResolveJobHandle) would
                // make GameSimulationEndBarrier force-complete the whole vanilla Efficiency
                // chain on the main thread, recreating the drain this job replaced (the
                // ThreatLifecycleBarrier piggy-back lesson).
                if (ctx.HasEcb)
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(default(JobHandle));
            }
            finally
            {
                ctx.DisposeCachedResolvedPlantEntities();
            }
        }

        [CompletesDependency("PowerCapacityResolver consume: completes ONLY the resolver's own PlantResolveJob handle scheduled one throttle tick (~500 ms) earlier — long finished on workers, so this is dependency bookkeeping, not a graph drain. The vanilla Efficiency/Producer/ResourceConsumer chain is never force-completed here; that main-thread drain is exactly what the scheduled-job layout removed.")]
        private void ConsumePendingResolve(ref PowerCapacityPipelineContext ctx)
        {
            if (!m_HasPendingResolveResults)
                return;

            // A/B-verified 2026-06-13 (SP:PCR.Consume probe, 0.0 ms over 381 calls): the handle
            // is done by consume time, this Complete is pure dependency bookkeeping.
            m_ResolveJobHandle.Complete();
            m_HasPendingResolveResults = false;

            PublishResolveResults(ref ctx);
        }

        /// <summary>
        /// Main-thread half of the resolve: turns the job's native outputs into the published
        /// managed snapshot, applies the flow-edge decisions via ECB, surfaces the job's
        /// never-fires detector, and emits the debug log lines (no managed logging inside Burst).
        /// Runs at tick N+1 for a live tick and synchronously after <c>Run()</c> on the
        /// post-load path.
        /// </summary>
        private void PublishResolveResults(ref PowerCapacityPipelineContext ctx)
        {
            ResolveAggregates agg = m_PendingAggregates.Value;

            int edgesUpdated = ApplyPendingEdgeWrites(ref ctx, agg.SnapshotCount);

            if (agg.NoBufferWarnEntity != Entity.Null)
            {
                // INVARIANT-DETECTOR (intentional, keep): the job hit the direct-write fallback
                // on a grid producer without an Efficiency buffer. No vanilla grid plant lacks
                // the buffer, so this must never fire — it exists to surface a third-party
                // prefab without CityServiceBuilding if one ever appears. Zero lines = dead
                // path confirmed.
                Log.Warn($"GridProducer {agg.NoBufferWarnEntity.Index} has no Efficiency buffer — direct-write m_Capacity fallback (third-party prefab without CityServiceBuilding?); single-writer Efficiency path skipped.");
            }

            // Authoritative nameplate for Фаза 7 — recomputed every tick regardless of the
            // saturation toggle. Clamp to int range: a fleet sum cannot realistically reach
            // 2.1 TW, but the clamp makes the long→int narrowing provably safe (CIVIC136).
            int nameplateKW = (int)math.min(agg.NameplateSumKW, int.MaxValue);
            m_FleetNameplateKW = nameplateKW;
            SetLastResolvedCount(agg.ResolvedCount);
            int dispatchableMW = (int)((agg.DispatchableSumKW + PowerCapacityMath.KW_ROUND_HALF) / 1000);
            int cityDispatchableMW = (int)math.min(agg.CityDispatchableSumMW, int.MaxValue);
            // Dead-band publication (see m_PublishedCityDispatchableMW): hold the last
            // published value while the live sum stays within max(MIN_MW, 0.5%) of it,
            // snap unconditionally right after load.
            int deadBandMW = math.max(CITY_DISPATCHABLE_DEADBAND_MIN_MW,
                cityDispatchableMW / CITY_DISPATCHABLE_DEADBAND_FRACTION);
            if (m_PendingWasAfterLoad || math.abs(cityDispatchableMW - m_PublishedCityDispatchableMW) >= deadBandMW)
                m_PublishedCityDispatchableMW = cityDispatchableMW;

            var snapshots = new PowerCapacityPlantSnapshot[m_PendingPlantRows.Length];
            for (int i = 0; i < m_PendingPlantRows.Length; i++)
                snapshots[i] = m_PendingPlantRows[i];

            Publish(dispatchableMW, snapshots, nameplateKW, agg.FleetTargetFactor, agg.LargestPlantKW,
                math.countbits(agg.IntermittentTypeMask), m_PublishedCityDispatchableMW);

            if (agg.ResolvedCount > 0 && Log.IsDebugEnabled)
                Log.Debug($"Resolved {agg.ResolvedCount} capacities, {edgesUpdated} edges queued via ECB (safe frame: {ctx.IsSafeFrame})");

            if (agg.CollectedLossBreakdown)
                Log.Debug($"[CapacityLoss] nameplate={nameplateKW / KwPerMw:F1}MW knockedOut={agg.Losses.KnockedOutKW / KwPerMw:F1}MW damage=-{agg.Losses.DamageLossKW / KwPerMw:F1}MW saturation=-{agg.Losses.SaturationLossKW / KwPerMw:F1}MW fuel=-{agg.Losses.FuelLossKW / KwPerMw:F1}MW allowed={agg.Losses.AllowedKW / KwPerMw:F1}MW delivered={agg.DeliveredKW / KwPerMw:F1}MW undelivered={(agg.Losses.AllowedKW - agg.DeliveredKW) / KwPerMw:F1}MW boost={(agg.HealthyKW > 0 ? agg.BoostWeighted / agg.HealthyKW : 1.0):F2}");
        }

        /// <summary>
        /// Applies the job's flow-edge decisions (OutsideConnection + no-buffer fallback) via
        /// ECB, owning the safe-frame gate and the <c>m_FlowEdgeDirty</c> retry latch exactly as
        /// the retired in-loop layout did. <c>TryUpdateFlowEdgeViaEcb</c> re-checks the live
        /// edge value, so applying a one-tick-old target stays idempotent.
        /// </summary>
        private int ApplyPendingEdgeWrites(ref PowerCapacityPipelineContext ctx, int snapshotCount)
        {
            bool reconcileFlowEdges = ctx.IsSafeFrame && m_FlowEdgeDirty;
            bool needsFlowEdgeRetry = !ctx.IsSafeFrame && m_FlowEdgeDirty;
            int edgesUpdated = 0;

            for (int i = 0; i < m_PendingEdgeWrites.Length; i++)
            {
                PendingFlowEdgeWrite entry = m_PendingEdgeWrites[i];
                if (!entry.CapacityChanged && !reconcileFlowEdges)
                    continue;

                if (ctx.IsSafeFrame)
                {
                    ctx.EnsureEcb();
                    var result = PowerCapacityMath.TryUpdateFlowEdgeViaEcb(ref ctx, entry.Plant, entry.CapacityKW);
                    switch (result)
                    {
                        case FlowEdgeUpdateResult.Updated:
                            edgesUpdated++;
                            break;
                        case FlowEdgeUpdateResult.Unresolved:
                            needsFlowEdgeRetry = true;
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    needsFlowEdgeRetry = true;
                }
            }

            if (snapshotCount == 0)
                needsFlowEdgeRetry = false;

            m_FlowEdgeDirty = needsFlowEdgeRetry;
            return edgesUpdated;
        }

        /// <summary>
        /// Builds the job input on the main thread and schedules <see cref="PlantResolveJob"/>
        /// on <c>Dependency</c> (live tick) or runs it inline (post-load). The work list
        /// snapshots every mod-owned input — modifier sidecars, prefab classification, config —
        /// so the job touches ONLY the three vanilla components it declares.
        /// </summary>
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ResolveAndPublish)]
        [CompletesDependency("PowerCapacityResolver schedule: throttled main-thread snapshot of mod-owned modifier sidecars and static prefab classification into the PlantResolveJob input; the empty-fleet branch publishes synchronously. Vanilla components are deliberately NOT read here — the job reads them under job-graph ordering (Dependency chaining replaces the retired per-tick fence drains).")]
        private void ScheduleResolve(ref PowerCapacityPipelineContext ctx, bool afterLoad)
        {
            BuildPlantWork(ref ctx, afterLoad);

            if (m_PlantWorkInput.IsEmpty)
            {
                // Empty fleet publishes synchronously (nothing to read from vanilla) — matches
                // the retired early return, including the latch resets.
                m_FlowEdgeDirty = false;
                m_FleetNameplateKW = 0;
                m_PublishedCityDispatchableMW = 0;
                SetLastResolvedCount(0);
                Publish(0, Array.Empty<PowerCapacityPlantSnapshot>(), 0, 1f);
                return;
            }

            m_PendingPlantRows.Clear();
            m_PendingEdgeWrites.Clear();
            m_PendingAggregates.Value = default;

            var balance = Core.Config.BalanceConfig.Current;
            var fuelCfg = balance.FuelCurve;
            var satCfg = balance.GenerationSaturation;
            var job = new PlantResolveJob
            {
                Work = m_PlantWorkInput,
                ProducerLookup = m_ProducerLookup,
                EfficiencyLookup = m_EfficiencyLookup,
                ResourceConsumerLookup = m_ResourceConsumerLookup,
                AfterLoad = afterLoad,
                IsSafeFrame = ctx.IsSafeFrame,
                // m_LastProduction is not covered by job inputs on the afterLoad path semantics,
                // and managed logging is unavailable in the job — the breakdown is debug-gated
                // at schedule time and surfaced from the aggregates at consume time.
                CollectLossBreakdown = !afterLoad && Log.IsDebugEnabled,
                FleetTargetFactor = m_FleetTargetFactor,
                FuelCurve = new FuelCurveJobParams
                {
                    Enabled = fuelCfg.Enabled,
                    BufferThreshold = fuelCfg.BufferThreshold,
                    MinOutputAtZero = fuelCfg.MinOutputAtZero,
                    AnchorFrac = fuelCfg.AnchorFrac,
                    AnchorOutput = fuelCfg.AnchorOutput,
                    SteepnessLow = fuelCfg.SteepnessLow,
                    SteepnessHigh = fuelCfg.SteepnessHigh,
                },
                Saturation = new SaturationJobParams
                {
                    Enabled = satCfg.Enabled,
                    Hysteresis = satCfg.Hysteresis,
                    TauUpHours = satCfg.TauUpHours,
                },
                ImportCap = new ImportCapJobParams
                {
                    HasPublishedImportCap = ImportCapRuntimeState.HasPublishedImportCap,
                    CurrentImportCapKW = ImportCapRuntimeState.CurrentImportCapKW,
                },
                Snapshots = m_PendingPlantRows,
                PendingEdgeWrites = m_PendingEdgeWrites,
                Aggregates = m_PendingAggregates,
            };

            if (afterLoad)
            {
                // Post-load must publish synchronously (HydrationOrder=25, readers at 30+).
                // SystemBase.Dependency is unavailable outside OnUpdate, so the job's component
                // dependencies are completed explicitly; vanilla simulation is paused during
                // ValidateAfterLoad, making these instant fence bookkeeping — NOT the per-tick
                // drain the scheduled path removed.
                EntityManager.CompleteDependencyBeforeRW<ElectricityProducer>();
                EntityManager.CompleteDependencyBeforeRW<Efficiency>();
                EntityManager.CompleteDependencyBeforeRO<Game.Buildings.ResourceConsumer>();
                job.Run();
                m_PendingWasAfterLoad = true;
                PublishResolveResults(ref ctx);
                m_PendingWasAfterLoad = false;
                return;
            }

            // Ordering comes from the job graph: the incoming Dependency already chains every
            // registered reader/writer of ElectricityProducer + Efficiency + ResourceConsumer
            // (declared via this system's lookups), and assigning it back hands our handle to
            // downstream vanilla systems. Decompile-verified (CS2 1.5.5, 2026-06-12): no vanilla
            // system completes the Efficiency chain on the main thread per-frame in normal play
            // (26 readers + 20 writers are all job schedulers; the 4 main-thread accessors are
            // selection/debug/editor-gated and RO), so nothing downstream inherits the wait.
            m_ResolveJobHandle = job.Schedule(Dependency);
            Dependency = m_ResolveJobHandle;
            m_HasPendingResolveResults = true;
        }

        /// <summary>
        /// Snapshots the per-plant mod-owned inputs (modifier sidecars via
        /// <see cref="ReadCapacityState"/>, channel/nameplate/kind, prefab classification) into
        /// <see cref="PlantWork"/>. Runs after the Apply* phases so the job sees this tick's
        /// hydrated modifier values — and BECAUSE it runs on the main thread, the job never
        /// races the mod's own main-thread modifier writers (this system's Apply* phases,
        /// ConstructionDelaySystem, OperationalDamageSystem, …).
        /// </summary>
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver work-list build: throttled main-thread materialisation of indexed producers into the PlantResolveJob input from mod-owned sidecar components and static prefab data only.")]
        private void BuildPlantWork(ref PowerCapacityPipelineContext ctx, bool afterLoad)
        {
            m_PlantWorkInput.Clear();
            var entities = ctx.GetResolvedPlantEntities();
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (!m_BaseCapacityLookup.TryGetComponent(entity, out var baseCap)
                    || !m_IndexStateLookup.TryGetComponent(entity, out var indexState))
                    continue;

                PlantKind kind = m_PlantKindLookup.TryGetComponent(entity, out var plantKind)
                    ? plantKind.Value
                    : PlantKind.Unclassified;

                int intermittentTypeBit = 0;
                if (indexState.Channel == CapacityChannel.GridProducer
                    && m_PrefabRefLookup.TryGetComponent(entity, out var prefabRef))
                {
                    // Managed PrefabSystem classification (per-prefab cached) is unreachable
                    // from the job — precomputed here into the diversity-mask bit.
                    var pt = PowerPlantUtils.GetPlantType(m_PrefabSystem, prefabRef);
                    if (PowerPlantUtils.IsIntermittent(pt))
                        intermittentTypeBit = 1 << (int)pt;
                }

                m_PlantWorkInput.Add(new PlantWork
                {
                    Entity = entity,
                    Channel = indexState.Channel,
                    OriginalCapacityKW = baseCap.OriginalCapacity,
                    Kind = kind,
                    State = ReadCapacityState(entity),
                    IsVariableGeneration = PowerCapacityMath.IsVariableGeneration(ref ctx, entity),
                    IntermittentTypeBit = intermittentTypeBit,
                });
            }
        }

        /// <summary>
        /// Discards a resolve scheduled by the previous city: an in-session load can land
        /// between schedule and consume, and stale results must not publish into the new city.
        /// The Efficiency/producer writes the orphaned job already performed targeted entities
        /// of the OLD world content, which deserialization replaces wholesale — harmless.
        /// </summary>
        private void InvalidatePendingResolve()
        {
            if (m_HasPendingResolveResults)
            {
                m_ResolveJobHandle.Complete();
                m_HasPendingResolveResults = false;
            }

            if (m_PendingPlantRows.IsCreated)
                m_PendingPlantRows.Clear();
            if (m_PendingEdgeWrites.IsCreated)
                m_PendingEdgeWrites.Clear();
        }

        // Publish the resolved snapshot after every split capacity modifier writer
        // (OperationalDamage=10, Disaster/Construction=20, Wear=21, assign=22) but before any
        // reader of the published snapshot (EquipmentUISystem, GridStressSystem at 30). Without
        // this the publisher fell to HydrationPriority.DEFAULT (100) — after the readers — so the
        // first post-load frame served a stale/empty snapshot.
        public int HydrationOrder => HydrationPriority.POWER_SNAPSHOT_PUBLISH;

        public void ValidateAfterLoad()
        {
            RefreshLookups();
            InvalidatePendingResolve();
            // The publication latch is [NonSerialized] and the system instance is reused across an
            // in-session load, so it carries the previous city's snapshot. Reset it before resolving
            // so the first post-load publication reflects this city's real capacity instead of the
            // prior city's last snapshot (mirrors the import-cap latch reset below).
#pragma warning disable CIVIC458
            m_HasPublishedSnapshot = false;
            m_LatestSnapshot = PowerCapacitySnapshot.Empty;
#pragma warning restore CIVIC458
            // The observation latch is [NonSerialized] and the system instance is reused across an
            // in-session load, so it carries the previous city's last-observed import cap. Reset it
            // before observing so the first post-load publication reflects this city's real
            // ImportCapRuntimeState instead of a delta from the prior city (which would otherwise
            // publish the int.MinValue sentinel when this city has no import cap yet).
            m_LastObservedImportCapKnown = false;
            m_LastObservedImportCapKW = int.MinValue;
            // Export-edge diagnostics latches are transient and the system instance is
            // reused across an in-session load: a warn burned in the previous city would
            // otherwise silence the diagnostics of the new one.
            m_ExportEdgeUnresolvedStreak = 0;
            m_ExportEdgeWarned = false;
            ObserveImportCapVersion();
            var ctx = CreatePipelineContext(IsSafeFrameForFlowEdgeWrite());
            try
            {
                ApplyGridStressModifier(ref ctx);
                ApplyConstructionDelay(ref ctx, afterLoad: true);
                ApplyDisasterModifier(ref ctx);
                ApplyWearAndRepair(ref ctx);
                ApplySaturationInertia(ref ctx, afterLoad: true);

                // Matches the retired layout, where the export-cap pass ran at the top of
                // ResolveAndPublish on this path too (a city with outside connections but no
                // resolved plant sidecars yet must still get its export trade edges capped).
                EnforceExportCaps(ref ctx);

                // afterLoad runs the resolve job INLINE (Run + immediate publish) so the
                // snapshot is fresh for the hydration readers at order 30+ on this same pass.
                ScheduleResolve(ref ctx, afterLoad: true);

                if (ctx.HasEcb)
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(default(JobHandle));
            }
            finally
            {
                ctx.DisposeCachedResolvedPlantEntities();
            }
        }

        private void ObserveImportCapVersion()
        {
            bool hasPublishedImportCap = ImportCapRuntimeState.HasPublishedImportCap;
            int importCapKW = hasPublishedImportCap ? ImportCapRuntimeState.CurrentImportCapKW : int.MinValue;
            if (hasPublishedImportCap == m_LastObservedImportCapKnown && importCapKW == m_LastObservedImportCapKW)
                return;

            m_LastObservedImportCapKnown = hasPublishedImportCap;
            m_LastObservedImportCapKW = importCapKW;
            m_ImportCapView.Publish(importCapKW);
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver grid-stress hydration: throttled sidecar-to-modifier reconciliation over indexed power producers")]
        public void ApplyGridStressModifier(ref PowerCapacityPipelineContext ctx)
        {
            using var collapsed = PowerCapacityClassifier.BuildCollapsedProducerSet(ref ctx);
            using var entities = ctx.ResolvedPlantQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (!ctx.GridStressModifierLookup.TryGetComponent(entity, out var modifier))
                    continue;
                bool isCollapsed = collapsed.Contains(BuildingIdentityKey.Pack(entity));
                if (modifier.IsCollapsed == isCollapsed)
                    continue;
                modifier.IsCollapsed = isCollapsed;
                ctx.GridStressModifierLookup[entity] = modifier;
            }
        }

        // Per-building construction state mirrored from the UnderConstruction sidecar at apply time.
        // TargetNameplateKW is the sidecar's own nameplate, used as the ramp divisor so served/target
        // stays self-consistent regardless of the index system's PlantBaseCapacity lag. The map is
        // keyed by Index|Version (BuildingIdentityKey.Pack), so a sidecar left over on a recycled Index
        // slot carries the demolished plant's old Version and simply never matches the new plant's key.
        private readonly struct ConstructionEntry
        {
            public ConstructionEntry(float progress, int baseKW, int targetNameplateKW)
            {
                Progress = progress;
                BaseKW = baseKW;
                TargetNameplateKW = targetNameplateKW;
            }

            public readonly float Progress;
            public readonly int BaseKW;
            public readonly int TargetNameplateKW;
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver construction hydration: throttled sidecar-to-modifier reconciliation over indexed power producers")]
        public void ApplyConstructionDelay(ref PowerCapacityPipelineContext ctx, bool afterLoad)
        {
            // Build building-key -> construction state from the UnderConstruction sidecars (owned by
            // ConstructionDelaySystem). Progress drives a linear capacity ramp during the build window
            // instead of a hard zero (see PowerCapacityMath.ComputeEffectiveFactor), so an emergency build gives growing
            // relief while still rewarding capacity built ahead of a crisis. Mirrors
            // ApplyDisasterModifier: derive the per-plant data from sidecar state at apply time.
            using var activeConstruction = new NativeHashMap<long, ConstructionEntry>(16, Allocator.Temp);
            if (m_Settings.ConstructionDelayEnabled)
            {
                float currentDay = GameTimeSystem.TryGetGameHours(out var gameHours)
                    ? gameHours / GameRate.HOURS_PER_DAY
                    : 0f;
                using var constructionEntities = ctx.UnderConstructionQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < constructionEntities.Length; i++)
                {
                    Entity ucEntity = constructionEntities[i];
                    if (!ctx.EntityManager.HasComponent<UnderConstruction>(ucEntity))
                        continue;
                    var uc = ctx.EntityManager.GetComponentData<UnderConstruction>(ucEntity);
                    if (uc.Building.Index <= 0)
                        continue;
                    float progress = PowerCapacityMath.ComputeConstructionProgress(uc, currentDay);
                    // Key by Index|Version: a live plant's Version is stable (ECS only re-versions a
                    // slot on destroy+reuse), so this matches the live plant; an orphan sidecar left on
                    // a recycled Index slot keeps the demolished plant's old Version and never matches
                    // the new plant. Mirrors ApplyDisasterModifier / the operational-damage map.
                    long key = BuildingIdentityKey.Pack(uc.Building.Index, uc.Building.Version);
                    // One building has at most one construction sidecar; TryAdd keeps the first
                    // (duplicates are not expected). Avoids an indexer set on the using-scoped map (CS1654).
                    activeConstruction.TryAdd(key, new ConstructionEntry(progress, uc.BaseCapacityKW, uc.OriginalCapacity));
                }
            }

            using var entities = ctx.ResolvedPlantQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                // Only reconcile plants ConstructionDelaySystem has classified. Classification
                // is a managed side-set (ConstructionClassifiedState), NOT a component on the
                // vanilla plant: adding a tag to the rendered building migrates its archetype,
                // and doing that from GameSimulation risks the render-batch Burst crash — the
                // same reason CDS keeps state on an UnderConstruction sidecar and the index
                // system's A2 dirty-set replaced a structural tag. Until CDS marks the plant,
                // leave the add-site default (IsUnderConstruction = ConstructionDelayEnabled) so
                // a brand-new plant resolves to 0 MW instead of leaking full nameplate for the
                // ~1 s before CDS first runs. Bypassed on afterLoad — every saved plant is
                // already classified, so the post-load pass reconciles all of them (sidecar ⇒
                // ramp, none ⇒ full) and the set needs no persistence. Feature off ⇒ CDS never
                // marks ⇒ bypass and fall through to the normal clear (empty activeConstruction
                // ⇒ every plant false ⇒ full MW).
                if (ctx.ConstructionDelayEnabled && !afterLoad
                    && !ConstructionClassifiedState.IsClassified(StablePlantIdentityRegistry.ClassificationKey(entity)))
                    continue;
                if (!ctx.ConstructionModifierLookup.TryGetComponent(entity, out var modifier))
                    continue;
                long buildingKey = BuildingIdentityKey.Pack(entity);
                // The Index|Version key already excludes an orphan sidecar on a recycled Index slot
                // (its key carries the demolished plant's old Version), so a hit here is this plant's
                // own window.
                bool underConstruction = activeConstruction.TryGetValue(buildingKey, out var entry);
                float progress = underConstruction ? entry.Progress : 0f;
                int baseKW = underConstruction ? entry.BaseKW : 0;
                int targetKW = underConstruction ? entry.TargetNameplateKW : 0;
                if (modifier.IsUnderConstruction == underConstruction
                    && math.abs(modifier.Progress - progress) <= 0.001f
                    && modifier.BaseCapacityKW == baseKW
                    && modifier.TargetNameplateKW == targetKW)
                    continue;
                modifier.IsUnderConstruction = underConstruction;
                modifier.Progress = progress;
                modifier.BaseCapacityKW = baseKW;
                modifier.TargetNameplateKW = targetKW;
                ctx.ConstructionModifierLookup[entity] = modifier;
            }
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver disaster hydration: throttled sidecar-to-modifier reconciliation over indexed power producers")]
        public void ApplyDisasterModifier(ref PowerCapacityPipelineContext ctx)
        {
            using var activeDisasters = new NativeHashMap<long, float>(16, Allocator.Temp);
            using (var disasterEntities = ctx.DisabledByDisasterQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < disasterEntities.Length; i++)
                {
                    Entity disasterEntity = disasterEntities[i];
                    if (!ctx.EntityManager.HasComponent<DisabledByDisaster>(disasterEntity))
                        continue;
                    var disaster = ctx.EntityManager.GetComponentData<DisabledByDisaster>(disasterEntity);
                    if (disaster.Building.Index <= 0)
                        continue;
                    if (disaster.CreatedHour > 0.0 && disaster.RepairedThroughHour >= disaster.CreatedHour)
                        continue;
                    activeDisasters.TryAdd(BuildingIdentityKey.Pack(disaster.Building.Index, disaster.Building.Version),
                        disaster.IsMajor ? 1f : 0.5f);
                }
            }

            using var plants = ctx.ResolvedPlantQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < plants.Length; i++)
            {
                Entity entity = plants[i];
                if (!ctx.DisasterDamageModifierLookup.TryGetComponent(entity, out var modifier))
                    continue;
                float canonical = activeDisasters.TryGetValue(BuildingIdentityKey.Pack(entity), out var value) ? value : 0f;
                if (math.abs(modifier.DisasterDamagePercent - canonical) <= 0.001f)
                    continue;
                modifier.DisasterDamagePercent = canonical;
                ctx.DisasterDamageModifierLookup[entity] = modifier;
            }
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver wear/repair hydration: throttled sidecar-to-modifier reconciliation over indexed power producers")]
        public void ApplyWearAndRepair(ref PowerCapacityPipelineContext ctx)
        {
            var activeOperationalDamage = new NativeHashMap<long, float>(16, Allocator.Temp);
            try
            {
                using (var damageEntities = ctx.PowerPlantDamageQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < damageEntities.Length; i++)
                    {
                        Entity damageEntity = damageEntities[i];
                        if (!ctx.PowerPlantDamageLookup.TryGetComponent(damageEntity, out var damage))
                            continue;
                        if (damage.Building.Index <= 0 || damage.DamagePercent <= 0f)
                            continue;

                        long damageKey = BuildingIdentityKey.Pack(damage.Building.Index, damage.Building.Version);
                        if (activeOperationalDamage.TryGetValue(damageKey, out var existing))
                            activeOperationalDamage[damageKey] = math.max(existing, damage.DamagePercent);
                        else
                            activeOperationalDamage.TryAdd(damageKey, damage.DamagePercent);
                    }
                }

                using (var wearEntities = ctx.EquipmentWearQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < wearEntities.Length; i++)
                    {
                        Entity wearEntity = wearEntities[i];
                        if (!ctx.EquipmentWearLookup.TryGetComponent(wearEntity, out var wear))
                            continue;

                        Entity buildingEntity = wear.GetBuildingEntity();
                        if (!ctx.EntityStorageInfoLookup.Exists(buildingEntity))
                            continue;
                        if (!ctx.WearModifierLookup.TryGetComponent(buildingEntity, out var modifier))
                            continue;

                        bool changed = false;
                        if (modifier.IsUnderRepair != wear.IsUnderRepair)
                        {
                            modifier.IsUnderRepair = wear.IsUnderRepair;
                            changed = true;
                        }

                        float canonicalExplosion = 0f;
                        if (wear.HasExploded)
                        {
                            canonicalExplosion = wear.SavedExplosionDamage > 0f
                                ? wear.SavedExplosionDamage
                                : Core.Config.BalanceConfig.Current.EquipmentWear.ExplosionDamage;
                        }
                        if (math.abs(modifier.ExplosionDamagePercent - canonicalExplosion) > 0.001f)
                        {
#pragma warning disable CIVIC259 // Hydrates downstream capacity modifier from already-recorded EquipmentWear explosion state.
                            modifier.ExplosionDamagePercent = canonicalExplosion;
#pragma warning restore CIVIC259
                            changed = true;
                        }

                        if (changed)
                            ctx.WearModifierLookup[buildingEntity] = modifier;
                    }
                }

                using var plants = ctx.ResolvedPlantQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < plants.Length; i++)
                {
                    Entity entity = plants[i];
                    if (!ctx.OperationalDamageModifierLookup.TryGetComponent(entity, out var modifier))
                        continue;
                    float canonical = activeOperationalDamage.TryGetValue(BuildingIdentityKey.Pack(entity), out var value)
                        ? value
                        : 0f;
                    if (math.abs(modifier.OperationalDamagePercent - canonical) <= 0.001f)
                        continue;
#pragma warning disable CIVIC259 // Hydrates downstream capacity modifier from OperationalDamageSystem sidecar.
                    modifier.OperationalDamagePercent = canonical;
#pragma warning restore CIVIC259
                    ctx.OperationalDamageModifierLookup[entity] = modifier;
                }
            }
            finally
            {
                activeOperationalDamage.Dispose();
            }
        }

        // Surplus-saturation hydration. Computes a fleet-wide target factor from built nameplate
        // over GridProducer-channel plants vs (intermediate) instant Demand, then advances each
        // plant's persisted SaturationModifier toward that target with asymmetric inertia (down
        // instant, up slow). Result folds into the Efficiency factor via PowerCapacityMath.ComputeEffectiveFactor.
        // Reads only PlantBaseCapacity + PowerGridSingleton.Demand + PrefabRef — independent of the
        // construction/disaster/wear passes, so order among the Apply* passes is not significant.
        // afterLoad=true is a timestamp-only reconcile (factor preserved).
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        [CompletesDependency("PowerCapacityResolver surplus-saturation hydration: throttled fleet-aggregate target + per-plant asymmetric inertia over indexed grid producers")]
        public void ApplySaturationInertia(ref PowerCapacityPipelineContext ctx, bool afterLoad)
        {
            var cfg = Core.Config.BalanceConfig.Current.GenerationSaturation;
            using var entities = ctx.ResolvedPlantQuery.ToEntityArray(Allocator.Temp);

            // Feed the demand-peak ring on every LIVE tick regardless of the saturation toggle:
            // the 24h peak is a city-level fact owned by this resolver, and WaveScheduler reads
            // the ring under its own independent toggle (Waves.SurplusStrikesEnabled). Gating the
            // feed on GenerationSaturation.Enabled froze the ring whenever saturation was off, so
            // surplus strikes were computed against a stale (usually low) peak. NOT fed on the
            // afterLoad pass: PowerGridSingleton.Demand reads 0 there (hydration order) and a
            // 0-sample would also clobber LastSampleGameHours before
            // ReconcileDemandPeakAfterLoadIfPending judges the saved ring's staleness.
            int peakDemandKW = 0;
            int instantDemandKW = 0;
            double sampleHours = 0.0;
            if (!afterLoad)
            {
                // INVARIANT: ratio base is Demand (wanted-consumption), NOT Consumption
                // (active-load). Consumption falls under load shedding, so basing degradation on
                // it would deepen the cut during a crisis → death-spiral. Demand reflects what
                // the city wants regardless of shed.
#pragma warning disable CIVIC070 // Demand changes gradually and saturation is a slow inertial mechanism (Tau≈12h, 500ms throttle); a 1-frame lag is invisible — same rationale as GridStressSystem.
                instantDemandKW = m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid)
                    ? grid.Demand
                    : 0;
#pragma warning restore CIVIC070
                sampleHours = GameTimeSystem.TryGetGameHours(out var sh) ? sh : 0.0;
                peakDemandKW = SampleAndReadDemandPeak(instantDemandKW, sampleHours);
            }

            // Feature off → idempotently clear factor to 1 so toggling off restores full capacity.
            if (!cfg.Enabled)
            {
                m_FleetTargetFactor = 1f;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    if (!ctx.SaturationModifierLookup.TryGetComponent(e, out var m))
                        continue;
                    if (math.abs(m.SaturationFactor - 1f) <= 0.001f)
                        continue;
                    m.SaturationFactor = 1f;       // LastUpdateGameHours left as-is — re-enable reconciles it
                    ctx.SaturationModifierLookup[e] = m;
                }
                return;
            }

            // --- timestamp-only reconcile on the post-load pass (factor stays = persisted value) ---
            // INVARIANT: persisted LastUpdateGameHours MUST be reconciled to now here, NOT left as the
            // saved value. The factor (inertia state) is persisted and kept; the timestamp must not be,
            // or the first steady tick sees a huge Δh = now − oldSave → up-ramp (1−exp(−Δh/Tau)) ≈ 1
            // → the saved degradation is wiped on the first post-load tick and the spam city regains
            // immunity. Mirrors the import-cap latch reset in ValidateAfterLoad.
            // After load GameTimeSystem may report a different game time than the saved
            // LastUpdateGameHours; writing now defers the up-ramp so the first steady tick sees Δh≈0
            // and does not skip the factor toward target. SaturationFactor (the inertia state) is
            // preserved — a reloaded spam city keeps its degraded factor.
            if (afterLoad)
            {
                double loadHours = GameTimeSystem.TryGetGameHours(out var lh) ? lh : 0.0;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity e = entities[i];
                    if (!m_IndexStateLookup.TryGetComponent(e, out var idx) || idx.Channel != CapacityChannel.GridProducer)
                        continue;
                    if (!ctx.SaturationModifierLookup.TryGetComponent(e, out var m))
                        continue;
                    m.LastUpdateGameHours = loadHours;     // factor unchanged
                    ctx.SaturationModifierLookup[e] = m;
                }

                // Fleet TARGET recomputed from THIS save's persisted data. m_FleetTargetFactor is
                // [NonSerialized] on a reused system instance, so leaving it as-is carried the
                // PREVIOUS city's target into the first published snapshot — UI "fleet КПД" and
                // per-plant RecoveryHours were wrong for the whole post-load pause (mirrors the
                // m_HasPublishedSnapshot / import-cap latch resets in ValidateAfterLoad). The
                // Demand singleton reads 0 on this path, but the persisted ring's max is this
                // city's own 24h peak — valid without feeding a sample. Peak 0 (fresh city /
                // empty ring) → neutral 1f, NOT a target against zero demand (which would
                // publish floor-level "degradation" on a brand-new map).
                int savedPeakKW = PeekDemandPeakKW();
                m_FleetTargetFactor = savedPeakKW > 0
                    ? ComputeFleetTargetFactor(entities, savedPeakKW / KwPerMw, cfg, out _)
                    : 1f;
                return;
            }

            // --- fleet aggregates ONCE per tick ---
            // Фаза 3: the ratio base is the 24h PEAK demand (fed above), not the instant value —
            // so degradation is steady across the day/night demand swing instead of cutting plants
            // at night and blacking out at the morning spike while the inertial release slowly
            // recovers. The instant Demand only FEEDS the rolling ring; the ring's max is what the
            // formula sees.
            float demandMW = peakDemandKW / KwPerMw;
            float targetFactor = ComputeFleetTargetFactor(entities, demandMW, cfg, out int intermittentTypes);
            m_FleetTargetFactor = targetFactor;

            // Same game-hour reading used to feed the demand-peak ring above (one TryGet per tick).
            double nowHours = sampleHours;

            // --- per-plant inertia ---
            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                if (!m_IndexStateLookup.TryGetComponent(e, out var idx) || idx.Channel != CapacityChannel.GridProducer)
                    continue;                              // OutsideConnection/EmergencyBattery → factor=1 (PlantResolveJob branch)
                // Under-construction plants get NO saturation degradation: their effective output is
                // already shaped by the construction ramp, and stacking saturation on top would "finish
                // off" a station that is not even in the grid yet. Their SaturationFactor stays at the
                // add-site default (1.0); once construction completes they enter the nameplate aggregate
                // above and degrade normally. Same signal as the fleet loop (ApplyConstructionDelay-hydrated).
                if (m_ConstructionModifierLookup.TryGetComponent(e, out var cMod) && cMod.IsUnderConstruction)
                {
                    // Keep the every-run timestamp invariant (see below) even while skipping the
                    // factor: a bare skip lets Δh accumulate over the whole build window, so the
                    // first tick after completion sees alpha = 1−exp(−days/Tau) ≈ 1 and the
                    // up-ramp jumps to target instantly instead of easing over TauUpHours
                    // (a cheap upgrade would fast-forward the recovery ramp).
                    if (ctx.SaturationModifierLookup.TryGetComponent(e, out var ucMod))
                    {
                        ucMod.LastUpdateGameHours = nowHours;   // factor unchanged
                        ctx.SaturationModifierLookup[e] = ucMod;
                    }
                    continue;
                }
                if (!ctx.SaturationModifierLookup.TryGetComponent(e, out var mod))
                    continue;
                float deltaHours = (float)math.max(0.0, nowHours - mod.LastUpdateGameHours);
                float newFactor = SaturationLogic.StepInertia(
                    mod.SaturationFactor, targetFactor, deltaHours, cfg.Hysteresis, cfg.TauUpHours);
                // Timestamp written EVERY run while Enabled: otherwise Δh accumulates in the dead
                // zone and the up-ramp jumps on exit (StepInertia uses 1-exp(-Δh/tau)). One
                // non-structural writeback per tick covers both fields (cheap, throttled 500 ms).
                mod.SaturationFactor = newFactor;
                mod.LastUpdateGameHours = nowHours;
                ctx.SaturationModifierLookup[e] = mod;
            }

            if (Log.IsDebugEnabled)
                Log.Debug($"[Saturation] nameplate={m_FleetNameplateKW / KwPerMw:F1}MW residual={m_FleetResidualKW / KwPerMw:F1}MW (fleetKW={m_FleetNameplateKW}) peakKW={peakDemandKW} instDemandKW={instantDemandKW} demand={demandMW:F1}MW intermittentTypes={intermittentTypes} target={targetFactor:F3}");
        }

        /// <summary>
        /// Fleet surplus-saturation TARGET from the built-and-in-grid RESIDUAL nameplate
        /// (nameplate × damage multiplier, per plant) vs the 24h peak demand. Shared by the live
        /// throttle tick and the post-load reseed in
        /// <see cref="ApplySaturationInertia"/> (the latter recomputes the [NonSerialized]
        /// <c>m_FleetTargetFactor</c> from THIS save's persisted ring so the first published
        /// snapshot never carries the previous city's target).
        ///
        /// RESIDUAL vs FULL — deliberate split between the two nameplate aggregates:
        /// the published <c>PowerCapacitySnapshot.NameplateKW</c> (job aggregate, feeds Фаза-1
        /// surplus ratio and Фаза-7 surplus-attracts-strikes) stays на FULL nameplate — enemy
        /// recon counts built plants, and degradation must not hide over-build from the strike
        /// axis. THIS aggregate however feeds only the saturation target, and counting damaged
        /// plants at full nameplate made the target demand-blind after a strike: a fleet at 50%
        /// damage still read as "over-built", saturation kept cutting the survivors, and the
        /// city sat in a STATIC deficit until every plant was repaired (player report
        /// 2026-07-11: 6 MW served vs 11 MW demand at 48.7 Hz). Residual nameplate keeps the
        /// anti-spam semantics (healthy over-build still degrades) while a damaged fleet's
        /// target eases toward what the fleet can physically serve. The post-strike blackout
        /// window is preserved by the UP-slow inertia (TauUpHours), not by this aggregate.
        /// Fuel is deliberately NOT part of the residual: availability is an instantaneous
        /// flow signal (jitter would pump the target), and fuel starvation is self-inflicted.
        ///
        /// Side effect: refreshes
        /// <c>m_FleetNameplateKW</c> (the Фаза-1 ratio nameplate — kept FULL; the authoritative snapshot
        /// NameplateKW for Фаза 7 is authored by the resolve pass — same channel filter ⇒
        /// identical value when both run).
        /// </summary>
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        private float ComputeFleetTargetFactor(
            NativeArray<Entity> entities,
            float demandMW,
            GenerationSaturationConfig cfg,
            out int intermittentTypes)
        {
            // Self-contained CIVIC081 updater (RefreshLookups already ran this pass; redundant
            // Update is cheap — no structural change ⇒ instant fence drain).
            m_IndexStateLookup.Update(this);
            m_ConstructionModifierLookup.Update(this);
            m_BaseCapacityLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_GridStressModifierLookup.Update(this);
            m_WearModifierLookup.Update(this);
            m_OperationalDamageModifierLookup.Update(this);
            m_DisasterDamageModifierLookup.Update(this);

            int nameplateSumKW = 0;
            long residualSumKW = 0;
            int largestResidualKW = 0;
            // Diversity headroom counts ONLY intermittent types (Wind/Solar — see
            // PowerPlantUtils.IsIntermittent): they genuinely need backup reserve. Bit mask over
            // PlantType (<32 values) gives a no-alloc distinct count via math.countbits.
            int typeMask = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                Entity e = entities[i];
                if (!m_IndexStateLookup.TryGetComponent(e, out var idx))
                    continue;
                if (idx.Channel != CapacityChannel.GridProducer)
                    continue;                              // import / battery excluded
                // A plant still under construction-delay is not yet on the grid (its ramp serves ~0):
                // its nameplate must not inflate the surplus ratio and its type must not grant
                // diversity headroom. Reuse the SAME construction signal the resolver folds into the
                // capacity factor — ConstructionModifier.IsUnderConstruction, hydrated by
                // ApplyConstructionDelay earlier this tick (covers ConstructionDelayEnabled +
                // ConstructionClassifiedState + live UnderConstruction sidecar). It joins the
                // aggregate once construction completes.
                if (m_ConstructionModifierLookup.TryGetComponent(e, out var cMod) && cMod.IsUnderConstruction)
                    continue;
                // Ruins are not over-build — same knockout exclusion as the resolve job's
                // aggregate (see PowerCapacityMath.IsKnockedOut), built from the raw modifier
                // lookups because this path runs without the full ReadCapacityState.
                var damageSlice = BuildDamageSlice(e);
                if (PowerCapacityMath.IsKnockedOut(damageSlice))
                    continue;
                if (!m_BaseCapacityLookup.TryGetComponent(e, out var baseCap))
                    continue;
                nameplateSumKW += baseCap.OriginalCapacity;
                // Residual = nameplate × (1 − operational − disaster − explosion): what the plant
                // can physically serve before saturation/fuel. This is the saturation ratio base
                // (see the method docstring for the residual-vs-full split rationale).
                int residualKW = (int)math.round(baseCap.OriginalCapacity * PowerCapacityMath.GetDamageMultiplier(damageSlice));
                residualSumKW += residualKW;
                largestResidualKW = math.max(largestResidualKW, residualKW);
                if (m_PrefabRefLookup.TryGetComponent(e, out var prefabRef))
                {
                    var pt = PowerPlantUtils.GetPlantType(m_PrefabSystem, prefabRef);
                    if (PowerPlantUtils.IsIntermittent(pt))
                        typeMask |= 1 << (int)pt;
                }
            }
            // Kept FULL: the Фаза-1 ratio nameplate mirrors the published snapshot semantics.
            // Only the ComputeTargetFactor input below is residual.
            m_FleetNameplateKW = nameplateSumKW;
            m_FleetResidualKW = residualSumKW;
            float totalResidualMW = residualSumKW / KwPerMw;
            intermittentTypes = math.countbits(typeMask);  // 0..2 (Wind/Solar present)

            // N+1 unit buffer: a reserve of one biggest built unit is forgiven (plants come in
            // build quanta — a town whose only available plant is a 50 MW thermal must not read
            // as "over-built" the day it is finished). The cap closes the single-giant-plant
            // loophole (a 500 MW nuclear at 100 MW demand is a deliberate over-build, not a
            // quantum). RESIDUAL like the sum: the buffer stands for real dispatchable reserve —
            // a half-destroyed giant only shields the half it can still serve. (The snapshot's
            // LargestPlantKW for the Фаза-7 strike axis stays full, published by the job.)
            float unitBufferMW = math.min(largestResidualKW / KwPerMw, cfg.UnitBufferCapMW);

            return SaturationLogic.ComputeTargetFactor(
                totalResidualMW, demandMW, intermittentTypes,
                cfg.HeadroomBase, cfg.HeadroomPerType, cfg.SaturationSoftness,
                cfg.SaturationFloor, unitBufferMW);
        }

        /// <summary>
        /// Minimal damage/knockout slice of <see cref="CapacityModifierState"/> for the fleet
        /// aggregate loop, built straight from the modifier lookups (no fuel read
        /// — a nameplate decision needs no IVanillaWriteBarrier ticket, and fuel is deliberately
        /// excluded from the saturation residual — see <see cref="ComputeFleetTargetFactor"/>).
        /// One slice serves both the <see cref="PowerCapacityMath.IsKnockedOut"/> ruin check and
        /// the <see cref="PowerCapacityMath.GetDamageMultiplier"/> residual, so the two can
        /// never drift on what counts as damage.
        /// </summary>
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        private CapacityModifierState BuildDamageSlice(Entity e)
        {
            bool hasStress = m_GridStressModifierLookup.TryGetComponent(e, out var stressMod);
            bool hasWear = m_WearModifierLookup.TryGetComponent(e, out var wearMod);
            bool hasOp = m_OperationalDamageModifierLookup.TryGetComponent(e, out var opMod);
            bool hasDisaster = m_DisasterDamageModifierLookup.TryGetComponent(e, out var disasterMod);
            return new CapacityModifierState(
                hasStress && stressMod.IsCollapsed,
                isUnderConstruction: false,
                constructionProgress: 0f,
                isUnderRepair: hasWear && wearMod.IsUnderRepair,
                explosionDamagePercent: hasWear ? wearMod.ExplosionDamagePercent : 0f,
                operationalDamagePercent: hasOp ? opMod.OperationalDamagePercent : 0f,
                disasterDamagePercent: hasDisaster ? disasterMod.DisasterDamagePercent : 0f);
        }

        /// <summary>
        /// Read-only max over the persisted 24h demand-peak ring (kW), 0 when the ring is missing
        /// or empty. Unlike <see cref="SampleAndReadDemandPeak"/> this neither feeds a sample nor
        /// touches <c>LastSampleGameHours</c> — safe on the post-load pass, where the pending
        /// <see cref="ReconcileDemandPeakAfterLoadIfPending"/> still has to judge the saved ring's
        /// staleness from that timestamp.
        /// </summary>
        private int PeekDemandPeakKW()
        {
            // Self-contained CIVIC081 updater (RefreshLookups already ran this pass; redundant
            // Update is cheap — no structural change ⇒ instant fence drain).
            m_DemandPeakLookup.Update(this);
            m_DemandPeakBucketLookup.Update(this);

            if (!m_DemandPeakQuery.TryGetSingletonEntity<DemandPeakSingleton>(out var entity)
                || !m_DemandPeakLookup.HasComponent(entity)
                || !m_DemandPeakBucketLookup.TryGetBuffer(entity, out var ring)
                || ring.Length != DemandPeakSingleton.BUCKETS)
                return 0;

            int peak = 0;
            for (int b = 0; b < DemandPeakSingleton.BUCKETS; b++)
                peak = math.max(peak, ring[b].PeakKW);
            return peak;
        }

        /// <summary>
        /// Advances the 24-hour rolling demand-peak ring with the current instant demand and returns
        /// the peak (max over the 24 buckets, kW) used as the saturation ratio base (Фаза 3).
        ///
        /// Ring discipline: on a game-hour rollover the buckets the cursor steps INTO are zeroed (the
        /// one it lands on is exactly 24h old → "forget that hour"). Multiple hours can pass in one
        /// 500ms throttle tick at high sim speed, so the range (prevBucket+1 .. curBucket] is cleared,
        /// not just the landing bucket; ≥24h advanced ⇒ the whole ring is stale and zeroed. On pause
        /// no game-hour passes ⇒ the cursor is stable and max-with-same-demand is idempotent.
        /// </summary>
        private int SampleAndReadDemandPeak(int currentDemandKW, double nowHours)
        {
            // Already refreshed in RefreshLookups this tick; the redundant Update makes this method a
            // self-contained CIVIC081 updater (cheap — no structural change ⇒ instant fence drain).
            m_DemandPeakLookup.Update(this);
            m_DemandPeakBucketLookup.Update(this);

            if (!m_DemandPeakQuery.TryGetSingletonEntity<DemandPeakSingleton>(out var entity)
                || !m_DemandPeakLookup.HasComponent(entity)
                || !m_DemandPeakBucketLookup.TryGetBuffer(entity, out var ring)
                || ring.Length != DemandPeakSingleton.BUCKETS)
            {
                // Ring not yet materialised (should not happen — ensured in OnCreate/OnStartRunning).
                // Fall back to the instant demand so the formula still has a base.
                return math.max(0, currentDemandKW);
            }

            var state = m_DemandPeakLookup[entity];
            int sampleKW = math.max(0, currentDemandKW);

            int nowHourFloor = (int)math.floor(nowHours);
            int lastHourFloor = (int)math.floor(state.LastSampleGameHours);
            int hoursAdvanced = nowHourFloor - lastHourFloor;
            int curBucket = ((nowHourFloor % DemandPeakSingleton.BUCKETS) + DemandPeakSingleton.BUCKETS) % DemandPeakSingleton.BUCKETS;

            if (hoursAdvanced >= DemandPeakSingleton.BUCKETS)
            {
                // ≥ 24h passed in one tick — every stored hour is older than the window.
                for (int b = 0; b < DemandPeakSingleton.BUCKETS; b++)
                    ring[b] = new DemandPeakBucket { PeakKW = 0 };
            }
            else if (hoursAdvanced >= 1)
            {
                // Zero only the buckets the cursor steps into (prevBucket+1 .. curBucket].
                int prevBucket = ((state.CursorHour % DemandPeakSingleton.BUCKETS) + DemandPeakSingleton.BUCKETS) % DemandPeakSingleton.BUCKETS;
                for (int h = 1; h <= hoursAdvanced; h++)
                {
                    int b = (prevBucket + h) % DemandPeakSingleton.BUCKETS;
                    ring[b] = new DemandPeakBucket { PeakKW = 0 };
                }
            }

            // Update the current bucket's hourly maximum.
            ring[curBucket] = new DemandPeakBucket { PeakKW = math.max(ring[curBucket].PeakKW, sampleKW) };

            state.CursorHour = curBucket;
            state.LastSampleGameHours = nowHours;
            m_DemandPeakLookup[entity] = state;

            int peak = 0;
            for (int b = 0; b < DemandPeakSingleton.BUCKETS; b++)
                peak = math.max(peak, ring[b].PeakKW);
            return peak;
        }

        /// <summary>
        /// First-tick post-load reconcile of the demand-peak ring. If the gap between the saved last
        /// sample and the current game-hour exceeds the configured window, the persisted ring is from
        /// a long-ago day → drop it and reseed from the CURRENT (now-fresh) demand; otherwise keep the
        /// saved ring. Runs lazily on the first OnThrottledUpdate because Demand reads 0 in
        /// ValidateAfterLoad.
        /// </summary>
        private void ReconcileDemandPeakAfterLoadIfPending()
        {
            if (!m_DemandPeakReconcilePending)
                return;
            m_DemandPeakReconcilePending = false;

            // Self-contained CIVIC081 updater (RefreshLookups already ran this tick; redundant Update
            // is cheap — no structural change ⇒ instant fence drain).
            m_DemandPeakLookup.Update(this);
            m_DemandPeakBucketLookup.Update(this);

            if (!m_DemandPeakQuery.TryGetSingletonEntity<DemandPeakSingleton>(out var entity)
                || !m_DemandPeakLookup.HasComponent(entity)
                || !m_DemandPeakBucketLookup.TryGetBuffer(entity, out var ring)
                || ring.Length != DemandPeakSingleton.BUCKETS)
                return;

            var state = m_DemandPeakLookup[entity];
            double nowHours = GameTimeSystem.TryGetGameHours(out var h) ? h : state.LastSampleGameHours;
            double gap = nowHours - state.LastSampleGameHours;
            float windowHours = Core.Config.BalanceConfig.Current.GenerationSaturation.PeakWindowHours;

            if (gap <= windowHours)
                return; // saved ring still valid — leave it

#pragma warning disable CIVIC070 // One-shot post-load reseed; Demand is consumed only to seed a single bucket, and ordering is structural (this runs on the first OnThrottledUpdate, after PowerGridDataSystem hydrated Demand) — same rationale as the steady-state read above.
            int currentDemandKW = m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid)
                ? math.max(0, grid.Demand)
                : 0;
#pragma warning restore CIVIC070
            int curBucket = (((int)math.floor(nowHours) % DemandPeakSingleton.BUCKETS) + DemandPeakSingleton.BUCKETS) % DemandPeakSingleton.BUCKETS;

            for (int b = 0; b < DemandPeakSingleton.BUCKETS; b++)
                ring[b] = new DemandPeakBucket { PeakKW = 0 };
            ring[curBucket] = new DemandPeakBucket { PeakKW = currentDemandKW };

            state.CursorHour = curBucket;
            state.LastSampleGameHours = nowHours;
            m_DemandPeakLookup[entity] = state;

            if (Log.IsDebugEnabled)
                Log.Debug($"[Saturation] peak buffer stale (gap={gap:F1}h) — reseeded from current demand={currentDemandKW}kW");
        }

        // ExportCap: caps the export trade edges of every interconnector. Checked on
        // every safe-frame tick (the comparison is cheap, the ECB write happens only on
        // a difference): the setting can change at any moment, and vanilla recreates the
        // edges at max capacity on an outside-connection rebuild and on the legacy load
        // path in ElectricityFlowSystem. needsFlowEdgeRetry/m_FlowEdgeDirty are NOT
        // touched: a persistent Unresolved here would otherwise spin the shared
        // reconcile of all plant edges — this pass keeps its own diagnostics latch.
        [CompletesDependency("PowerCapacityResolver export-cap pass: throttled safe-frame materialisation of the trade-marker query (one entity per interconnector, a handful at most) to cap the export trade edges; runs from OnThrottledUpdate/ValidateAfterLoad after RefreshLookups, before the resolve schedule.")]
        private void EnforceExportCaps(ref PowerCapacityPipelineContext ctx)
        {
            if (!ctx.IsSafeFrame)
                return;
            int exportCapKW = ImportCapRuntimeState.HasPublishedExportCap
                ? ImportCapRuntimeState.CurrentExportCapKW
                : Engine.PowerGrid.DEFAULT_LEGAL_EXPORT_MW * Engine.PowerGrid.KW_PER_MW;

            // Self-contained CIVIC081 updater (RefreshLookups already ran this pass; redundant
            // Update is cheap — no structural change ⇒ instant fence drain).
            m_OwnerLookup.Update(this);

            bool anyUnresolved = false;
            int edgesUpdated = 0;
            using var markers = m_TradeMarkerQuery.ToEntityArray(Allocator.Temp);
            // Marker count feeds the export ceiling (cap × N) used to clamp the noisy
            // flow-difference export proxy in the UI and the shadow-ceiling math.
            ImportCapRuntimeState.SetExportInterconnectorCount(markers.Length);
            for (int i = 0; i < markers.Length; i++)
            {
                if (!m_OwnerLookup.TryGetComponent(markers[i], out var owner))
                    continue;
                ctx.EnsureEcb();
                var result = PowerCapacityMath.TryUpdateExportEdgeViaEcb(ref ctx, owner.m_Owner, exportCapKW);
                if (result == FlowEdgeUpdateResult.Updated)
                    edgesUpdated++;
                else if (result == FlowEdgeUpdateResult.Unresolved)
                    anyUnresolved = true;
                else
                {
                    // AlreadyCurrent / None — the edge is at the requested capacity (or the
                    // marker produced no work); nothing to count and nothing to diagnose.
                }
            }

            if (edgesUpdated > 0 && Log.IsDebugEnabled)
                Log.Debug($"[ExportCap] {edgesUpdated} export trade edge(s) capped to {exportCapKW} kW");

            // A persistent Unresolved means the lookup route is broken (vanilla changed
            // the topology) — the cap silently stops applying, which must reach the log
            // exactly once.
            if (anyUnresolved && markers.Length > 0)
            {
                if (++m_ExportEdgeUnresolvedStreak >= 10 && !m_ExportEdgeWarned)
                {
                    m_ExportEdgeWarned = true;
                    Log.Warn($"[ExportCap] export trade edge unresolved for {m_ExportEdgeUnresolvedStreak} consecutive ticks — cap NOT enforced, route lookup broken?");
                }
            }
            else
            {
                m_ExportEdgeUnresolvedStreak = 0;
            }
        }

        private static float ReadEfficiencyFactor(DynamicBuffer<Efficiency> buffer, EfficiencyFactor factor)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_Factor == factor)
                    return buffer[i].m_Efficiency;
            }

            // Absent slot is neutral (== 1.0). SetEfficiencyFactor stores nothing for ≈1.0.
            return 1f;
        }

        /// <summary>
        /// Product of the FOREIGN BOOSTS in the Efficiency buffer: Π max(1, slot_i) — a slot
        /// contributes only when it is &gt; 1 (boost: ServiceBudget above 100% budget,
        /// EmployeeHappiness overstaffing, and the like). Foreign PENALTIES (&lt; 1, e.g.
        /// NotEnoughEmployees) do NOT enter the compensation — they must stack UNDER the
        /// allowed output, not be cancelled by the division. Excluded: our slot 26
        /// (ModDamageEfficiencyFactor) and slots 17–20 — PowerPlantAISystem resets those to 1
        /// BEFORE GetEfficiency (decompile PowerPlantAISystem.cs:144-147); their buffer values
        /// are last tick's ApproximateEfficiencyFactors output (:256-261), so they must not be
        /// read back as input.
        /// </summary>
        private static float ComputeForeignEfficiencyBoost(DynamicBuffer<Efficiency> buffer)
        {
            float product = 1f;
            for (int i = 0; i < buffer.Length; i++)
            {
                EfficiencyFactor slot = buffer[i].m_Factor;
                if (slot == ModDamageEfficiencyFactor)
                    continue;
                if (slot >= EfficiencyFactor.WindSpeed && slot <= EfficiencyFactor.NaturalResources)
                    continue;
                product *= math.max(1f, buffer[i].m_Efficiency);
            }
            return product;
        }

        /// <summary>
        /// Snapshot of the MOD-OWNED modifier slice (sidecar components hydrated by the Apply*
        /// phases) — main-thread by design, so <see cref="PlantResolveJob"/> never races the
        /// mod's own main-thread modifier writers. The fuel pair stays at its neutral defaults
        /// (1,1): the vanilla <c>ResourceConsumer</c> read lives inside the job (see
        /// <c>CapacityModifierState.WithFuel</c>) — the only field of this state that needs
        /// job-graph ordering against a vanilla writer.
        /// </summary>
        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        internal CapacityModifierState ReadCapacityState(Entity entity)
        {
            bool hasGridModifier = m_GridStressModifierLookup.TryGetComponent(entity, out var stressMod);
            bool hasConstructionModifier = m_ConstructionModifierLookup.TryGetComponent(entity, out var constructionMod);
            bool hasWearModifier = m_WearModifierLookup.TryGetComponent(entity, out var wearMod);
            bool hasOperationalDamageModifier = m_OperationalDamageModifierLookup.TryGetComponent(entity, out var opMod);
            bool hasDisasterDamageModifier = m_DisasterDamageModifierLookup.TryGetComponent(entity, out var disasterMod);
            bool hasSaturationModifier = m_SaturationModifierLookup.TryGetComponent(entity, out var satMod);
            bool hasImportCapModifier = m_ImportCapModifierLookup.TryGetComponent(entity, out var importMod);

            return new CapacityModifierState(
                hasGridModifier && stressMod.IsCollapsed,
                hasConstructionModifier && constructionMod.IsUnderConstruction,
                hasConstructionModifier ? constructionMod.Progress : 0f,
                hasWearModifier && wearMod.IsUnderRepair,
                hasWearModifier ? wearMod.ExplosionDamagePercent : 0f,
                hasOperationalDamageModifier ? opMod.OperationalDamagePercent : 0f,
                hasDisasterDamageModifier ? disasterMod.DisasterDamagePercent : 0f,
                hasImportCapModifier,
                hasImportCapModifier ? importMod.ImportCapLimitKW : 0,
                hasConstructionModifier,
                hasWearModifier,
                hasOperationalDamageModifier,
                hasDisasterDamageModifier,
                hasConstructionModifier ? constructionMod.BaseCapacityKW : 0,
                hasConstructionModifier ? constructionMod.TargetNameplateKW : 0,
                hasSaturationModifier ? satMod.SaturationFactor : 1f);
        }

        private void Publish(int dispatchableMW, PowerCapacityPlantSnapshot[] plants, int nameplateKW, float fleetTargetFactor, int largestPlantKW = 0, int intermittentTypeCount = 0, int cityDispatchableMW = 0)
        {
            var next = new PowerCapacitySnapshot(dispatchableMW, plants, nameplateKW, fleetTargetFactor, largestPlantKW, intermittentTypeCount, cityDispatchableMW);
            m_LatestSnapshot = next;
            m_HasPublishedSnapshot = true;
            m_View.Publish(next);
        }

        private void RefreshLookups()
        {
            m_ProducerLookup.Update(this);
            m_BaseCapacityLookup.Update(this);
            m_PlantKindLookup.Update(this);
            m_IndexStateLookup.Update(this);
            m_GridStressModifierLookup.Update(this);
            m_ConstructionModifierLookup.Update(this);
            m_WearModifierLookup.Update(this);
            m_OperationalDamageModifierLookup.Update(this);
            m_DisasterDamageModifierLookup.Update(this);
            m_SaturationModifierLookup.Update(this);
            m_ImportCapModifierLookup.Update(this);
            m_OutsideConnectionLookup.Update(this);
            m_ConnectionLookup.Update(this);
            m_FlowEdgeLookup.Update(this);
            m_NodeConnectionLookup.Update(this);
            m_OwnerLookup.Update(this);
            m_FlowConnectionLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_SubObjectLookup.Update(this);
            m_InstalledUpgradeLookup.Update(this);
            m_EfficiencyLookup.Update(this);
            m_DemandPeakBucketLookup.Update(this);
            m_DemandPeakLookup.Update(this);
            m_EntityStorageInfoLookup.Update(this);
            m_PowerPlantDataLookup.Update(this);
            m_EmergencyGeneratorDataLookup.Update(this);
            m_BatteryDataLookup.Update(this);
            m_WindPoweredDataLookup.Update(this);
            m_SolarPoweredDataLookup.Update(this);
            m_GarbagePoweredDataLookup.Update(this);
            m_WaterPoweredDataLookup.Update(this);
            m_WaterPoweredLookup.Update(this);
            m_GroundWaterPoweredDataLookup.Update(this);
            m_EquipmentWearLookup.Update(this);
            m_PowerPlantDamageLookup.Update(this);
            m_ResourceConsumerLookup.Update(this);

        }

        private PowerCapacityPipelineContext CreatePipelineContext(bool isSafeFrame)
        {
            return new PowerCapacityPipelineContext
            {
                EntityManager = EntityManager,
                EcbFactory = m_GameSimulationEndBarrier.CreateCommandBuffer,
                HasEcb = false,
                IsSafeFrame = isSafeFrame,
                ConstructionDelayEnabled = m_Settings.ConstructionDelayEnabled,
                PrefabSystem = m_PrefabSystem,
                ResolvedPlantQuery = m_ResolvedPlantQuery,
                GridStressQuery = m_GridStressQuery,
                UnderConstructionQuery = m_UnderConstructionQuery,
                DisabledByDisasterQuery = m_DisabledByDisasterQuery,
                EquipmentWearQuery = m_EquipmentWearQuery,
                PowerPlantDamageQuery = m_PowerPlantDamageQuery,
                CollapsedProducerQuery = m_CollapsedProducerQuery,
                DistrictPowerQuery = m_DistrictPowerQuery,
                ExternalPowerInputQuery = m_ExternalPowerInputQuery,
                ShadowExportStateQuery = m_ShadowExportStateQuery,
                ProducerLookup = m_ProducerLookup,
                BaseCapacityLookup = m_BaseCapacityLookup,
                PlantKindLookup = m_PlantKindLookup,
                GridStressModifierLookup = m_GridStressModifierLookup,
                ConstructionModifierLookup = m_ConstructionModifierLookup,
                WearModifierLookup = m_WearModifierLookup,
                OperationalDamageModifierLookup = m_OperationalDamageModifierLookup,
                DisasterDamageModifierLookup = m_DisasterDamageModifierLookup,
                SaturationModifierLookup = m_SaturationModifierLookup,
                ImportCapModifierLookup = m_ImportCapModifierLookup,
                OutsideConnectionLookup = m_OutsideConnectionLookup,
                ConnectionLookup = m_ConnectionLookup,
                FlowEdgeLookup = m_FlowEdgeLookup,
                NodeConnectionLookup = m_NodeConnectionLookup,
                OwnerLookup = m_OwnerLookup,
                FlowConnectionLookup = m_FlowConnectionLookup,
                ElectricitySinkNode = m_ElectricityFlowSystem.sinkNode,
                PrefabRefLookup = m_PrefabRefLookup,
                SubObjectLookup = m_SubObjectLookup,
                InstalledUpgradeLookup = m_InstalledUpgradeLookup,
                EntityStorageInfoLookup = m_EntityStorageInfoLookup,
                PowerPlantDataLookup = m_PowerPlantDataLookup,
                EmergencyGeneratorDataLookup = m_EmergencyGeneratorDataLookup,
                BatteryDataLookup = m_BatteryDataLookup,
                WindPoweredDataLookup = m_WindPoweredDataLookup,
                SolarPoweredDataLookup = m_SolarPoweredDataLookup,
                GarbagePoweredDataLookup = m_GarbagePoweredDataLookup,
                WaterPoweredDataLookup = m_WaterPoweredDataLookup,
                WaterPoweredLookup = m_WaterPoweredLookup,
                GroundWaterPoweredDataLookup = m_GroundWaterPoweredDataLookup,
                EquipmentWearLookup = m_EquipmentWearLookup,
                PowerPlantDamageLookup = m_PowerPlantDamageLookup
            };
        }

        private bool IsSafeFrameForFlowEdgeWrite()
        {
            uint frame = m_SimulationSystem.frameIndex % FLOW_CYCLE_FRAMES;
            return frame != 1 && frame < FLOW_APPLY_PHASE_START;
        }

        protected override void OnDestroy()
        {
            // The pending resolve (if any) must finish before its containers are disposed.
            if (m_HasPendingResolveResults)
            {
                m_ResolveJobHandle.Complete();
                m_HasPendingResolveResults = false;
            }
            if (m_PlantWorkInput.IsCreated)
                m_PlantWorkInput.Dispose();
            if (m_PendingPlantRows.IsCreated)
                m_PendingPlantRows.Dispose();
            if (m_PendingEdgeWrites.IsCreated)
                m_PendingEdgeWrites.Dispose();
            if (m_PendingAggregates.IsCreated)
                m_PendingAggregates.Dispose();

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IPowerCapacityPipeline>(this);
                ServiceRegistry.Instance.Unregister<IPowerCapacitySnapshotReader>(this);
                ServiceRegistry.Instance.Unregister<IImportCapVersionReader>(this);
            }

#pragma warning disable CIVIC458
            m_HasPublishedSnapshot = false;
            m_LatestSnapshot = PowerCapacitySnapshot.Empty;
#pragma warning restore CIVIC458

            SetLastResolvedCount(0);
            SetLastNewPlantsCount(0);
            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }

}
