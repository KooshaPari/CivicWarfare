using System;
using Colossal.Logging;
using CivicSurvival.Core.Utils;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Systems.Base;

namespace CivicSurvival.Domains.PowerGrid.Systems
{
    /// <summary>
    /// SINGLE RESPONSIBILITY: Calculate power grid data (production/consumption).
    /// Does NOT handle UI or blackout logic.
    /// Writes to PowerGridSingleton ECS component.
    /// </summary>
    [SingletonOwner(typeof(PowerGridSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
    public partial class PowerGridDataSystem : ThrottledSystemBase, IResettable, IServedLoadVersionReader, IPostLoadValidation, ICivicSingletonOwner<PowerGridSingleton>
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PowerGridData");

        private EntityQuery m_ProducerQuery;
        private EntityQuery m_ContractQuery;
        private EntityQuery m_ExternalPowerQuery;
        private EntityQuery m_SingletonQuery;
        private EntityQuery m_StressDataQuery;
        private EntityQuery m_ShadowTradeQuery;
        // Dependencies (initialized in OnCreate)
        // Vanilla statistics — read directly for the demand-divergence diagnostic
        // (our computed demand vs vanilla consumption). Replaces the removed
        // ProducerAccessor facade, whose only live member was this consumption read.
        private ElectricityStatisticsSystem? m_ElectricityStats;
        private IDistrictStateReader? m_DistrictState;
        private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;
        private EntityQuery m_DistrictPowerQuery;
        private BufferLookup<DistrictPowerEntry> m_DistrictPowerEntryLookup;
        private BufferLookup<DistrictEntityEntry> m_DistrictEntityEntryLookup;

        // R8 FIX: Reduced from 2500ms to 1000ms — control-loop latency handled by fast path,
        // this only affects UI staleness (Production/Balance slow-path fields)
        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        private int m_DiagCounter = 0;
        private int m_DebugCounter = 0;
        [System.NonSerialized] private int m_DiagDemandCounter = 0;
        private int m_InvariantViolations = 0;
        private readonly VersionedView<ServedLoadSnapshot> m_ServedLoadView = new(ServedLoadSnapshot.Empty);
        public IVersionedView<ServedLoadSnapshot> ServedLoadView => m_ServedLoadView;

        // Frame-local contract data by building index (ContractData is on separate entities now)
        [NonEntityIndex] private NativeHashMap<long, ContractData> m_ContractsByBuilding;

        protected override void OnCreate()
        {
            base.OnCreate();

            Log.Info($"{nameof(PowerGridDataSystem)} created");
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IServedLoadVersionReader>(this);

            // Get dependencies
            m_ElectricityStats = World.GetOrCreateSystemManaged<ElectricityStatisticsSystem>();
            m_ShadowTradeQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowExportState>());
            m_DistrictPowerQuery = GetEntityQuery(ComponentType.ReadOnly<DistrictPowerBufferSingleton>());
            // Producer query for actual production (m_LastProduction, not m_Capacity)
            // Needed for Service Contract penalties and actual solar/wind output
            // FIX: Exclude<Destroyed> prevents reading production from ruins
            m_ProducerQuery = GetEntityQuery(
                ComponentType.ReadOnly<ElectricityProducer>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Destroyed>()
            );

            // ContractData lives on its own entities (not on vanilla buildings),
            // so it needs a separate query. Same shape the SystemAPI iteration used
            // (RO + WithNone<Deleted>) — kept here so the seed path can call
            // ToComponentDataArray instead of SystemAPI.Query, matching the writer
            // path that already uses m_SingletonQuery + EntityManager directly.
            m_ContractQuery = GetEntityQuery(
                ComponentType.ReadOnly<ContractData>(),
                ComponentType.Exclude<Deleted>()
            );

            m_ExternalPowerQuery = GetEntityQuery(ComponentType.ReadOnly<ExternalPowerInput>());
            m_SingletonQuery = GetEntityQuery(ComponentType.ReadWrite<PowerGridSingleton>());
            m_StressDataQuery = GetEntityQuery(ComponentType.ReadOnly<GridStressData>());
            m_DistrictPowerEntryLookup = GetBufferLookup<DistrictPowerEntry>(true);
            m_DistrictEntityEntryLookup = GetBufferLookup<DistrictEntityEntry>(true);

            // Domain-Driven Initialization (Static Factory)
            PowerGridSingleton.EnsureExists(EntityManager);

            // Frame-local map for ContractData (separate entities, query each frame)
            m_ContractsByBuilding = new NativeHashMap<long, ContractData>(16, Allocator.Persistent);

            // NOTE: Removed RequireForUpdate - system must run even with 0 producers
            // (production=0 is valid state, consumption still needs to be calculated).
            //
            // Earlier this OnCreate logged producer/singleton counts and warned on
            // zero producers, but OnCreate fires during Mod.OnLoad in the main menu
            // (no city → no producers), making those messages misleading noise in
            // every fresh-launch log. Moved to OnStartRunning where they reflect
            // actual world state.
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_DistrictState ??= ServiceRegistry.Instance.Require<IDistrictStateReader>();
            PowerGridSingleton.EnsureExists(EntityManager);
        }

        [CompletesDependency("OnThrottledUpdate diagnostic path: producer count logged every 3rd throttle tick when Log.IsDebugEnabled; sync amortised over throttle interval, only when debug logging enabled")]
        protected override void OnThrottledUpdate()
        {
            m_DistrictPowerEntryLookup.Update(this);
            m_DistrictEntityEntryLookup.Update(this);

            using (Core.Utils.PerformanceProfiler.Measure("PowerGridData.OnUpdate"))
            {
                // Diagnostic: log every ~3 throttled updates (150*3 = 450 frames)
                m_DiagCounter++;
                if (m_DiagCounter >= 3)
                {
                    m_DiagCounter = 0;
                    int prodCount = m_ProducerQuery.CalculateEntityCount();
                    if (Log.IsDebugEnabled) Log.Debug($"[DIAG] PowerGridData OnUpdate: producers={prodCount}, vanilla_consumption={(m_ElectricityStats != null ? m_ElectricityStats.consumption : 0)}kW");
                }

                CalculatePowerData();
            }
        }

        private void CalculatePowerData()
        {
            var snapshot = AggregateSnapshot();
            WriteSnapshot(EntityManager, snapshot);
        }

        /// <summary>
        /// Pure read of every input source needed for the PowerGridSingleton snapshot.
        /// Returns the value; the singleton itself is written by WriteSnapshot so the
        /// post-load seed path and the normal tick path share one formula and one
        /// publish point.
        /// </summary>
        [CompletesDependency("AggregateSnapshot: producer count + per-entity producer/contract iteration; called from 1s throttled tick and one-shot post-load seed; sync amortised over throttle interval")]
        private PowerGridSingleton AggregateSnapshot()
        {
            int totalProduction = 0;
            int externalBonusMW = GetExternalPowerBonus();
            int producerCount = m_ProducerQuery.CalculateEntityCount();

            // Build frame-local map of ContractData by building index. ContractData
            // lives on SEPARATE entities (not on vanilla buildings), so we look it up
            // by packed Index+Version when iterating producers below.
            m_ContractsByBuilding.Clear();
            using (var contracts = m_ContractQuery.ToComponentDataArray<ContractData>(Allocator.Temp))
            {
                for (int i = 0; i < contracts.Length; i++)
                {
                    long contractKey = contracts[i].Building.Packed;
                    m_ContractsByBuilding.TryAdd(contractKey, contracts[i]);
                }
            }

            // Sum all producers (with Supply contract efficiency penalty).
            if (producerCount > 0)
            {
                using var producers = m_ProducerQuery.ToComponentDataArray<ElectricityProducer>(Allocator.Temp);
                using var producerEntities = m_ProducerQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < producers.Length; i++)
                {
                    var entity = producerEntities[i];
                    int production = producers[i].m_LastProduction;
                    long buildingContractKey = ((long)entity.Index << 32) | (uint)entity.Version;
                    if (m_ContractsByBuilding.TryGetValue(buildingContractKey, out var contract))
                    {
                        if (contract.Type == ContractType.Supply && contract.IsShady)
                        {
                            // Shady supply = degraded fuel/coal = reduced output.
                            // Quality 0.7 → 85% efficiency, Quality 0.5 → 75% efficiency.
                            float efficiency = 0.5f + (contract.Quality * 0.5f);
                            production = (int)Math.Round(production * efficiency);
                        }
                    }
                    totalProduction += production;
                }
            }

            // Demand comes from DistrictPowerSystem's last pass; both systems tick at 1s.
            int demandKW = 0;
            if (m_DistrictPowerQuery.TryGetSingleton<DistrictPowerBufferSingleton>(out var districtSingleton))
            {
                demandKW = districtSingleton.TotalDemandKW;
            }

            // Consumption = what active districts actually use (our calculation).
            int consumptionKW = CalculateActiveConsumption();

            // INVARIANT: consumption ≤ demand in a single pass; clamp on ECS edge cases.
            if (consumptionKW > demandKW)
            {
                m_InvariantViolations++;
                if (m_InvariantViolations <= 3 || m_InvariantViolations % 100 == 0)
                {
                    Log.Warn($"[INVARIANT] consumption ({consumptionKW}kW) > demand ({demandKW}kW) - clamping (count: {m_InvariantViolations})");
                }
                consumptionKW = demandKW;
            }

            int vanillaDemand = m_ElectricityStats != null ? m_ElectricityStats.consumption : 0;
            if (Math.Abs(demandKW - vanillaDemand) > 1000)
            {
                if (Log.IsDebugEnabled) Log.Debug($"[DIAG] demand divergence: ours={demandKW}kW vanilla={vanillaDemand}kW");
            }

            if (m_DiagDemandCounter++ % 60 == 0)
            {
                if (Log.IsDebugEnabled) Log.Debug($"[PowerGrid] demand={demandKW}kW, consumption={consumptionKW}kW");
            }

            // All values in kW (no MW conversion — preserves sub-MW precision).
            long productionKWLong = totalProduction;
            productionKWLong += (long)externalBonusMW * 1000L;
            int productionKW = ClampLongToInt(productionKWLong);

            int rawBalance = productionKW - consumptionKW;

            // Shadow export VOLUME ceiling comes from the capacity headroom (with the
            // legal export cap in place the flow RawBalance sits near zero and the old
            // flow-based ceiling would choke the covert channel forever); the legal and
            // covert channels never sell the same MW. The Balance SUBTRACTION stays
            // flow-clamped (the covert trade is a monetary layer and does not move the
            // physical flow): Balance semantics are unchanged, so its consumers
            // (CityStability, ThresholdOperation, statuses) live as before.
            // Until the first capacity snapshot is published the ceiling falls back to
            // the flow clamp alone — accepted one-resolver-tick window.
            int exportedKW = (m_ShadowTradeQuery.TryGetSingleton<ShadowExportState>(out var se)
                    ? se : ShadowExportState.Default)
                .ExportedMW * 1000;
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            if (m_PowerCapacitySnapshotReader.TryGetSnapshot(out var capacitySnapshot))
            {
                exportedKW = Math.Min(exportedKW, PowerHeadroomMath.ComputeShadowExportCapKW(
                    capacitySnapshot.CityDispatchableMW, consumptionKW, rawBalance,
                    ClampLongToInt((long)externalBonusMW * 1000L),
                    ImportCapRuntimeState.CurrentExportCapTotalKW));
            }
            int exportedDrainKW = Math.Min(exportedKW, Math.Max(0, rawBalance));
            int balance = rawBalance - exportedDrainKW;

            GridStressZone stressZone = GridStressZone.Normal;
            if (m_StressDataQuery.TryGetSingleton<GridStressData>(out var stressData))
            {
                stressZone = stressData.Zone;
            }

            // Status reflects frequency/stress in addition to balance: a +7 MW surplus
            // at 49.9 Hz frequency reads as WARNING because stress is still recovering.
            // Deficit side is gated by the same dead zone as GridStressSystem: vanilla
            // generation is demand-following, so the flow balance sits a hair negative
            // whenever load grows — a bare `< 0` makes the status flicker WARNING on noise.
            var gsCfg = BalanceConfig.Current.GridStress;
            GridStatusType statusType;
            if (balance < Engine.PowerGrid.CRITICAL_DEFICIT_THRESHOLD
                || stressZone == GridStressZone.Red
                || stressZone == GridStressZone.Collapsed)
            {
                statusType = GridStatusType.Critical;
            }
            else if (GridStressMath.IsDeficit(balance, gsCfg.DeficitDeadZoneMinKW, gsCfg.DeficitDeadZoneFraction, consumptionKW)
                || stressZone == GridStressZone.Yellow)
            {
                statusType = GridStatusType.Warning;
            }
            else if (balance > Engine.PowerGrid.SURPLUS_THRESHOLD
                && stressZone == GridStressZone.Normal)
            {
                statusType = GridStatusType.Surplus;
            }
            else
            {
                statusType = GridStatusType.Normal;
            }

            if (m_DebugCounter++ % 10 == 0)
            {
                if (Log.IsDebugEnabled) Log.Debug($"PowerGridData: {producerCount} producers | {productionKW / 1000}MW prod / {consumptionKW / 1000}MW cons = {balance / 1000}MW balance");
            }

            return new PowerGridSingleton
            {
                Production = productionKW,
                Demand = demandKW,
                Consumption = consumptionKW,
                RawBalance = rawBalance,
                Balance = balance,
                Status = statusType,
                ExternalPower = ClampLongToInt((long)externalBonusMW * 1000L),
                ShadowExportDrain = exportedDrainKW,
            };
        }

        /// <summary>
        /// Publish a snapshot to PowerGridSingleton and the ServedLoad reader-view.
        /// Uses the cached EntityQuery + EntityManager (no SystemAPI) so callers in
        /// post-load contexts get the same atomic write as the normal tick path.
        /// </summary>
        private void WriteSnapshot(EntityManager em, in PowerGridSingleton snapshot)
        {
            if (m_SingletonQuery.TryGetSingletonEntity<PowerGridSingleton>(out var entity))
            {
                em.SetComponentData(entity, snapshot);
            }
            ObserveServedLoad(snapshot.Consumption);
        }

        private int GetExternalPowerBonus()
        {
            // TOCTOU FIX: Use TryGetSingleton instead of IsEmpty + GetSingleton
            if (!m_ExternalPowerQuery.TryGetSingleton<ExternalPowerInput>(out var input))
                return 0;

            return input.BonusMW;
        }

        /// <summary>
        /// Calculate total consumption (in kW) from ACTIVE districts only.
        /// Single source of truth - reads from DistrictPowerBuffer singleton.
        /// </summary>
        private int CalculateActiveConsumption()
        {
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (!m_DistrictPowerQuery.TryGetSingletonEntity<DistrictPowerBufferSingleton>(out var singletonEntity))
                return 0;
            if (!m_DistrictPowerEntryLookup.TryGetBuffer(singletonEntity, out var powerBuffer)) return 0;
            DynamicBuffer<DistrictEntityEntry> districtEntities;
            bool hasDistrictEntities = m_DistrictEntityEntryLookup.TryGetBuffer(singletonEntity, out districtEntities);

            int activeKW = 0;

            var snapshot = m_DistrictState!.TakeSnapshot();

            for (int i = 0; i < powerBuffer.Length; i++)
            {
                var entry = powerBuffer[i];
                int districtIndex = entry.District.Index;
                var powerData = entry.Data;

                if (entry.District.IsNull)
                {
                    // Unzoned / No-District aggregate. PowerCalculationJob writes
                    // DistrictRef.Null (0,0) for buildings with m_District == Entity.Null;
                    // map to the logical id the blackout/snapshot layer keys Unzoned under
                    // and fall through to the same schedule/category gating as a real district.
                    districtIndex = Engine.Districts.NO_DISTRICT_INDEX;
                }
                else if (!HasLiveDistrictVersion(districtEntities, hasDistrictEntities, districtIndex, entry.District.Version))
                {
                    continue;
                }

                // Active KW after schedule / category blackout gating. Shared with the
                // per-district UI DEL column (DistrictDtoFactory) so the city consumption
                // aggregate and the district table can never diverge.
                activeKW += DistrictActiveLoad.ComputeActiveKW(in snapshot, districtIndex, in powerData);
            }

            return activeKW;
        }

        private void ObserveServedLoad(int consumptionKW)
        {
            m_ServedLoadView.Publish(new ServedLoadSnapshot(consumptionKW));
        }

        private static bool HasLiveDistrictVersion(
            DynamicBuffer<DistrictEntityEntry> districtEntities,
            bool hasDistrictEntities,
            int districtIndex,
            int districtVersion)
        {
            if (!hasDistrictEntities)
                return false;

            for (int i = 0; i < districtEntities.Length; i++)
            {
                var entry = districtEntities[i];
                if (entry.District.Index == districtIndex && entry.District.Version == districtVersion)
                    return true;
            }
            return false;
        }

        private static int ClampLongToInt(long value)
        {
            if (value > int.MaxValue)
                return int.MaxValue;
            if (value < int.MinValue)
                return int.MinValue;
            return checked((int)value);
        }

        /// <summary>
        /// SMELL-05 FIX: Reset diagnostic counters on new game (IResettable).
        /// </summary>
        public void ResetState()
        {
            m_DiagCounter = 0;
            m_DebugCounter = 0;
            m_DiagDemandCounter = 0;
            m_InvariantViolations = 0;
            m_ServedLoadView.Publish(ServedLoadSnapshot.Empty);
            if (m_ContractsByBuilding.IsCreated) m_ContractsByBuilding.Clear();
        }

        public void OnLoadRestore(EntityManager entityManager) => PowerGridSingleton.EnsureExists(entityManager);

        public void ValidateAfterLoad()
        {
            PowerGridSingleton.EnsureExists(EntityManager);

            // DistrictPower and ExternalPower are not IPostLoadValidation; invoke their
            // seed methods directly so DistrictPowerBufferSingleton.TotalDemandKW,
            // DynamicBuffer<DistrictPowerEntry> and ExternalPowerInput.BonusMW are all
            // populated before AggregateSnapshot reads them.
            // ORDER-INVARIANT: ExternalPowerSource.BonusMW is restored by
            // DonorConferenceSystem.OnLoadRestore in PLVS RestoreSingletonOwners,
            // which always precedes this RunValidation call.
            World.GetExistingSystemManaged<DistrictPowerSystem>()
                 ?.SeedFromRestoredVanillaData(EntityManager);
            World.GetExistingSystemManaged<ExternalPowerAggregationSystem>()
                 ?.SeedFromRestoredSources(EntityManager);

            // Refresh BufferLookups so the aggregation reads the freshly-seeded rows.
            m_DistrictPowerEntryLookup.Update(this);
            m_DistrictEntityEntryLookup.Update(this);

            // m_DistrictState normally initialises in OnStartRunning, but PLVS
            // ordering does not guarantee it ran before this validator. Lazy-init
            // here so CalculateActiveConsumption (called by AggregateSnapshot) does
            // not NRE on .TakeSnapshot().
            m_DistrictState ??= ServiceRegistry.Instance.Require<IDistrictStateReader>();

            var snapshot = AggregateSnapshot();
            WriteSnapshot(EntityManager, snapshot);
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IServedLoadVersionReader>(this);

            if (m_ContractsByBuilding.IsCreated)
                m_ContractsByBuilding.Dispose();

            // Consistency: destroy singleton entity created via EnsureExists (matches DistrictPowerSystem pattern)
            if (m_SingletonQuery.TryGetSingletonEntity<PowerGridSingleton>(out var singletonEntity))
                EntityManager.DestroyEntity(singletonEntity);

            Log.Info($"{nameof(PowerGridDataSystem)} destroyed");
            base.OnDestroy();
        }
    }
}
