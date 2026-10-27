using System;
using System.Collections.Generic;
using Colossal.Logging;
using CivicSurvival.Core.Utils;
using Game;
using Game.Areas;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.Debug;
using CivicSurvival.Core.Interfaces.Domain.Narrative;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Services.Countermeasures;
using CivicSurvival.Localization;
using CivicSurvival.Core.Interfaces.Domain.Corruption;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.UI;
using Colossal.Serialization.Entities;

using CivicSurvival.Core.Services;
namespace CivicSurvival.Domains.Countermeasures.Systems
{
    /// <summary>
    /// Countermeasures tick logic - ECS-pure implementation.
    /// Updates CountermeasuresCoreFsm + CmInvestigationState + CmPoliceState + CmProtestState singletons each tick.
    /// S6-04 FIX: Sole writer to all countermeasures components (request processing consolidated here).
    ///
    /// Responsibilities:
    /// - Process CountermeasureChoiceRequest entities (player choices)
    /// - Corruption calculation (from CorruptionCalculator)
    /// - Heat system updates
    /// - FSM phase transitions
    /// - Protest detection
    /// - Investigation/Police progress
    /// </summary>
    [SingletonOwner(typeof(CountermeasuresCoreFsm))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
    public partial class CountermeasuresUpdateSystem : ThrottledSystemBase, IPostLoadValidation, IDefaultSerializable, IResettable, IBootDefaultsReset
#if DEBUG
        , ICountermeasuresDebugMutator
#endif
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("Countermeasures");
        private const int JOURNALIST_COUNT_FALLBACK = 5;
        private const int MISSING_CORRUPTION_ERROR_FRAME_THRESHOLD = 60;

        // Dependencies - interface-based: no direct domain dependencies
        private INarrativeReactions m_NarrativeReactions = null!;
        private GameTimeSystem? m_TimeProvider;
        private IDistrictStateReader m_DistrictState = null!;

        // Pure logic helper — recreated in OnCreate, no serialization needed

        // ECB for wallet control requests + request entity cleanup
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        private readonly List<int> m_LiveDistrictScratch = new();

        // Extracted helpers — wired in WireDependencies
        [NonSerialized] private CmWalletOps m_WalletOps = null!;
        [NonSerialized] private CmChoiceProcessor m_ChoiceProcessor = null!;
        [NonSerialized] private CivicDependencyWire m_DependencyWire = null!;
        [NonSerialized] private bool m_ResetComponentsAfterLoad;

        // IJob singleton write (avoids CompleteDependencyBeforeRW sync point)
#pragma warning disable CIVIC269 // Write via IJob, not direct indexer
        private ComponentLookup<CountermeasuresCoreFsm> m_CoreLookup;
        private ComponentLookup<CmInvestigationState> m_InvLookup;
        private ComponentLookup<CmPoliceState> m_PoliceLookup;
        private ComponentLookup<CmProtestState> m_ProtestLookup;
#pragma warning restore CIVIC269
        private EntityQuery m_SingletonQuery;
        private EntityQuery m_SanctionsQuery;

        [NonSerialized] private bool m_LoggedMissingCorruptionSingleton;
#pragma warning disable CIVIC324 // Ephemeral retry/narrative guards; reset by ResetBootDefaultsFields and recomputed from live FSM state.
        [NonSerialized] private int m_FramesWithoutCorruption;
        [NonSerialized] private bool m_ArrestReactionFired;
#pragma warning restore CIVIC324

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Domain-Driven Initialization
            CountermeasuresCoreFsm.EnsureExists(EntityManager);

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_DependencyWire = new CivicDependencyWire(nameof(CountermeasuresUpdateSystem));

            m_CoreLookup = GetComponentLookup<CountermeasuresCoreFsm>(false);
            m_InvLookup = GetComponentLookup<CmInvestigationState>(false);
            m_PoliceLookup = GetComponentLookup<CmPoliceState>(false);
            m_ProtestLookup = GetComponentLookup<CmProtestState>(false);
            m_SingletonQuery = GetEntityQuery(
                ComponentType.ReadWrite<CountermeasuresCoreFsm>(),
                ComponentType.ReadWrite<CmInvestigationState>(),
                ComponentType.ReadWrite<CmPoliceState>(),
                ComponentType.ReadWrite<CmProtestState>());
            m_SanctionsQuery = GetEntityQuery(ComponentType.ReadOnly<CivicSurvival.Core.Components.CrossDomain.DonorSanctionsSingleton>());

            // Player-choice requests are owned by CountermeasureChoiceIntakeSystem
            // (ModificationEnd, pause-safe per Axiom 14) — no request query here.
            // FIX S4-09: Post-load reconciliation (Arrested state vs wallet)
#if DEBUG
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<ICountermeasuresDebugMutator>(this);
#endif

            Log.Info("[CountermeasuresUpdateSystem] Created (sole writer — S6-04)");
        }

        protected override void OnDestroy()
        {
#if DEBUG
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<ICountermeasuresDebugMutator>(this);
#endif
            base.OnDestroy();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            WireDependencies();
        }

        private void WireDependencies()
        {
            m_DependencyWire ??= new CivicDependencyWire(nameof(CountermeasuresUpdateSystem));
            m_DependencyWire.EnsureWired(TryWireDependencies);
        }

