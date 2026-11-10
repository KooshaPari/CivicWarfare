using Game;
using Game.Buildings;
using Game.Areas;
using Game.Common;
using Unity.Entities;
using Unity.Collections;
using System.Collections.Generic;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Corruption;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Localization;
using Game.Simulation;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Domains.Corruption.Systems.Modernization;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// District Modernization: Unified corruption scheme for backup power equipment.
    ///
    /// Mechanics:
    /// - One-time district modernization program
    /// - Player chooses: Honest contractor (reliable) vs Your Guy (counterfeit + kickback)
    /// - 30-day cooldown between ANY district procurements
    /// - Corrupt choice triggers investigation risk, fires, reputation loss
    ///
    /// Architecture (post-split): system owns ECS context (queries, lookups, ECB,
    /// event subscription, service registration, day-changed dispatch) and delegates
    /// procurement logic to <see cref="ModernizationProcurementProcessor"/>, program
    /// state to <see cref="ModernizationProgramStore"/>, counterfeit cleanup to
    /// <see cref="CounterfeitCleanupService"/>, eligibility/cost to
    /// <see cref="ModernizationEligibilityPolicy"/>, and equipment install to
    /// <see cref="CounterfeitEquipmentInstaller"/>.
    /// </summary>
    [ActIndependent]
