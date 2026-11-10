using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Services.Economy;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.ShadowEconomy.Systems
{
    /// <summary>
    /// Sole owner of ShadowImportState + ShadowExportState singletons (split from ShadowTradeState).
    /// Handles all shadow trade operations: daily logic + the pause-safe UI command surface.
    ///
    /// UI commands (sync, AXIOM 14 route 3 — applied on the UI thread via
    /// TryApplyImportMWImmediate / TryApplyExportPercentImmediate, so both sliders
    /// work while the simulation is paused):
    /// - SetImportMW: Validate sanctions, mutual exclusion, clamp, corruption on activation
    /// - SetExportPercent: Mutual exclusion, clamp, reset accumulation timer
    ///
    /// Import daily logic:
    /// - Cost deduction from shadow wallet
    /// - Risk escalation based on days active
    /// - Discovery check (random roll against risk)
    /// - Sanction handling
    ///
    /// Export daily logic:
    /// - Calculate exported MW from surplus
    /// - Accumulate income to shadow wallet
    /// - Suspicion check based on corruption level
    /// </summary>
    // Contract: ShadowTrade intentionally reads CountermeasuresCoreFsm
    // as previous-frame state. Do not order after CountermeasuresReadyMarker,
    // otherwise ShadowTrade -> Corruption -> Countermeasures forms a cycle.
    [SingletonOwner(typeof(ShadowImportState))]
    [SingletonOwner(typeof(ShadowExportState))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    public partial class ShadowTradeDailySystem : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation, IShadowTradeConsumerReadiness, IActGatedSystem
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private const float FALLBACK_SHADOW_IMPORT_PRICE = 600f;
        // R9-M12 FIX: Read from config (was hardcoded 25f, must match CountermeasuresConfig.SuspicionThreshold)
        private static float SUSPICION_CORRUPTION_THRESHOLD => BalanceConfig.Current.Countermeasures.SuspicionThreshold;

        private static readonly LogContext Log = new("ShadowTradeDailySystem");

        public Act MinActiveAct => Act.PreWar;
        public ActGateState GateState => m_Gate?.State ?? ActGateState.AwaitingActState;

        private EntityQuery m_PowerGridQuery;
        private EntityQuery m_SingletonQuery;
        private EntityQuery m_CurrentActQuery;
#pragma warning disable CIVIC324 // Ephemeral act-gate controller; recreated by OnCreate, reset paths, and Deserialize.
        [NonSerialized] private ActGateController m_Gate = null!;
#pragma warning restore CIVIC324
        private EntityQuery m_CountermeasuresQuery;
        private EntityQuery m_ShadowExportQuery;
        private EntityQuery m_ShadowImportQuery;
        // Default to the null-object so `CanConsumeShadowTradeRequests` is safe to
        // read before OnStartRunning wires the real service. Returns IsOperational=false
        // until the wallet producer is resolved.
        private IShadowWalletService m_Wallet = NullShadowWalletService.Instance;
        private GameTimeSystem? m_TimeProvider;
        private ModSettings? m_Settings;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private DayChangedDedup m_DayDedup = default;
        [NonSerialized] private bool m_ImportDeductFailed; // Runtime flag: set by ShadowDeductFailedEvent, cleared on next DayChanged/load
        [NonSerialized] private bool m_PendingPostLoadImportProjection;
        // Note: CorruptionScore and CurrentPhase read from CountermeasuresCoreFsm singleton (ECS-pure)

        // MinValue sentinel on load forces first-tick recalculation; it is a
        // derived shadow-cap cache, not save state.
#pragma warning disable CIVIC221 // Derived transient cache reset by Deserialize/ValidateAfterLoad.
        [NonSerialized] private int m_LastShadowCapMW = int.MinValue;
#pragma warning restore CIVIC221
        private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PowerGridQuery = GetEntityQuery(
                ComponentType.ReadOnly<PowerGridSingleton>()
            );
            m_SingletonQuery = GetEntityQuery(
                ComponentType.ReadWrite<ShadowImportState>(),
                ComponentType.ReadWrite<ShadowExportState>()
            );
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_CountermeasuresQuery = GetEntityQuery(ComponentType.ReadOnly<CountermeasuresCoreFsm>());
            m_ShadowExportQuery = GetEntityQuery(ComponentType.ReadWrite<ShadowExportState>());
            m_ShadowImportQuery = GetEntityQuery(ComponentType.ReadWrite<ShadowImportState>());

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            // Require singletons exist
            RequireForUpdate<ShadowImportState>();
            RequireForUpdate<ShadowExportState>();

            SubscribeRequired<DayChangedEvent>(OnDayChanged, DayEventPriority.Cost);
            SubscribeRequired<ShadowDeductFailedEvent>(OnDeductFailed);
            SubscribeRequired<ShadowIncomeAppliedEvent>(OnShadowIncomeApplied);

            // S2-08: Post-load reconciliation (ExportLastAccumulationTime clock drift)
            InitializeGate();

            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IShadowTradeConsumerReadiness>(this);

            Log.Info("Created (active from PreWar)");
        }

        public bool CanConsumeShadowTradeRequests
            => RefreshGate() == ActGateState.Active && m_Wallet.IsOperational;

        private ActGateState RefreshGate()
        {
            m_Gate.ReconcileFromSingleton(m_CurrentActQuery);
            return m_Gate.State;
        }

        /// <summary>
        /// S2-08: Reset ExportLastAccumulationTime to force re-initialization on first frame.
        /// Prevents income burst from absolute-time clock drift between save and load sessions.
        /// The L501 guard (ExportLastAccumulationTime &lt;= 0) will re-initialize to current time.
        /// </summary>
        public void ValidateAfterLoad()
        {
            // Clear session-ephemeral flag — meaningful only within a single frame (ECB fail → next day cleanup)
            m_ImportDeductFailed = false;
            m_PendingPostLoadImportProjection = true;
            m_LastShadowCapMW = int.MinValue;

            if (m_PendingBootDefaultComponentReset)
            {
                ResetComponentState();
                m_PendingBootDefaultComponentReset = false;
            }

            ShadowImportState.EnsureExists(EntityManager);
            if (!m_SingletonQuery.TryGetSingletonEntity<ShadowImportState>(out var singletonEntity))
            {
                Log.Info("S2-08: ShadowTradeState singleton not found — skipping export timestamp rebase");
                return;
            }

            var export = EntityManager.GetComponentData<ShadowExportState>(singletonEntity);
            var import = EntityManager.GetComponentData<ShadowImportState>(singletonEntity);

            if (ApplyPostLoadImportProjection(ref import))
                EntityManager.SetComponentData(singletonEntity, import);

            if (export.ExportLastAccumulationTime > 0.0)
            {
                Log.Info($"S2-08: Reset ExportLastAccumulationTime ({export.ExportLastAccumulationTime:F0} → 0) — will re-init on next frame");
                export.ExportLastAccumulationTime = 0.0;
                EntityManager.SetComponentData(singletonEntity, export);
            }
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            // Self-wiring: resolve cross-domain services when system actually runs
            m_Wallet = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            m_TimeProvider ??= GameTimeSystem.Instance;
            m_Settings = ServiceRegistry.Instance.Require<ModSettings>();
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IShadowTradeConsumerReadiness>(this);

            UnsubscribeSafe<DayChangedEvent>(OnDayChanged);
            UnsubscribeSafe<ShadowDeductFailedEvent>(OnDeductFailed);
            UnsubscribeSafe<ShadowIncomeAppliedEvent>(OnShadowIncomeApplied);
            base.OnDestroy();
        }

        private void InitializeGate()
        {
            m_Gate = new ActGateController(
                // Shadow trade runs from PreWar — accumulate offshore cash before the war.
                isOpenFor: _ => true,
                onTransition: HandleGateTransition);
        }

        private void HandleGateTransition(ActGateState old, ActGateState next, bool isInitial)
        {
            if (next == ActGateState.Active)
            {
                if (!isInitial)
                {
                    ResetAccumulationTimer();
                    Log.Info("[ShadowTradeDaily] Gate opened");
                }

                return;
            }

            if (next == ActGateState.Inactive && !isInitial)
            {
                m_DayDedup.Reset();
                ResetAccumulationTimer();
                Log.Info("[ShadowTradeDaily] Gate closed");
            }
        }

        private void ResetAccumulationTimer()
        {
            if (m_ShadowExportQuery.TryGetSingletonRW<ShadowExportState>(out var export))
            {
                export.ValueRW.ExportLastAccumulationTime = 0.0;
                export.ValueRW.ExportIncomeRemainder = 0.0;
            }
        }

        private void OnDeductFailed(ShadowDeductFailedEvent evt)
        {
            // M61 FIX: Guard against inactive gate — stale flag on reopen triggers
            // false "deduction failed" treatment on the first ProcessImportDaily call.
            if (RefreshGate() != ActGateState.Active) return;
            if (evt.Reason != "ShadowImport") return;
            m_ImportDeductFailed = true;
        }

        protected override void OnUpdateImpl()
        {
            if (RefreshGate() != ActGateState.Active)
                return;

            if (!m_ShadowImportQuery.TryGetSingletonRW<ShadowImportState>(out var importRef))
                return;
            if (!m_ShadowExportQuery.TryGetSingletonRW<ShadowExportState>(out var exportRef))
                return;

            if (m_PendingPostLoadImportProjection && !ApplyPostLoadImportProjection(ref importRef.ValueRW))
                return;

            // Continuous export: calculate first so AccumulateExportIncome uses current-frame ExportDailyIncome
            CalculateExport(ref exportRef.ValueRW);
            AccumulateExportIncome(ref exportRef.ValueRW);
        }

        // ============================================================================
        // PAUSE-SAFE UI COMMAND SURFACE (AXIOM 14 route 3)
        // Sync apply on the UI thread — replaces the former ShadowTradeRequest ECB
        // consumer, which lived in GameSimulation and never ran while paused.
        // ============================================================================

        /// <summary>Sentinel for direct-MW import commands (no preset selected).</summary>
        internal const int NoPresetPercent = -1;

        /// <summary>
        /// Apply an export-percent command synchronously. Pause-safe: mutates the
        /// persisted ShadowExportState singleton on the calling (UI) thread.
        /// </summary>
        internal bool TryApplyExportPercentImmediate(int percent, out string failReason)
        {
            if (!TryBeginImmediateCommand(out var import, out var export, out failReason))
                return false;

            bool ok = ProcessSetExportPercent(percent, ref import.ValueRW, ref export.ValueRW);
            if (!ok)
                failReason = ReasonIds.ShadowTradeUnknownError;
            return ok;
        }

        /// <summary>
        /// Apply an import-MW command synchronously. Pause-safe: mutates the persisted
        /// ShadowImportState singleton on the calling (UI) thread. Price-lock fields
        /// (<paramref name="expectedMaxMW"/>/<paramref name="expectedDailyCost"/>) keep the
        /// stale-quote protection of the old request path.
        /// </summary>
        internal bool TryApplyImportMWImmediate(int mw, int presetPercent, int expectedMaxMW, long expectedDailyCost, out string failReason)
        {
            if (!TryBeginImmediateCommand(out var import, out var export, out failReason))
                return false;

            return ProcessSetImportMW(mw, presetPercent, expectedMaxMW, expectedDailyCost, ref import.ValueRW, ref export.ValueRW, out failReason);
        }

        /// <summary>
        /// Shared preflight for the sync command surface: act gate + singleton
        /// availability + the one-frame post-load projection window.
        /// </summary>
        private bool TryBeginImmediateCommand(out RefRW<ShadowImportState> import, out RefRW<ShadowExportState> export, out string failReason)
        {
            import = default;
            export = default;

            if (RefreshGate() != ActGateState.Active || m_PendingPostLoadImportProjection)
            {
                failReason = ReasonIds.MarketStateUnavailable;
                return false;
            }

            if (!m_ShadowImportQuery.TryGetSingletonRW<ShadowImportState>(out import)
                || !m_ShadowExportQuery.TryGetSingletonRW<ShadowExportState>(out export))
            {
                failReason = ReasonIds.MarketStateUnavailable;
                return false;
            }

            failReason = "";
            return true;
        }

        private bool ProcessSetImportMW(int mw, int presetPercent, int expectedMaxMW, long expectedDailyCost, ref ShadowImportState import, ref ShadowExportState export, out string failReason)
        {
            failReason = "";
            bool hasPresetPercent = presetPercent != NoPresetPercent;
            if (mw < 0)
            {
                failReason = ReasonIds.ShadowTradeInvalidImportAmount;
                return false;
            }

            if (!ShadowImportEligibility.ShadowImportAvailable(
                    import.ImportIsSanctioned,
                    m_Wallet.IsFrozen,
                    out failReason))
            {
                Log.Info($"Cannot set import - {failReason} ({import.ImportSanctionDaysRemaining} days remaining)");
                return false;
            }

            int maxMW = CalculateMaxImport();
            if (!hasPresetPercent && expectedMaxMW != maxMW)
            {
                failReason = ReasonIds.ShadowTradeImportCapacityChanged;
                Log.Info($"Cannot set import - capacity changed ({expectedMaxMW} -> {maxMW} MW)");
                return false;
            }

            int clampedMW = hasPresetPercent
                ? ShadowImportCalculator.CalculateImportMWForPercent(maxMW, presetPercent)
                : math.clamp(mw, 0, maxMW);
            if (!hasPresetPercent && clampedMW != mw)
            {
                failReason = ReasonIds.ShadowTradeImportCapacityChanged;
                Log.Info($"Cannot set import - requested {mw} MW exceeds max {maxMW} MW");
                return false;
            }

            float price = m_Settings?.ShadowImportPrice ?? FALLBACK_SHADOW_IMPORT_PRICE;
            long baseCost = ShadowImportCalculator.CalculateDailyCostLong(clampedMW, price);
            float markup = m_Wallet.SanctionsMarkup;
            long effectiveCost = SanctionsCostHelper.ApplyMarkup(baseCost, markup);
            if (!hasPresetPercent && effectiveCost != expectedDailyCost)
            {
                failReason = ReasonIds.ShadowTradeImportPriceChanged;
                Log.Info($"Cannot set import - price changed (${expectedDailyCost:N0} -> ${effectiveCost:N0})");
                return false;
            }

            var wallet = m_Wallet;
            bool walletAvailable = wallet.IsOperational;
            bool walletFrozen = wallet.IsFrozen;
            long walletBalance = wallet.Balance;

            if (clampedMW > 0 && !ShadowImportEligibility.CanSetImportMW(
                    importStateAvailable: true,
                    walletAvailable: walletAvailable,
                    walletFrozen: walletFrozen,
                    walletBalance: walletBalance,
                    effectiveCost: effectiveCost,
                    out failReason))
            {
                Log.Info($"Cannot set import - insufficient offshore funds for ${effectiveCost:N0}/day");
                return false;
            }

            // Mutual exclusion: disable export if enabling import
            if (clampedMW > 0 && export.ExportPercentage > 0)
            {
                export.ExportPercentage = 0;
                export.ExportLastAccumulationTime = 0.0;
                export.ExportIncomeRemainder = 0.0;
                export.ExportedMW = 0;
                export.ExportDailyIncome = 0;
                m_LastShadowCapMW = int.MinValue;
                Log.Info("Auto-disabled export (import enabled)");
            }

            int previousMW = import.ImportMW;
            import.ImportMW = clampedMW;

            // First activation - add corruption
            if (previousMW == 0 && import.ImportMW > 0)
            {
                import.ImportDaysActive = 0;
                EventBus?.SafePublish(new CorruptionGainEvent(
                    BalanceConfig.Current.ShadowImport.CorruptionOnActivate,
                    "ShadowImport"), "ShadowTradeDailySystem");
                Log.Info($"Import ACTIVATED at {import.ImportMW} MW (max {maxMW})");
            }
            else if (import.ImportMW == 0 && previousMW > 0)
            {
                Log.Info($"Import DEACTIVATED (was {previousMW} MW)");
            }
            else if (import.ImportMW != previousMW)
            {
                Log.Info($"Import changed: {previousMW} → {import.ImportMW} MW");
            }

            return true;
        }

        private bool ProcessSetExportPercent(int percent, ref ShadowImportState import, ref ShadowExportState export)
        {
            int clamped = math.clamp(percent, 0, 100);

            // No-op if unchanged — avoid resetting income accumulators on UI re-confirm
            if (clamped == export.ExportPercentage)
                return true;

            // Mutual exclusion: disable import if enabling export
            if (clamped > 0 && import.ImportMW > 0)
            {
                import.ImportMW = 0;
                Log.Info("Auto-disabled import (export enabled)");
            }

            int previous = export.ExportPercentage;
            export.ExportPercentage = clamped;
            export.ExportLastAccumulationTime = 0.0;
            export.ExportIncomeRemainder = 0.0;
            m_LastShadowCapMW = int.MinValue;
            if (clamped == 0)
            {
                export.ExportedMW = 0;
                export.ExportDailyIncome = 0;
            }

            Log.Info($"Export: {previous}% → {clamped}%");
            return true;
        }

        private int CalculateMaxImport()
        {
            int consumption = 0;
            if (m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid))
            {
                consumption = grid.Consumption / 1000;
            }

            float corruptionScore = 0f;
            if (m_CountermeasuresQuery.TryGetSingleton<CountermeasuresCoreFsm>(out var cmCore))
            {
                corruptionScore = cmCore.CorruptionScore;
            }

            return ShadowImportCalculator.CalculateMaxImportMW(consumption, corruptionScore);
        }

        private bool ApplyPostLoadImportProjection(ref ShadowImportState import)
        {
#pragma warning disable CIVIC070 // Post-load projection is explicitly gated until PowerGridSingleton is hydrated.
            if (!m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid))
                return false;