        private bool TryWireDependencies()
        {
            if (m_WalletOps != null && m_ChoiceProcessor != null)
                return true;

            if (!ServiceRegistry.IsInitialized) return false;
            m_NarrativeReactions = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullNarrativeReactions.Instance);
            m_TimeProvider = GameTimeSystem.Instance;
            m_DistrictState = ServiceRegistry.Instance.Require<IDistrictStateReader>();
            var walletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            var reputationService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowReputationService.Instance);

            m_WalletOps = new CmWalletOps(walletService, m_GameSimulationEndBarrier);
            m_ChoiceProcessor = new CmChoiceProcessor(
                EventBus, reputationService, m_WalletOps,
#pragma warning disable CIVIC070 // Arrested is a one-shot event; 1-frame singleton lag is irrelevant
                () => SystemAPI.TryGetSingleton<ScenarioSingleton>(out var s) ? s.GameDay : 0,
                () => SystemAPI.TryGetSingleton<CurrentActSingleton>(out var a) ? a.CurrentAct : Act.PreWar);
#pragma warning restore CIVIC070

            // NarrativeReactions optional — null-object yields silent no-ops when Narrative closed
            return true;
        }

        protected override void OnThrottledUpdate()
        {
            // Inv 2 / Axiom 15: OnStartRunning can run before ServiceRegistry is
            // initialized (cross-system activation order); WireDependencies then
            // early-returns unwired and was never retried, leaving m_ChoiceProcessor
            // / m_WalletOps / m_DistrictState null forever → NRE storm on the first
            // countermeasure choice. Retry idempotently each tick (cheap two-null
            // early-return once wired) and skip the tick until wired.
            WireDependencies();
            if (m_DependencyWire == null || !m_DependencyWire.Ready)
                return;

            if (!TryReadCorruptionSingleton(out _))
                return;

            if (!SystemAPI.TryGetSingleton<CountermeasuresCoreFsm>(out var core))
                return;
            if (!SystemAPI.TryGetSingleton<CmInvestigationState>(out var inv))
                return;
            if (!SystemAPI.TryGetSingleton<CmPoliceState>(out var police))
                return;
            if (!SystemAPI.TryGetSingleton<CmProtestState>(out var protest))
                return;

            float prevGameHour = core.GameHour;
            UpdateGameTime(ref core);

            // Player choice requests are drained by CountermeasureChoiceIntakeSystem
            // (ModificationEnd, pause-safe) via DrainChoiceRequestsImmediate — not here.

            // Take snapshot once for all methods that need district state
            var snapshot = m_DistrictState.TakeSnapshot();

            float gameDayFraction = Math.Max(0f, GameRate.DayFractionFromHours(core.GameHour - prevGameHour));

            CalculateCorruption(ref core, in protest, gameDayFraction);
            CountermeasuresRules.UpdateHeat(ref core, gameDayFraction);
            // F-S1-11 FIX: Drain cascading phase transitions at high speed.
            // At 3x speed, corruption can jump across multiple thresholds between
            // checks, causing the FSM to need multiple transitions in one tick.
            var prevPhase = core.CurrentPhase;
            UpdatePhase(ref core, ref inv, ref police);
            int guard = 8;
            while (core.CurrentPhase != prevPhase && guard-- > 0
                && core.CurrentPhase != CountermeasuresPhase.WaitingForInvestigationChoice
                && core.CurrentPhase != CountermeasuresPhase.WaitingForPoliceChoice
                && core.CurrentPhase != CountermeasuresPhase.Suspicion
                && core.CurrentPhase != CountermeasuresPhase.ArticlePublished
                && core.CurrentPhase != CountermeasuresPhase.Arrested)
            {
                prevPhase = core.CurrentPhase;
                UpdatePhase(ref core, ref inv, ref police);
            }
            CheckForProtests(ref core, ref protest, snapshot);

            ScheduleCountermeasuresWrite(in core, in inv, in police, in protest);
        }

        // ============================================================================
        // GAME TIME
        // ============================================================================

        private void UpdateGameTime(ref CountermeasuresCoreFsm core)
        {
            m_TimeProvider ??= GameTimeSystem.Instance;
            if (m_TimeProvider == null) { Log.Error("[CountermeasuresUpdateSystem] TimeProvider unavailable"); return; }
            core.GameHour = m_TimeProvider.Current.TotalGameHours;
        }

        private void ScheduleCountermeasuresWrite(
            in CountermeasuresCoreFsm core,
            in CmInvestigationState inv,
            in CmPoliceState police,
            in CmProtestState protest)
        {
            // Write back via IJob (avoids CompleteDependencyBeforeRW sync point)
            m_CoreLookup.Update(this);
            m_InvLookup.Update(this);
            m_PoliceLookup.Update(this);
            m_ProtestLookup.Update(this);
            if (m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
            {
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre WriteCountermeasuresJob.Schedule singleton={entity} phase={core.CurrentPhase} heat={core.Heat} invProgress={inv.Progress} policeCharges={police.ChargesCount} activeProtests={protest.ActiveProtests}");
                Dependency = new WriteCountermeasuresJob
                {
                    CoreLookup = m_CoreLookup,
                    InvLookup = m_InvLookup,
                    PoliceLookup = m_PoliceLookup,
                    ProtestLookup = m_ProtestLookup,
                    Entity = entity,
                    Core = core,
                    Inv = inv,
                    Police = police,
                    Protest = protest
                }.Schedule(Dependency);
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post WriteCountermeasuresJob.Schedule singleton={entity} phase={core.CurrentPhase} heat={core.Heat} invProgress={inv.Progress} policeCharges={police.ChargesCount} activeProtests={protest.ActiveProtests}");
            }
        }

        // ============================================================================
        // REQUEST PROCESSING
        // ============================================================================

        /// <summary>
        /// Pause-safe single-choice apply, called by CountermeasureChoiceIntakeSystem
        /// (ModificationEnd) per request with its barrier's ECB. Runs outside this
        /// system's OnUpdate, so all component access goes through EntityManager/
        /// EntityQuery — SystemAPI here would use a stale __TypeHandle. Singleton
        /// mutations are written back synchronously (main thread; the GameSimulation
        /// write job of the same frame has already completed by ModificationEnd).
        /// Request iteration/destruction stays in the intake — this system must not
        /// hold a request query (it is not the request consumer).
        /// </summary>
        internal RequestStatus ApplyChoiceImmediate(
            CountermeasureChoiceType choiceType,
            int choiceValue,
            EntityCommandBuffer ecb,
            out string reasonId)
        {
            reasonId = ReasonIds.CountermeasuresInvalidChoice;

            WireDependencies();
            if (m_DependencyWire == null || !m_DependencyWire.Ready)
                return RequestStatus.Failed;

            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var singletonEntity))
                return RequestStatus.Failed;

            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(singletonEntity);
            var inv = EntityManager.GetComponentData<CmInvestigationState>(singletonEntity);
            var police = EntityManager.GetComponentData<CmPoliceState>(singletonEntity);

            // Choices anchor cooldowns to authoritative time, same as the FSM tick.
            UpdateGameTime(ref core);

            RequestStatus status = choiceType switch
            {
                CountermeasureChoiceType.Investigation => m_ChoiceProcessor.ProcessInvestigationChoice(
                    (InvestigationChoice)choiceValue,
                    ref core,
                    ref inv,
                    out reasonId),
                CountermeasureChoiceType.Police => m_ChoiceProcessor.ProcessPoliceChoice(
                    (PoliceChoice)choiceValue, ref core, ref police, ecb, out reasonId)
                        ? RequestStatus.Success
                        : RequestStatus.Failed,
                _ => RequestStatus.Failed
            };

            EntityManager.SetComponentData(singletonEntity, core);
            EntityManager.SetComponentData(singletonEntity, inv);
            EntityManager.SetComponentData(singletonEntity, police);
            return status;
        }


        /// <summary>
        /// FIX S4-09: Post-load reconciliation — if Arrested but wallet not confiscated.
        /// Called once by PostLoadValidationSystem after all Deserialize() completes.
        /// </summary>