#pragma warning disable CIVIC173 // False positive: live durability is DistrictModernizationIntent.
    [HandlesRequestKind(RequestKind.Modernization)]
    [TransientConsumerReconcile(typeof(ModernizationRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Transient UI command: modernization intent and wallet side-effects are created only after this consumer validates the request, so pre-consume load loss is reissuable.")]
    public partial class DistrictModernizationSystem : CivicSystemBase, IDistrictModernizationService, IPostLoadValidation, IModernizationProcurementHost
#pragma warning restore CIVIC173
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private const int INVESTIGATION_CHECK_INTERVAL_DAYS = 30;
        private const int RANDOM_SEED_OFFSET = 12345;
        private const uint SEED_DAY_MULTIPLIER = 31;
        private const int JOURNALIST_COUNT_FALLBACK = 5;

        private static readonly LogContext Log = new("DistrictModernization");

        // ============================================================================
        // STATE (serialized — m_DayDedup is the only direct serialized field on the
        // system; program dictionary lives in m_Store, cleanup state in m_Cleanup,
        // cooldown anchor in m_Processor.LastProcurementDay)
        // ============================================================================

        private DayChangedDedup m_DayDedup = default;

        // ============================================================================
        // DEPENDENCIES (vanilla systems, services, GameSimulationEndBarrier)
        // ============================================================================

        private IShadowReputationService m_ReputationService = null!;
        private IAreaCollectReader m_AreaCollect = null!;
        private Core.Systems.GameTimeSystem? m_TimeSystem;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        // ============================================================================
        // ECS QUERIES + LOOKUPS
        // ============================================================================

        private EntityQuery m_FireRecordRequestQuery;
        private EntityQuery m_BuildingsWithDistrictQuery;
        private EntityQuery m_CounterfeitQuery;
        private EntityQuery m_ResolvedBudgetQuery;
        private EntityQuery m_PendingModernizationBudgetQuery;
        private EntityQuery m_ModernizationInstallReceiptQuery;

        private IBackupPowerLinkReader m_LinkReader = null!;
        private ComponentLookup<CurrentDistrict> m_CurrentDistrictLookup;
        private ComponentLookup<BackupPower> m_BackupPowerLookup;
        private ComponentLookup<CounterfeitBattery> m_CounterfeitBatteryLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;

        // Bundle for IDistrictModernizationService entry points (GetEstimatedCost /
        // GetModernizationEligibility). Owns lookup freshness for synchronous district
        // queries answered outside the throttled OnUpdateImpl tick.
        [System.NonSerialized] private CivicServiceLookups m_DistrictLookups = null!;

        // ============================================================================
        // KNOWN-DISTRICTS VIEW (vanilla-driven, not procurement state)
        // ============================================================================

        [EntityQueryOrderCursor("Invalidates the known-district cache when the buildings-with-district query's archetype set changes.")]
        [System.NonSerialized] private int m_LastBuildingOrderVersion;
        [System.NonSerialized] private bool m_DistrictsDirty;
        [NonEntityIndex] private readonly HashSet<int> m_KnownDistrictsCache = new();
        [EntityQueryOrderCursor("Invalidates the known-district snapshot when the buildings-with-district query's archetype set changes.")]
        [System.NonSerialized] private int m_KnownDistrictsCacheVersion = -1;
        [System.NonSerialized] private int[] m_KnownDistrictsSnapshotSource = System.Array.Empty<int>();

        // ============================================================================
        // SUB-SERVICES
        // ============================================================================

        [System.NonSerialized] private ModernizationProgramStore m_Store = null!;
        [System.NonSerialized] private CounterfeitCleanupService m_Cleanup = null!;
        [System.NonSerialized] private ModernizationEligibilityPolicy m_Policy = null!;
        [System.NonSerialized] private CounterfeitEquipmentInstaller m_Installer = null!;
        [System.NonSerialized] private ModernizationProcurementProcessor m_Processor = null!;

        // ============================================================================
        // SERVICE FACADE — IDistrictModernizationService
        // ============================================================================

        public IVersionedView<ModernizationProgramsSnapshot>? ProgramsView => m_Store.View;

        public DistrictModernizationData? GetProgram(int districtIndex) => m_Store.GetProgram(districtIndex);

        public IReadOnlyCollection<int> ActiveProgramDistricts => m_Store.ActiveProgramDistricts;

        public IReadOnlyCollection<int> KnownDistricts
        {
            get
            {
                int currentVersion = m_BuildingsWithDistrictQuery.GetCombinedComponentOrderVersion(true);
                if (currentVersion != m_KnownDistrictsCacheVersion)
                {
                    m_KnownDistrictsCache.Clear();
                    var districts = m_BuildingsWithDistrictQuery.ToComponentDataArray<CurrentDistrict>(Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < districts.Length; i++)
                        {
#pragma warning disable CIVIC097 // CurrentDistrict.m_District.Index is a logical district id, not an entity index.
                            m_KnownDistrictsCache.Add(districts[i].m_District.Index);
#pragma warning restore CIVIC097
                        }
                    }
                    finally
                    {
                        if (districts.IsCreated) districts.Dispose();
                    }
                    m_KnownDistrictsCacheVersion = currentVersion;

                    m_KnownDistrictsSnapshotSource = m_KnownDistrictsCache.Count == 0
                        ? System.Array.Empty<int>()
                        : new List<int>(m_KnownDistrictsCache).ToArray();
                }
                return m_KnownDistrictsSnapshotSource;
            }
        }

        public int DaysUntilNextProcurement => m_Processor.DaysUntilNextProcurement;

        public IReadOnlyCollection<int> PendingCounterfeitCleanupDistricts => m_Cleanup.PendingDistricts;

        public void CopyPendingCounterfeitCleanupDistricts(List<int> target) => m_Cleanup.CopyPendingDistricts(target);

        public void CopyPendingCounterfeitCleanupBuildingKeys(List<long> target) => m_Cleanup.CopyPendingBuildingKeys(target);

        public void ClearPendingCounterfeitCleanup()
        {
            if (m_Cleanup.ClearPending())
                m_Store.Publish();
        }

        public int GetEstimatedCost(int districtIndex)
        {
            m_DistrictLookups.RefreshIfStale();
            int targetCount = ComputeSyncTargetCount(districtIndex);
            return (int)System.Math.Round(targetCount * BalanceConfig.Current.ShadowProcurement.CostPerBuilding);
        }

        public EligibilityFlag GetModernizationEligibility(int districtIndex, ContractorType contractor)
        {
            m_DistrictLookups.RefreshIfStale();
            int targetCount = ComputeSyncTargetCount(districtIndex);
            long totalCost = (long)System.Math.Round(targetCount * BalanceConfig.Current.ShadowProcurement.CostPerBuilding);
            return m_Policy.GetEligibility(
                contractor,
                m_Processor.HasPendingProcurement,
                m_Processor.DaysUntilNextProcurement,
                targetCount,
                totalCost,
                World);
        }

        private int ComputeSyncTargetCount(int districtIndex)
        {
            var (unprotected, replacing) = m_Policy.Count(
                districtIndex, m_BuildingsWithDistrictQuery, m_CurrentDistrictLookup, m_LinkReader);
            if (!replacing)
                return unprotected;
            return unprotected + m_Cleanup.CountCounterfeitInDistrict(
                districtIndex, m_CounterfeitQuery, m_CurrentDistrictLookup);
        }

        // ============================================================================
        // IModernizationProcurementHost (internal contract for sub-services)
        // ============================================================================

        World IModernizationProcurementHost.World => World;
        IEventBus? IModernizationProcurementHost.EventBus => EventBus;
        GameSimulationEndBarrier IModernizationProcurementHost.GameSimulationEndBarrier => m_GameSimulationEndBarrier;
        IShadowReputationService IModernizationProcurementHost.ReputationService => m_ReputationService;
        IShadowWalletService IModernizationProcurementHost.ResolveWalletService()
            => ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
        Core.Systems.GameTimeSystem? IModernizationProcurementHost.ResolveTimeSystem() => ResolveTimeSystem();
        double IModernizationProcurementHost.ElapsedTime => SystemAPI.Time.ElapsedTime;

        EntityQuery IModernizationProcurementHost.BuildingsWithDistrictQuery => m_BuildingsWithDistrictQuery;
        EntityQuery IModernizationProcurementHost.CounterfeitQuery => m_CounterfeitQuery;
        EntityQuery IModernizationProcurementHost.PendingModernizationBudgetQuery => m_PendingModernizationBudgetQuery;
        EntityQuery IModernizationProcurementHost.ModernizationInstallReceiptQuery => m_ModernizationInstallReceiptQuery;

        ComponentLookup<CurrentDistrict> IModernizationProcurementHost.CurrentDistrictLookup => m_CurrentDistrictLookup;
        IBackupPowerLinkReader IModernizationProcurementHost.BackupPowerLinks => m_LinkReader;
        ComponentLookup<BackupPower> IModernizationProcurementHost.BackupPowerLookup => m_BackupPowerLookup;
        ComponentLookup<CounterfeitBattery> IModernizationProcurementHost.CounterfeitBatteryLookup => m_CounterfeitBatteryLookup;
        ComponentLookup<Deleted> IModernizationProcurementHost.DeletedLookup => m_DeletedLookup;

        EntityCommandBuffer IModernizationProcurementHost.CreateCommandBuffer() => m_GameSimulationEndBarrier.CreateCommandBuffer();

        // CIVIC356 suppressed: this host method is invoked only from sub-services
        // running inside OnUpdate / DayChanged handlers — Dependency is the system's
        // current job handle, not default. The analyzer can't see the dynamic call
        // chain across the IModernizationProcurementHost interface boundary.
#pragma warning disable CIVIC356
        void IModernizationProcurementHost.RegisterECBProducer() => m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
#pragma warning restore CIVIC356

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            Log.Info($"{nameof(DistrictModernizationSystem)} created");

            m_TimeSystem = World.GetExistingSystemManaged<Core.Systems.GameTimeSystem>();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            if (m_TimeSystem == null)
                Log.Warn("GameTimeSystem not found during OnCreate; will retry lazily");

            m_CurrentDistrictLookup = GetComponentLookup<CurrentDistrict>(true);
            m_BackupPowerLookup = GetComponentLookup<BackupPower>(true);
            m_CounterfeitBatteryLookup = GetComponentLookup<CounterfeitBattery>(true);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);

            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_DistrictLookups = new CivicServiceLookups(() =>
            {
                m_CurrentDistrictLookup.Update(this);
                m_BackupPowerLookup.Update(this);
                m_CounterfeitBatteryLookup.Update(this);
                m_DeletedLookup.Update(this);
            });

            m_BuildingsWithDistrictQuery = GetEntityQuery(
                ComponentType.ReadOnly<CurrentDistrict>(),
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<ElectricityConsumer>(),
                ComponentType.Exclude<Deleted>());

            m_CounterfeitQuery = GetEntityQuery(
                ComponentType.ReadOnly<CounterfeitBattery>(),
                ComponentType.Exclude<Deleted>());

            m_FireRecordRequestQuery = GetEntityQuery(ComponentType.ReadOnly<FireRecordRequest>());

            m_ResolvedBudgetQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<BudgetDeductResult>(),
                ComponentType.ReadOnly<DistrictModernizationIntent>(),
                ComponentType.ReadWrite<PendingPhase>());
            m_PendingModernizationBudgetQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<DistrictModernizationIntent>());
            m_ModernizationInstallReceiptQuery = GetEntityQuery(
                ComponentType.ReadOnly<ModernizationInstallReceipt>(),
                ComponentType.Exclude<Deleted>());

            // Wire sub-services (Store → Cleanup → Policy → Installer → Processor)
            m_Cleanup = new CounterfeitCleanupService();
            m_Store = new ModernizationProgramStore(m_Cleanup);
            m_Policy = new ModernizationEligibilityPolicy(m_Store);
            m_Installer = new CounterfeitEquipmentInstaller(m_Cleanup);
            m_Processor = new ModernizationProcurementProcessor(this, m_Store, m_Cleanup, m_Policy, m_Installer);

            // L-104: Use Cleanup priority (40) so district removal runs before StateChange (50) readers like CounterfeitBatteryFireSystem
            SubscribeRequired<DayChangedEvent>(OnDayChanged, DayEventPriority.Cleanup);

            // Producer-side registration MUST happen in OnCreate, not OnStartRunning:
            // OnStartRunning fires only on first Update, which never arrives if
            // GameSimulation phase never ticks (e.g. UI consumer in MainMenu hits us first).
            ServiceRegistry.Instance.Register<IDistrictModernizationService>(this);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            // Consumer-side resolve: TryGetOrNullObject requires FeatureRegistry boot complete,
            // so it stays in OnStartRunning (not OnCreate).
            m_ReputationService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowReputationService.Instance);
            m_AreaCollect = ServiceRegistry.Instance.Require<IAreaCollectReader>();
            m_LinkReader = ServiceRegistry.Instance.Require<IBackupPowerLinkReader>();
        }

        private Core.Systems.GameTimeSystem? ResolveTimeSystem()
        {
            m_TimeSystem ??= World.GetExistingSystemManaged<Core.Systems.GameTimeSystem>();
            return m_TimeSystem;
        }

        protected override void OnUpdateImpl()
        {
            m_CurrentDistrictLookup.Update(this);
            m_BackupPowerLookup.Update(this);
            m_CounterfeitBatteryLookup.Update(this);
            m_DeletedLookup.Update(this);

            // Latch district boundary changes (per-frame flag → persistent dirty)
            if (m_AreaCollect.DistrictsUpdated)
            {
                m_DistrictsDirty = true;
                m_KnownDistrictsCacheVersion = -1;
            }

            // Drain resolved procurement budget requests before processing new ones
            DrainResolvedProcurementRequests();

            ProcessModernizationRequests();
            ProcessFireRecordRequests();
            // Monthly investigation risk checks handled in OnDayChanged
        }

        /// <summary>
        /// Query resolved procurement budget requests and dispatch to Processor.
        /// Budget outcome lives on the durable entity (save-safe).
        /// </summary>
        private void DrainResolvedProcurementRequests()
        {
            if (m_ResolvedBudgetQuery.IsEmpty)
                return;

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            var durableEcb = new EntityCommandBuffer(Allocator.Temp);

            try
            {
                foreach (var (intent, result, phase, entity) in
                    SystemAPI.Query<RefRW<DistrictModernizationIntent>, RefRO<BudgetDeductResult>, RefRW<PendingPhase>>()
                    .WithAll<BudgetDeductRequest>()
                    .WithEntityAccess())
                {
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    bool hasMeta = SystemAPI.HasComponent<RequestMeta>(entity);
                    var meta = hasMeta ? SystemAPI.GetComponent<RequestMeta>(entity) : default;

                    m_Processor.ProcessResolvedBudget(ecb, durableEcb, entity, ref intent.ValueRW, in result.ValueRO, ref phase.ValueRW, hasMeta, meta);
                }

                durableEcb.Playback(EntityManager);
            }
            finally
            {
                durableEcb.Dispose();
            }

            if (hasEcb)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        /// <summary>
        /// Process ModernizationRequest ephemeral entities.
        /// Data-Driven Commands pattern - UI creates entity, domain system processes.
        /// Status=Success means "request accepted, budget pending" — not "procurement completed".
        /// Actual completion/failure notified via ShadowNarrativeEvent (Procurement/ProcurementFailed).
        /// </summary>
        private void ProcessModernizationRequests()
        {
            // Reset same-frame reentrancy latch; the durable intent query owns
            // the cross-frame invariant once the barrier materialized it.
            m_Processor.BeginFrameLatchReset();

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            foreach (var (request, meta, entity) in
                SystemAPI.Query<RefRO<ModernizationRequest>, RefRO<RequestMeta>>()
                .WithEntityAccess())
            {
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }

                var (success, failReason) = m_Processor.ActivateProcurement(request.ValueRO.DistrictIndex, request.ValueRO.Contractor, meta.ValueRO);
                if (!success)
                    RequestResultEmitter.Emit(ecb, meta.ValueRO, RequestKind.Modernization, RequestStatus.Failed, ReasonId.FromRuntime(failReason), SystemAPI.Time.ElapsedTime);

                if (Log.IsDebugEnabled) Log.Debug($"[DistrictModernization] Processed ModernizationRequest: district {request.ValueRO.DistrictIndex} = {(success ? "Success" : "Failed")}");
                ecb.DestroyEntity(entity);
            }

            if (hasEcb) m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        private void ProcessFireRecordRequests()
        {
            if (m_FireRecordRequestQuery.IsEmpty) return;

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<FireRecordRequest>>()
                .WithEntityAccess())
            {
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                RecordFire(request.ValueRO.DistrictIndex, request.ValueRO.DayNumber);
                ecb.DestroyEntity(entity);
            }

            if (hasEcb) m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        private void RecordFire(int districtIndex, int day)
        {
            if (m_Store.RecordFire(districtIndex, day))
            {
                var program = m_Store.GetProgram(districtIndex);
                if (program.HasValue)
                    Log.Info($"District {districtIndex}: Fire #{program.Value.FireCount} recorded on day {day}");
            }
        }

        [CompletesDependency("ValidateAfterLoad: one-shot post-load replay of retained FireRecordRequest entities; CalculateEntityCount is diagnostic-only, sync amortised against the replay loop that follows")]
        public void ValidateAfterLoad()
        {
            ClampLastProcessedDayAfterLoad();
            m_CurrentDistrictLookup.Update(this);
            m_BackupPowerLookup.Update(this);
            m_CounterfeitBatteryLookup.Update(this);
            m_DeletedLookup.Update(this);

            // Rebuild the pending counterfeit-cleanup building keys from live components.
            // The keys are derived state (pending districts × live CounterfeitBattery) and
            // are not persisted, because the packed Index+Version is not remapped on load.
            // This runs in ValidateAfterLoad (IPostLoadValidation), after every sibling
            // Deserialize has completed and the engine has remapped CounterfeitBattery
            // entity refs, and after m_PendingDistricts was restored in Deserialize.
            m_Cleanup.RebuildPendingBuildingKeysFromLive(m_CounterfeitQuery, m_DeletedLookup);

            ReconcileModernizationIntentsAfterLoad();

            if (m_FireRecordRequestQuery.IsEmptyIgnoreFilter)
                return;

            int replayed = m_FireRecordRequestQuery.CalculateEntityCount();

            foreach (var request in SystemAPI.Query<RefRO<FireRecordRequest>>())
                RecordFire(request.ValueRO.DistrictIndex, request.ValueRO.DayNumber);

            EntityManager.DestroyEntity(m_FireRecordRequestQuery);

            if (replayed > 0)
                Log.Info($"ValidateAfterLoad: replayed and destroyed {replayed} fire record request entities");
        }

        private void ReconcileModernizationIntentsAfterLoad()
        {
            if (m_PendingModernizationBudgetQuery.IsEmptyIgnoreFilter)
                return;

            int reconciled = 0;
            var durableEcb = new EntityCommandBuffer(Allocator.Temp);
            try
            {
                foreach (var (intent, phase) in
                    SystemAPI.Query<RefRW<DistrictModernizationIntent>, RefRW<PendingPhase>>()
                    .WithAll<BudgetDeductRequest>())
                {
                    m_Processor.ReconcileAfterLoad(ref intent.ValueRW, ref phase.ValueRW, durableEcb);
                    reconciled++;
                }

                durableEcb.Playback(EntityManager);
            }
            finally
            {
                durableEcb.Dispose();
            }

            if (reconciled > 0)
                Log.Info($"ValidateAfterLoad: reconciled {reconciled} modernization intent entities");
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<DayChangedEvent>(OnDayChanged);

            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IDistrictModernizationService>(this);

            Log.Info($"{nameof(DistrictModernizationSystem)} destroyed");
            base.OnDestroy();
        }

        // ============================================================================
        // DAY-CHANGED: stale district cleanup + monthly investigation risk
        // ============================================================================

        private void OnDayChanged(DayChangedEvent evt)
        {
            if (m_DayDedup.AlreadyProcessed(evt.DayNumber)) return;

            using var _ = PerformanceProfiler.Measure("DistrictModernization.OnDayChanged");

            // Event-driven stale district cleanup:
            // OrderVersion detects structural changes (demolition/construction).
            // m_DistrictsDirty latches UpdateCollectSystem.districtsUpdated (boundary redraw).
            // Sync point only when either signal fires — zero cost on quiet days.
            if (m_Store.Count > 0)
            {
                int orderVersion = m_BuildingsWithDistrictQuery.GetCombinedComponentOrderVersion(true);
                if (m_DistrictsDirty || orderVersion != m_LastBuildingOrderVersion)
                {
                    m_LastBuildingOrderVersion = orderVersion;
                    m_DistrictsDirty = false;
                    // OnDayChanged runs in GameTimeSystem.OnUpdate context, not ours —
                    // lookups have not been refreshed this frame. CleanupStaleDistricts
                    // reads CurrentDistrictLookup via the host, so refresh the bundle
                    // before delegating.
                    m_DistrictLookups.RefreshIfStale();
                    m_Processor.CleanupStaleDistricts();
                }
            }

            // Monthly investigation risk check (skip day 0 — no programs exist yet)
            if (evt.DayNumber == 0 || evt.DayNumber % INVESTIGATION_CHECK_INTERVAL_DAYS != 0)
                return;

            int corruptCount = 0;
            int totalKickbacks = 0;
            foreach (var kvp in m_Store.Enumerate())
            {
                var p = kvp.Value;
                if (p.Contractor == ContractorType.YourGuy)
                    corruptCount++;
                if (p.KickbackEarned > 0)
                    totalKickbacks += p.KickbackEarned;
            }
            if (corruptCount == 0 && totalKickbacks == 0)
                return;

            var spCfg = BalanceConfig.Current.ShadowProcurement;
            float baseRisk = spCfg.InvestigationBaseRisk;
            float perDistrictRisk = spCfg.InvestigationPerDistrict;
            float totalRisk = System.Math.Min(1f, baseRisk + (corruptCount * perDistrictRisk));

            // FIX W1-M10: TickCount salt prevents savescumming (different each load + each day)
            // FIX W9-M7: Ensure seed != 0 — Unity.Mathematics.Random(0) produces degenerate sequence
            var random = new Unity.Mathematics.Random(((uint)evt.DayNumber * SEED_DAY_MULTIPLIER + (uint)(System.Environment.TickCount & 0x7FFFFFFF) + (uint)RANDOM_SEED_OFFSET) | 1u);
            if (random.NextFloat() < totalRisk)
            {
                Log.Warn($"Investigation triggered! ({corruptCount} corrupt districts, {totalRisk:P0} risk)");

                // Fine = base administrative penalty per corrupt district + restitution of ALL kickbacks.
                // FIX W9-M4: Kickback penalty includes ALL districts with KickbackEarned > 0,
                // not just current YourGuy districts.
                int baseFine = corruptCount * spCfg.InvestigationBaseFinePerDistrict;
                int kickbackPenalty = (int)System.Math.Round(totalKickbacks * spCfg.InvestigationFineMultiplier);
                int fineAmount = baseFine + kickbackPenalty;

                int journalistCount = LocalizationManager.GetPositiveInt("JOURNALIST_COUNT", JOURNALIST_COUNT_FALLBACK);
                string journalistName = LocalizationManager.Get($"JOURNALIST_NAME_{random.NextInt(journalistCount) + 1}") ?? "An auditor";

                EventBus?.SafePublish(new InvestigationStartedEvent(journalistName, fineAmount), "DistrictModernizationSystem");
            }
        }
    }
}