#pragma warning restore CIVIC070

            if (grid.Production == 0 && grid.Demand == 0 && grid.Consumption == 0)
                return false;

            int maxImport = CalculateMaxImport();
            if (import.ImportMW > maxImport)
            {
                Log.Info($"PostLoad: clamped ShadowImportState.ImportMW ({import.ImportMW} -> {maxImport})");
                import.ImportMW = maxImport;
            }

            m_PendingPostLoadImportProjection = false;
            return true;
        }

        // ============================================================================
        // DAY CHANGED EVENT
        // ============================================================================

        private void OnDayChanged(DayChangedEvent evt)
        {
            // M60 FIX: SubscribeRequired doesn't check the act gate — guard manually.
            // Without this, OnDayChanged fires in PreWar and processes daily
            // import/export against live singletons.
            if (RefreshGate() != ActGateState.Active) return;
            if (m_DayDedup.AlreadyProcessed(evt.DayNumber)) return;

            using var _ = PerformanceProfiler.Measure("ShadowTradeDaily.OnDayChanged");
            if (!m_ShadowImportQuery.TryGetSingletonRW<ShadowImportState>(out var importRef))
                return;
            if (!m_ShadowExportQuery.TryGetSingletonRW<ShadowExportState>(out var exportRef))
                return;

            ProcessImportDaily(ref importRef.ValueRW, evt.DayNumber);
            ProcessExportDaily(ref exportRef.ValueRW, evt.DayNumber);
            CheckSuspicion(ref exportRef.ValueRW);
        }

        // ============================================================================
        // IMPORT DAILY LOGIC
        // ============================================================================

        private void ProcessImportDaily(ref ShadowImportState state, int dayNumber)
        {
            // FIX S9-04: Previous frame's ECB deduct failed — disable import immediately
            if (m_ImportDeductFailed)
            {
                m_ImportDeductFailed = false;
                Log.Info("Import: Previous deduction failed. Auto-disabled.");
                state.ImportMW = 0;
                return;
            }

            if (state.ImportIsSanctioned)
            {
                HandleSanctions(ref state);
                return;
            }

            // FIX S4-F02: Block imports during wallet freeze (matches export behavior).
            // FIX S21_RAG2:25: Zero ImportMW so UI shows consistent frozen state (same as export).
            if (m_Wallet.IsFrozen)
            {
                state.ImportMW = 0;
                DecayRisk(ref state);
                Log.Debug("Import: Wallet frozen — import zeroed");
                return;
            }

            if (state.ImportMW > 0)
            {
                // Calculate daily cost
                float price = m_Settings?.ShadowImportPrice ?? FALLBACK_SHADOW_IMPORT_PRICE;
                long baseCost = ShadowImportCalculator.CalculateDailyCostLong(state.ImportMW, price);
                // FIX T3-12: Caller-side sanctions markup (wallet null = no markup)
#pragma warning disable CIVIC005
                float markup = m_Wallet.SanctionsMarkup;
#pragma warning restore CIVIC005
                long cost = SanctionsCostHelper.ApplyMarkup(baseCost, markup);

                // FIX S9-04: Deduct via ECB request (same frame as income requests).
                // ShadowWalletSystem processes Income first, Deduct second — correct order guaranteed.
                // If deduct fails, ShadowDeductFailedEvent → m_ImportDeductFailed → next day disables.
                if (cost > 0)
                {
                    var wallet = m_Wallet;
                    bool walletAvailable = wallet.IsOperational;
                    bool walletFrozen = wallet.IsFrozen;
                    long walletBalance = wallet.Balance;

                    if (!ShadowImportEligibility.CanSetImportMW(
                            importStateAvailable: true,
                            walletAvailable: walletAvailable,
                            walletFrozen: walletFrozen,
                            walletBalance: walletBalance,
                            effectiveCost: cost,
                            out _))
                    {
                        state.ImportMW = 0;
                        DecayRisk(ref state);
                        return;
                    }

                    var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    if (!ShadowEconomyEmitter.TryQueueDeduct(World, ecb, baseCost, "ShadowImport"))
                    {
                        state.ImportMW = 0;
                        DecayRisk(ref state);
                        return;
                    }
                    if (Log.IsDebugEnabled) Log.Debug($"Import: Queued ECB deduct ${cost:N0} for {state.ImportMW} MW");
                }

                // Side effects are optimistic — income processed first makes failure rare.
                // On rare failure (multi-consumer drain): one extra day is negligible,
                // next DayChanged disables via m_ImportDeductFailed flag.
                state.ImportDaysActive++;
                state.ImportWasActiveYesterday = true;

                // Update risk based on days active
                state.ImportDiscoveryRisk = ShadowImportCalculator.GetRiskForDay(state.ImportDaysActive);

                // Add daily corruption
                EventBus?.SafePublish(new CorruptionGainEvent(
                    BalanceConfig.Current.ShadowImport.CorruptionPerDayActive,
                    "ShadowImport"), "ShadowTradeDailySystem");

                // Roll for discovery
                CheckDiscovery(ref state);

                if (Log.IsDebugEnabled) Log.Debug($"Import Day {dayNumber} ({state.ImportDaysActive} active): {state.ImportMW} MW, risk {state.ImportDiscoveryRisk:P0}");
            }
            else
            {
                // Import off - decay risk
                if (state.ImportWasActiveYesterday)
                {
                    state.ImportWasActiveYesterday = false;
                }
                DecayRisk(ref state);
            }
        }

        private void DecayRisk(ref ShadowImportState state)
        {
            if (state.ImportDiscoveryRisk <= 0f)
                return;

            state.ImportDiscoveryRisk = Math.Max(0f, state.ImportDiscoveryRisk - BalanceConfig.Current.ShadowImport.RiskDecayPerDay);

            if (state.ImportDiscoveryRisk <= 0f)
            {
                state.ImportDaysActive = 0;
                Log.Debug("Import: Risk decayed to 0, days reset");
            }
        }

        private void CheckDiscovery(ref ShadowImportState state)
        {
            if (state.ImportDiscoveryRisk <= 0f)
                return;

            var rng = new Random(state.RngState);
            float roll = rng.NextFloat();
            state.RngState = rng.state;
            if (roll < state.ImportDiscoveryRisk)
            {
                TriggerDiscovery(ref state);
            }
        }

        private void TriggerDiscovery(ref ShadowImportState state)
        {
            Log.Info($"Import DISCOVERED! Importing {state.ImportMW} MW illegally. Sanctions applied.");

            var cfg = BalanceConfig.Current.ShadowImport;
            state.ImportIsSanctioned = true;
            state.ImportSanctionDaysRemaining = cfg.SanctionDurationDays;
            state.ImportMW = 0;
            state.ImportDaysActive = 0;
            state.ImportDiscoveryRisk = 0f;

            EventBus?.SafePublish(new ShadowNarrativeEvent(
                ShadowNarrativeEventType.ImportDiscovered,
                SanctionDays: cfg.SanctionDurationDays,
                AttentionIncrease: cfg.AttentionIncrease,
                TrustDecrease: cfg.DonorTrustDecrease
            ), "ShadowTradeDailySystem");
        }

        private void HandleSanctions(ref ShadowImportState state)
        {
            state.ImportSanctionDaysRemaining--;

            if (state.ImportSanctionDaysRemaining <= 0)
            {
                state.ImportIsSanctioned = false;
                state.ImportSanctionDaysRemaining = 0;
                Log.Info("Import: Sanctions LIFTED. Import available again.");

                EventBus?.SafePublish(new ShadowNarrativeEvent(ShadowNarrativeEventType.ImportSanctionsLifted), "ShadowTradeDailySystem");
            }
            else
            {
                if (Log.IsDebugEnabled) Log.Debug($"Import: Sanctioned - {state.ImportSanctionDaysRemaining} days remaining");
            }
        }

        // ============================================================================
        // EXPORT DAILY/CONTINUOUS LOGIC
        // ============================================================================

        private void ProcessExportDaily(ref ShadowExportState state, int dayNumber)
        {
            if (state.ExportedMW > 0)
            {
                EventBus?.SafePublish(
                    new ExportDeficitEvent(state.ExportedMW),
                    "ShadowTradeDailySystem");
            }

            if (state.ExportDailyIncome > 0)
            {
                long walletBalance = m_Wallet.Balance;
                Log.Info($"Export Day {dayNumber}: Exporting {state.ExportedMW}MW at ${state.ExportDailyIncome:N0}/day, Offshore: ${walletBalance:N0}");
            }
        }

        private void CalculateExport(ref ShadowExportState state)
        {
            // Asset Freeze: no exports during investigation
            if (m_Wallet.IsFrozen)
            {
                if (state.ExportPercentage != 0 && Log.IsDebugEnabled)
                    Log.Debug("Export: Wallet frozen - export setting reset");
                state.ExportPercentage = 0;
                state.ExportedMW = 0;
                state.ExportDailyIncome = 0;
                state.ExportLastAccumulationTime = 0.0;
                state.ExportIncomeRemainder = 0.0;
                m_LastShadowCapMW = int.MinValue;
                return;
            }

            // TOCTOU FIX: Use TryGetSingleton instead of IsEmpty + GetSingleton
#pragma warning disable CIVIC070 // Reads per-frame for continuous export calc; PowerGridDataSystem writes main-thread only — no sync point
            if (!m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid))
            {
                // M59 FIX: Zero stale values — AccumulateExportIncome uses ExportDailyIncome
                // and would accumulate income from a previous frame's calculation if we just return.
                state.ExportedMW = 0;
                state.ExportDailyIncome = 0;
                m_LastShadowCapMW = int.MinValue;
                return;
            }