#pragma warning disable CIVIC231 // Post-load validation is the act-gated hydration path for this ActIndependent singleton
        public void ValidateAfterLoad()
        {
            CountermeasuresCoreFsm.EnsureExists(EntityManager);
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;

            if (m_ResetComponentsAfterLoad)
            {
                EntityManager.SetComponentData(entity, CountermeasuresCoreFsm.CreateDefault());
                EntityManager.SetComponentData(entity, CmInvestigationState.CreateDefault());
                EntityManager.SetComponentData(entity, CmPoliceState.CreateDefault());
                EntityManager.SetComponentData(entity, CmProtestState.CreateDefault());
                m_ResetComponentsAfterLoad = false;
                return;
            }

            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
            var inv = EntityManager.GetComponentData<CmInvestigationState>(entity);
            var police = EntityManager.GetComponentData<CmPoliceState>(entity);
            if (ClampLoadedTimersToAuthoritativeClock(ref core, ref inv, ref police))
            {
#pragma warning disable CIVIC231 // Post-load validation is the act-gated hydration path for this ActIndependent singleton
                EntityManager.SetComponentData(entity, core);
                EntityManager.SetComponentData(entity, inv);
                EntityManager.SetComponentData(entity, police);
#pragma warning restore CIVIC231
            }

            ReconcileArrestedState(core);
        }
#pragma warning restore CIVIC231

        private bool ClampLoadedTimersToAuthoritativeClock(ref CountermeasuresCoreFsm core, ref CmInvestigationState inv, ref CmPoliceState police)
        {
            m_TimeProvider ??= GameTimeSystem.Instance;
            if (m_TimeProvider == null)
                return false;

            float authoritativeHour = Math.Max(0f, m_TimeProvider.Current.TotalGameHours);
            bool changed = false;

            if (Math.Abs(core.GameHour - authoritativeHour) > 0.001f)
            {
                core.GameHour = authoritativeHour;
                changed = true;
            }

            float invStart = math.clamp(inv.StartHour, 0f, authoritativeHour);
            if (Math.Abs(inv.StartHour - invStart) > 0.001f)
            {
                inv.StartHour = invStart;
                changed = true;
            }

            float policeStart = math.clamp(police.StartHour, 0f, authoritativeHour);
            if (Math.Abs(police.StartHour - policeStart) > 0.001f)
            {
                police.StartHour = policeStart;
                changed = true;
            }

            return changed;
        }

        private void ReconcileArrestedState(CountermeasuresCoreFsm core)
        {
            if (core.CurrentPhase != CountermeasuresPhase.Arrested)
                return;

            if (!SystemAPI.TryGetSingleton<ShadowWalletSingleton>(out var wallet))
                return;

            if ((wallet.FreezeReason & FreezeReason.Confiscated) != 0)
                return; // Already confiscated — no reconciliation needed

            Log.Warn($"[S4-09] Arrested but wallet not confiscated (FreezeReason={wallet.FreezeReason}) — forcing confiscation");
            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            CmWalletOps.UnfreezeViaEcb(ecb);
            CmWalletOps.ConfiscateViaEcb(ecb);
            // ECB confiscation — main-thread write in ValidateAfterLoad, no job handle needed
        }

        // ============================================================================
        // CORRUPTION CALCULATION
        // ============================================================================

        private void CalculateCorruption(ref CountermeasuresCoreFsm core, in CmProtestState protest, float gameDayFraction)
        {
            // ECS-pure: read CorruptionSingleton directly
            if (!TryReadCorruptionSingleton(out var corruptionState))
                return;

            // Calculate target corruption
            core.TargetCorruption = CorruptionCalculator.Calculate(in corruptionState);

            // W6-FIX: S5-01 Sanctions Natural Decay
            if (m_SanctionsQuery.TryGetSingleton<CivicSurvival.Core.Components.CrossDomain.DonorSanctionsSingleton>(out var sanctions) && sanctions.SanctionsActive)
            {
                // Sanctions suppress corruption (officials are afraid to steal when broke)
                // This gives the player a path out of the Death Spiral.
                core.TargetCorruption = Math.Max(0f, core.TargetCorruption - BalanceConfig.Current.Countermeasures.SanctionsCorruptionSuppression);
            }

            // Add protest penalty
            core.TargetCorruption = Math.Min(100f, core.TargetCorruption + CountermeasuresRules.GetCorruptionFromProtests(protest.ActiveProtests));

            // Apply inertia — game-time day fraction
            core.CorruptionScore = CorruptionCalculator.ApplyInertia(
                core.CorruptionScore, core.TargetCorruption, gameDayFraction);
        }

        private bool TryReadCorruptionSingleton(out CorruptionSingleton corruptionState)
        {
            if (SystemAPI.TryGetSingleton<CorruptionSingleton>(out corruptionState))
            {
                if (m_LoggedMissingCorruptionSingleton)
                {
                    Log.Info("[CountermeasuresUpdateSystem] CorruptionSingleton ready");
                    m_LoggedMissingCorruptionSingleton = false;
                }
                m_FramesWithoutCorruption = 0;
                return true;
            }

            if (FeatureRegistry.IsInitialized
                && FeatureRegistry.Instance.IsAvailable(FeatureIds.Corruption))
            {
                m_FramesWithoutCorruption++;
                if (m_FramesWithoutCorruption == MISSING_CORRUPTION_ERROR_FRAME_THRESHOLD)
                {
                    Log.Error("[CountermeasuresUpdateSystem] CorruptionSingleton missing repeatedly despite Corruption feature available - likely producer init failed");
                }
            }

            if (!m_LoggedMissingCorruptionSingleton)
            {
                Log.Warn("[CountermeasuresUpdateSystem] CorruptionSingleton missing - countermeasures update deferred");
                m_LoggedMissingCorruptionSingleton = true;
            }

            return false;
        }

        // ============================================================================
        // FSM PHASE TRANSITIONS
        // ============================================================================

        private void UpdatePhase(ref CountermeasuresCoreFsm core, ref CmInvestigationState inv, ref CmPoliceState police)
        {
            var cfg = BalanceConfig.Current.Countermeasures;
            if (core.CurrentPhase != CountermeasuresPhase.Arrested)
                m_ArrestReactionFired = false;

            switch (core.CurrentPhase)
            {
                case CountermeasuresPhase.Idle:
                    if (CorruptionCalculator.TriggersSuspicion(core.CorruptionScore))
                    {
                        core.CurrentPhase = CountermeasuresPhase.Suspicion;
                        Log.Info($"[Countermeasures] Entering Suspicion (corruption: {core.CorruptionScore:F0}%)");
                        TriggerNarrativeReaction(CharacterArchetype.HonestOfficial, ReactionTriggers.OnCorruptionHigh);
                        TriggerNarrativeReaction(CharacterArchetype.Journalist, ReactionTriggers.OnCorruptionHigh);
                    }
                    break;

                case CountermeasuresPhase.Suspicion:
                    if (core.CorruptionScore < cfg.SuspicionExitThreshold)
                    {
                        core.CurrentPhase = CountermeasuresPhase.Idle;
                        Log.Info("[Countermeasures] Corruption dropped, returning to Idle");
                    }
                    else if (CountermeasuresRules.ShouldStartInvestigation(ref core, ref inv, cfg, ThrottledDeltaSeconds))
                    {
                        StartInvestigation(ref core, ref inv, cfg);
                        core.CurrentPhase = CountermeasuresPhase.Investigation;
                        TriggerNarrativeReaction(CharacterArchetype.Journalist, ReactionTriggers.OnInvestigationStart);
                        TriggerCharacterReaction("Kotleta", ReactionTriggers.OnInvestigationStart);
                    }
                    break;

                case CountermeasuresPhase.Investigation:
                    if (UpdateInvestigationProgress(ref core, ref inv, cfg))
                    {
                        core.CurrentPhase = CountermeasuresPhase.WaitingForInvestigationChoice;
                        TriggerNarrativeReaction(CharacterArchetype.Journalist, ReactionTriggers.OnInvestigationProgress);
                        // Trigger corruption offer tutorial for players who reach investigation via Heat (no ShadowTrade)
                        EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.SuspicionRising), "CountermeasuresUpdateSystem");
                    }
                    break;

                case CountermeasuresPhase.WaitingForInvestigationChoice:
                    // Waiting for player choice (processed in ProcessPendingRequests)
                    break;

                case CountermeasuresPhase.ArticlePublished:
                    if (core.CorruptionScore < cfg.ArticleFadeThreshold)
                    {
                        core.CurrentPhase = CountermeasuresPhase.Suspicion;
                        // R9-H03 FIX: Set cooldown on fade-to-Suspicion path.
                        // Without this, stale NextEventHour allows immediate re-investigation.
                        CountermeasuresRules.ApplyResolutionCooldown(ref core, cfg);
                        Log.Info("[Countermeasures] Corruption reduced, scandal fading");
                    }
                    else if (CountermeasuresRules.ShouldStartPolice(ref core, ref police, cfg, ThrottledDeltaSeconds))
                    {
                        StartPolice(ref core, ref police);
                        core.CurrentPhase = CountermeasuresPhase.PoliceInvestigation;
                        m_WalletOps.Freeze();
                        TriggerNarrativeReaction(CharacterArchetype.HonestOfficial, ReactionTriggers.OnPoliceInvolved);
                        TriggerNarrativeReaction(CharacterArchetype.Journalist, ReactionTriggers.OnPoliceInvolved);
                        TriggerCharacterReaction("Kotleta", ReactionTriggers.OnPoliceInvolved);
                    }
                    break;

                case CountermeasuresPhase.PoliceInvestigation:
                    UpdatePoliceProgress(ref core, ref police, cfg);
                    break;

                case CountermeasuresPhase.WaitingForPoliceChoice:
                    // Waiting for player choice (processed in ProcessPendingRequests)
                    break;

                case CountermeasuresPhase.Arrested:
                    TriggerArrestNarrativeOnce();
                    break;

                default:
                    Log.Warn($"Unhandled {nameof(CountermeasuresPhase)}: {core.CurrentPhase}");
                    break;
            }
        }

        // ============================================================================
        // INVESTIGATION LOGIC
        // ============================================================================

        private void StartInvestigation(ref CountermeasuresCoreFsm core, ref CmInvestigationState inv, CountermeasuresConfig cfg)
        {
            inv.Active = true;
            inv.Progress = 0;
            inv.LastMilestone = 0;
            inv.StartHour = core.GameHour;
            inv.WaitingForChoice = false;

            // Pick random journalist
            var rng = new Unity.Mathematics.Random(inv.RngState);
            int journalistCount = LocalizationManager.GetPositiveInt("JOURNALIST_COUNT", JOURNALIST_COUNT_FALLBACK);
            int journalistIndex = rng.NextInt(journalistCount);
            inv.RngState = rng.state;

            string journalistName = LocalizationManager.Get($"JOURNALIST_NAME_{journalistIndex + 1}") ?? $"Journalist {journalistIndex + 1}";
            inv.Journalist = new Unity.Collections.FixedString64Bytes(journalistName);

            // Calculate fine
            float pointsAboveThreshold = Math.Max(0, core.CorruptionScore - cfg.InvestigationThreshold);
            int fineAmount = cfg.InvestigationBaseFine + (int)Math.Round(pointsAboveThreshold * cfg.InvestigationFinePerPoint);

            EventBus?.SafePublish(new InvestigationStartedEvent(journalistName, fineAmount));
            Log.Info($"[Countermeasures] Investigation started by {journalistName}, fine: ${fineAmount:N0}");
        }

        private bool UpdateInvestigationProgress(ref CountermeasuresCoreFsm core, ref CmInvestigationState inv, CountermeasuresConfig cfg)
        {
            if (!inv.Active || inv.WaitingForChoice) return false;

            inv.Progress = CountermeasuresRules.CalculateInvestigationProgress(in core, in inv, cfg);

            // Report milestones
            ReportInvestigationMilestones(ref inv, cfg);

            // At 75%, require player choice
            const int MILESTONE_CHOICE_REACHED = 100;
            if (inv.Progress >= cfg.InvestigationMilestone75 && inv.LastMilestone < MILESTONE_CHOICE_REACHED)
            {
                inv.WaitingForChoice = true;
                inv.LastMilestone = MILESTONE_CHOICE_REACHED;

                // Calculate bribe cost
                inv.BribeCost = cfg.JournalistBribeBase +
                    (inv.Progress * cfg.JournalistBribePerProgress);

                Log.Info($"[Countermeasures] Investigation waiting for choice (Bribe: ${inv.BribeCost:N0})");
                return true;
            }

            return false;
        }

        private void ReportInvestigationMilestones(ref CmInvestigationState inv, CountermeasuresConfig cfg)
        {
            if (inv.Progress >= cfg.InvestigationMilestone25 && inv.LastMilestone < cfg.InvestigationMilestone25)
            {
                EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.InvestigationProgress, Percent: cfg.InvestigationMilestone25));
                inv.LastMilestone = cfg.InvestigationMilestone25;
                Log.Info("[Countermeasures] Investigation at 25%");
            }
            // F-S1-13 FIX: Use 'if' instead of 'else if' so all crossed milestones
            // fire in one tick when progress jumps at high speed.
            // InvestigationLastMilestone guard prevents duplicate events.
            if (inv.Progress >= cfg.InvestigationMilestone50 && inv.LastMilestone < cfg.InvestigationMilestone50)
            {
                EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.InvestigationProgress, Percent: cfg.InvestigationMilestone50));
                inv.LastMilestone = cfg.InvestigationMilestone50;
                Log.Info("[Countermeasures] Investigation at 50%");
            }
            if (inv.Progress >= cfg.InvestigationMilestone75 && inv.LastMilestone < cfg.InvestigationMilestone75)
            {
                EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.InvestigationProgress, Percent: cfg.InvestigationMilestone75));
                inv.LastMilestone = cfg.InvestigationMilestone75;
                Log.Info("[Countermeasures] Investigation at 75%");
            }
        }

        // ============================================================================
        // POLICE LOGIC
        // ============================================================================

        private void StartPolice(ref CountermeasuresCoreFsm core, ref CmPoliceState police)
        {
            police.Active = true;
            police.StartHour = core.GameHour;
            police.WaitingForChoice = false;
            police.ChargesCount = core.ChargesCount;

            EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.PoliceInvestigation));
            Log.Info("[Countermeasures] Police investigation started!");
        }

        private void UpdatePoliceProgress(ref CountermeasuresCoreFsm core, ref CmPoliceState police, CountermeasuresConfig cfg)
        {
            if (!police.Active || police.WaitingForChoice) return;

            // Check if corruption dropped
            if (core.CorruptionScore < cfg.PoliceDropThreshold)
            {
                EventBus?.SafePublish(new CorruptionNarrativeEvent(CorruptionNarrativeEventType.PoliceInvestigationEnded));
                Log.Info("[Countermeasures] Police closed case - insufficient evidence");

                police.Active = false;
                police.StartHour = 0f;
                police.WaitingForChoice = false;
                core.CurrentPhase = CountermeasuresPhase.Suspicion;
                CountermeasuresRules.ApplyResolutionCooldown(ref core, cfg);
                m_WalletOps.Unfreeze();
                return;
            }

            // Check if investigation time passed
            float hoursSinceStart = core.GameHour - police.StartHour;
            if (hoursSinceStart < cfg.PoliceInvestigationHours) return;

            // Time's up - transition to ultimatum
            police.WaitingForChoice = true;
            core.CurrentPhase = CountermeasuresPhase.WaitingForPoliceChoice;
            Log.Info($"[Countermeasures] Police investigation complete after {hoursSinceStart:F1}h. Ultimatum time!");
        }

        // ============================================================================
        // PROTEST DETECTION (ECS-pure: reads from IDistrictStateReader directly)
        // ============================================================================

        private void CheckForProtests(ref CountermeasuresCoreFsm core, ref CmProtestState protest, DistrictStateSnapshot snapshot)
        {
            var cfg = BalanceConfig.Current.Countermeasures;

            int prevProtests = protest.ActiveProtests;
            CountermeasuresRules.UpdateProtestTimers(ref protest, cfg, ThrottledDeltaSeconds);
            if (Log.IsDebugEnabled && protest.ActiveProtests < prevProtests)
                Log.Debug($"[Countermeasures] Protest dissipated, {protest.ActiveProtests} remaining");

            // Check VIP protection
            bool hasVIPProtection = (snapshot.VIPDistricts != null && snapshot.VIPDistricts.Count > 0) ||
                                    (snapshot.VIPBypass != null && snapshot.VIPBypass.Count > 0);

            if (!hasVIPProtection)
            {
                if (protest.ActiveProtests > 0 || protest.CooldownSeconds > 0f)
                {
                    if (Log.IsDebugEnabled) Log.Debug($"[Countermeasures] VIP removed, clearing {protest.ActiveProtests} protests");
                    protest.ActiveProtests = 0;
                    protest.DecaySeconds = 0f;
                    protest.CooldownSeconds = 0f;
                }
                return;
            }

            // Check active blackouts
            if (!HasActiveBlackouts(snapshot)) return;

            // Cooldown check
            if (protest.CooldownSeconds > 0f) return;

            // Cap check
            if (protest.ActiveProtests >= cfg.ProtestMaxActive) return;

            // Random chance — single RNG stream: RollProtestChance advances once,
            // TriggerProtest continues from same state (H9: outcome-independent advance count)
            var rng = new Unity.Mathematics.Random(protest.RngState);
            float roll = rng.NextFloat();
            float protestChance = CorruptionCalculator.GetProtestChance(core.CorruptionScore);
            bool triggered = roll < 1f - math.pow(math.max(0f, 1f - protestChance), ThrottledDeltaSeconds);
            if (triggered)
                TriggerProtest(ref protest, ref rng, cfg);
            protest.RngState = rng.state;
        }

        private bool HasActiveBlackouts(DistrictStateSnapshot snapshot)
        {
            m_LiveDistrictScratch.Clear();
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<District>>()
                    .WithNone<Temp, Deleted>()
                    .WithEntityAccess())
            {
#pragma warning disable CIVIC097 // Entity.Index is the district id used by ThreadSafeDistrictState.
                m_LiveDistrictScratch.Add(entity.Index);
#pragma warning restore CIVIC097
            }

            return snapshot.AnyActiveBlackoutForNonVip(m_LiveDistrictScratch);
        }

        private void TriggerProtest(ref CmProtestState protest, ref Unity.Mathematics.Random rng, CountermeasuresConfig cfg)
        {
            protest.ActiveProtests++;
            protest.CooldownSeconds = cfg.ProtestCooldownSeconds;

            // FIX S21_RAG1:142: Guard against ProtestParticipantsRange=0 (NextInt(0) throws ArgumentOutOfRangeException)
            int participants = cfg.ProtestParticipantsMin + (cfg.ProtestParticipantsRange > 0 ? rng.NextInt(cfg.ProtestParticipantsRange) : 0);
            string location = "VIP district";

            EventBus?.SafePublish(new CorruptionNarrativeEvent(
                CorruptionNarrativeEventType.ProtestStarted,
                Participants: participants,
                Location: location));

            Log.Info($"[Countermeasures] Protest #{protest.ActiveProtests} started! ({participants} people)");

            // Narrative reactions
            string trigger = protest.ActiveProtests >= 3
                ? ReactionTriggers.OnProtestLarge
                : ReactionTriggers.OnProtestSmall;
            TriggerNarrativeReaction(CharacterArchetype.Journalist, trigger);
            TriggerNarrativeReaction(CharacterArchetype.Citizen, trigger);
        }

        // ============================================================================
        // NARRATIVE HELPERS
        // ============================================================================

        private void TriggerNarrativeReaction(CharacterArchetype archetype, string trigger)
        {
            m_NarrativeReactions.TriggerReaction(archetype, trigger);
        }

        private void TriggerCharacterReaction(string characterName, string trigger)
        {
            m_NarrativeReactions.TriggerCharacterReaction(characterName, trigger);
        }

        private void TriggerArrestNarrativeOnce()
        {
            if (m_ArrestReactionFired)
                return;

            TriggerNarrativeReaction(CharacterArchetype.HonestOfficial, ReactionTriggers.OnArrest);
            TriggerNarrativeReaction(CharacterArchetype.Journalist, ReactionTriggers.OnArrest);
            TriggerCharacterReaction("Kotleta", ReactionTriggers.OnArrest);
            m_ArrestReactionFired = true;
        }

        // ============================================================================
        // SERIALIZATION (IDefaultSerializable — handles all 4 components on singleton entity)
        // ============================================================================