#pragma warning restore CIVIC070

            // Shadow export volume is capped by the capacity headroom minus the legal
            // export flow (one formula, PowerHeadroomMath): the legal and covert
            // channels never sell the same MW. With the legal export cap in place the
            // flow RawBalance sits near zero, so the old flow-based ceiling would
            // choke the covert channel forever. Before the first capacity snapshot is
            // published the flow surplus is the fallback — accepted transient window.
            int shadowCapMW;
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            if (m_PowerCapacitySnapshotReader.TryGetSnapshot(out var capacitySnapshot))
            {
                shadowCapMW = PowerHeadroomMath.ComputeShadowExportCapKW(
                    capacitySnapshot.CityDispatchableMW, grid.Consumption, grid.RawBalance,
                    grid.ExternalPower, ImportCapRuntimeState.CurrentExportCapTotalKW) / 1000;
            }
            else
            {
                shadowCapMW = Math.Max(0, grid.RawBalance / 1000);
            }

            if (shadowCapMW == m_LastShadowCapMW)
                return;
            m_LastShadowCapMW = shadowCapMW;

            state.ExportedMW = (int)Math.Round(shadowCapMW * state.ExportPercentage / 100f);
            state.ExportDailyIncome = (int)Math.Round(state.ExportedMW * BalanceConfig.Current.Economy.ShadowPricePerMwDay);
        }

        private void AccumulateExportIncome(ref ShadowExportState state)
        {
            if (!m_Wallet.IsOperational || m_Wallet.IsFrozen || state.ExportDailyIncome <= 0)
                return;

            m_TimeProvider ??= GameTimeSystem.Instance;
            if (m_TimeProvider == null)
            {
                Log.Error("[ShadowTradeDailySystem] GameTimeSystem unavailable — skipping export accumulation");
                return;
            }
            double currentTime = m_TimeProvider.Current.TotalGameHours * (double)GameRate.SECONDS_PER_HOUR;

            // First update - initialize timer
            if (state.ExportLastAccumulationTime <= 0.0)
            {
                state.ExportLastAccumulationTime = currentTime;
                return;
            }

            double creditedTime = Math.Floor(currentTime);
            double deltaSeconds = creditedTime - state.ExportLastAccumulationTime;
            if (deltaSeconds <= 0)
                return;

            double incomePerSecond = state.ExportDailyIncome / (double)GameRate.SECONDS_PER_DAY;
            double nextRemainder = state.ExportIncomeRemainder;
            long whole = GameRate.AccumulateWithRemainder(incomePerSecond, deltaSeconds, ref nextRemainder);

            if (whole > 0)
            {
                var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                long fromSecond = (long)Math.Floor(state.ExportLastAccumulationTime);
                long toSecond = (long)creditedTime;
                if (ShadowEconomyEmitter.TryQueueIncome(World, ecb, whole, "ShadowExport", $"ShadowExport:{fromSecond}:{toSecond}"))
                {
                    // ACCOUNTING-INVARIANT: timer advances when the range is emitted.
                    // The wallet operation key dedups delivery, not overlapping range issuance.
                    state.ExportIncomeRemainder = nextRemainder;
                    state.ExportLastAccumulationTime = creditedTime;
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
                }

                return;
            }

            state.ExportIncomeRemainder = nextRemainder;
            state.ExportLastAccumulationTime = creditedTime;
        }

        private void OnShadowIncomeApplied(ShadowIncomeAppliedEvent evt)
        {
            // Delivery ack only: AccumulateExportIncome advances the accumulator
            // atomically with successful request emission, so this handler must not
            // move ExportLastAccumulationTime / ExportIncomeRemainder.
            const string operationPrefix = "ShadowExport:";
            if (!evt.OperationKey.StartsWith(operationPrefix, StringComparison.Ordinal))
                return;
            if (evt.Amount <= 0)
                return;
            if (!TryParseShadowExportKey(evt.OperationKey, out long fromSecond, out long toSecond))
                return;
            if (!m_ShadowExportQuery.TryGetSingleton<ShadowExportState>(out var state))
                return;

            if (toSecond > (long)Math.Ceiling(state.ExportLastAccumulationTime) + 1)
                Log.Warn($"ShadowExport ack for future range {fromSecond}:{toSecond} while accumulator is {state.ExportLastAccumulationTime:F0}");
        }

        private static bool TryParseShadowExportKey(string operationKey, out long fromSecond, out long toSecond)
        {
            fromSecond = 0;
            toSecond = 0;
            const string operationPrefix = "ShadowExport:";
            if (!operationKey.StartsWith(operationPrefix, StringComparison.Ordinal))
                return false;

            int separator = operationKey.IndexOf(':', operationPrefix.Length);
            if (separator < 0)
                return false;

            return long.TryParse(operationKey.Substring(operationPrefix.Length, separator - operationPrefix.Length), out fromSecond)
                && long.TryParse(operationKey.Substring(separator + 1), out toSecond);
        }

        /// <summary>
        /// Check if suspicion should rise based on corruption level.
        /// ECS-pure: reads from CountermeasuresCoreFsm singleton.
        /// </summary>
        private void CheckSuspicion(ref ShadowExportState state)
        {
            // Advance RNG unconditionally so seed doesn't stall during non-Idle phases (H1)
            var rng = new Random(state.RngState);
            float roll = rng.NextFloat();
            state.RngState = rng.state;

            if (state.SuspicionCooldown > 0)
            {
                state.SuspicionCooldown--;
                return;
            }

            // No suspicion without active exports — prevents "suspicion from nothing"
            if (state.ExportedMW <= 0)
                return;

            // ECS-pure: read from CountermeasuresCoreFsm singleton
            if (!m_CountermeasuresQuery.TryGetSingleton<CountermeasuresCoreFsm>(out var cmCore))
                return;

            float corruptionLevel = cmCore.CorruptionScore;

            // FIX S21-#10: Only trigger suspicion from Idle phase.
            // Previously allowed Suspicion → SuspicionRising re-fired → cooldown reset loop.
            if (cmCore.CurrentPhase != CountermeasuresPhase.Idle)
                return;

            if (corruptionLevel < SUSPICION_CORRUPTION_THRESHOLD)
                return;

            float chance = (corruptionLevel - SUSPICION_CORRUPTION_THRESHOLD) * 0.001f + 0.01f;
            if (roll < chance)
            {
                EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.SuspicionRising), "ShadowTradeDailySystem");
                state.SuspicionCooldown = BalanceConfig.Current.Countermeasures.SuspicionCooldownDays;
                if (Log.IsDebugEnabled) Log.Debug($"Suspicion triggered at {corruptionLevel:F0}% corruption");
            }
        }

    }
}