#pragma warning disable CIVIC322 // ResetState() writes all 4 components via EntityManager.SetComponentData
        public void SetDefaults(Context context) => ResetState();
#pragma warning restore CIVIC322

        public void ResetState()
        {
            ResetBootDefaultsFields();
            m_ResetComponentsAfterLoad = false;
            CountermeasuresCoreFsm.EnsureExists(EntityManager);
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;

            EntityManager.SetComponentData(entity, CountermeasuresCoreFsm.CreateDefault());
            EntityManager.SetComponentData(entity, CmInvestigationState.CreateDefault());
            EntityManager.SetComponentData(entity, CmPoliceState.CreateDefault());
            EntityManager.SetComponentData(entity, CmProtestState.CreateDefault());
        }

        public void ResetToBootDefaults(ResetReason reason)
        {
            ResetBootDefaultsFields();
            m_ResetComponentsAfterLoad = true;
        }

        private void ResetBootDefaultsFields()
        {
            m_LiveDistrictScratch.Clear();
            m_LoggedMissingCorruptionSingleton = false;
            m_FramesWithoutCorruption = 0;
            m_ArrestReactionFired = false;
            m_DependencyWire?.Reset();
            m_WalletOps = null!;
            m_ChoiceProcessor = null!;
        }

#if DEBUG
        public void DebugSetCorruption(float value, string source)
        {
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;
            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
            core.CorruptionScore = math.clamp(value, 0f, 100f);
            core.TargetCorruption = core.CorruptionScore;
            EntityManager.SetComponentData(entity, core);
            Log.Info($"[DEBUG] {source}: corruption set to {core.CorruptionScore:F1}");
        }

        public void DebugSetHeat(float value, string source)
        {
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;
            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
            core.Heat = math.clamp(value, 0f, 100f);
            EntityManager.SetComponentData(entity, core);
            Log.Info($"[DEBUG] {source}: heat set to {core.Heat:F1}");
        }

        public void DebugResetCountermeasures(string source)
        {
            ResetState();
            Log.Info($"[DEBUG] {source}: countermeasures reset");
        }

        // Both force-modal helpers reuse the natural transition code (StartInvestigation/
        // StartPolice + the same field writes UpdatePhase performs), so vendor/journalist
        // seeding, bribe pricing and wallet freeze match organic gameplay.
        public void DebugForceInvestigationChoice(string source)
        {
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;

            var cfg = BalanceConfig.Current.Countermeasures;
            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
            var inv = EntityManager.GetComponentData<CmInvestigationState>(entity);

            if (!inv.WaitingForChoice && core.CurrentPhase != CountermeasuresPhase.Investigation)
                StartInvestigation(ref core, ref inv, cfg);

            inv.WaitingForChoice = true;
            inv.BribeCost = cfg.JournalistBribeBase + (inv.Progress * cfg.JournalistBribePerProgress);
            core.CurrentPhase = CountermeasuresPhase.WaitingForInvestigationChoice;

            EntityManager.SetComponentData(entity, core);
            EntityManager.SetComponentData(entity, inv);
            Log.Info($"[DEBUG] {source}: forced WaitingForInvestigationChoice (Bribe: ${inv.BribeCost:N0})");
        }

        public void DebugForcePoliceChoice(string source)
        {
            if (!m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                return;

            WireDependencies();
            var core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
            var police = EntityManager.GetComponentData<CmPoliceState>(entity);

            StartPolice(ref core, ref police);
            if (m_WalletOps != null)
                m_WalletOps.Freeze();
            police.WaitingForChoice = true;
            core.CurrentPhase = CountermeasuresPhase.WaitingForPoliceChoice;

            EntityManager.SetComponentData(entity, core);
            EntityManager.SetComponentData(entity, police);
            Log.Info($"[DEBUG] {source}: forced WaitingForPoliceChoice");
        }
#endif

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {

                // Singleton always exists (EnsureExists in OnCreate). If missing, write defaults.
                CountermeasuresCoreFsm core;
                CmInvestigationState inv;
                CmPoliceState police;
                CmProtestState protest;

                if (m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                {
                    core = EntityManager.GetComponentData<CountermeasuresCoreFsm>(entity);
                    inv = EntityManager.GetComponentData<CmInvestigationState>(entity);
                    police = EntityManager.GetComponentData<CmPoliceState>(entity);
                    protest = EntityManager.GetComponentData<CmProtestState>(entity);
                }
                else
                {
                    core = CountermeasuresCoreFsm.CreateDefault();
                    inv = CmInvestigationState.CreateDefault();
                    police = CmPoliceState.CreateDefault();
                    protest = CmProtestState.CreateDefault();
                }

                CountermeasuresSerializer.WriteAll(writer, in core, in inv, in police, in protest);

            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(CountermeasuresCoreFsm), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(CountermeasuresCoreFsm)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                CountermeasuresSerializer.ReadAll(reader, out var core, out var inv, out var police, out var protest);

                // Ensure singleton exists — old entity destroyed on load, OnCreate won't re-fire
                CountermeasuresCoreFsm.EnsureExists(EntityManager);

                // Write all 4 components to singleton entity
                if (m_SingletonQuery.TryGetSingletonEntity<CountermeasuresCoreFsm>(out var entity))
                {
                    EntityManager.SetComponentData(entity, core);
                    EntityManager.SetComponentData(entity, inv);
                    EntityManager.SetComponentData(entity, police);
                    EntityManager.SetComponentData(entity, protest);
                }

                Log.Info($"Deserialized v{version}: Phase={core.CurrentPhase}, Corruption={core.CorruptionScore:F0}%, Heat={core.Heat:F0}");
            }
            catch (System.Exception ex)
            {
                Log.Error($"Deserialize {nameof(CountermeasuresCoreFsm)} failed: {ex}");
                ResetToBootDefaults(ResetReason.DeserializeFailed);
            }
            finally
            {
                SerializationGuard.EndBlock(reader, block);
            }
        }

        // ============================================================================
        // WRITE JOB
        // ============================================================================

        // CIVIC-INVARIANT: WriteCountermeasuresJob is the normal-tick writer for the
        // four countermeasures singleton components. ResetState(), Deserialize(), and
        // ValidateAfterLoad() may use EntityManager.SetComponentData only as one-shot
        // runtime/load restoration paths.

        #if ENABLE_BURST
        [BurstCompile]
        #endif
        private struct WriteCountermeasuresJob : IJob
        {
            public ComponentLookup<CountermeasuresCoreFsm> CoreLookup;
            public ComponentLookup<CmInvestigationState> InvLookup;
            public ComponentLookup<CmPoliceState> PoliceLookup;
            public ComponentLookup<CmProtestState> ProtestLookup;
            public Entity Entity;
            public CountermeasuresCoreFsm Core;
            public CmInvestigationState Inv;
            public CmPoliceState Police;
            public CmProtestState Protest;

            public void Execute()
            {
                if (CoreLookup.HasComponent(Entity))
                {
                    CoreLookup[Entity] = Core;
                    InvLookup[Entity] = Inv;
                    PoliceLookup[Entity] = Police;
                    ProtestLookup[Entity] = Protest;
                }
            }
        }
    }
}
