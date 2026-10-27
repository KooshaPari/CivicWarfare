using Game;
using Game.Common;
using CivicSurvival.Core.Features.CrossDomain.ThreatsAirDefense;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Config;
using System.Collections.Generic;
using System.Threading;
using CivicSurvival.Core.Attributes;
namespace CivicSurvival.Domains.Waves.Systems
{
    /// <summary>
    /// Wave executor - executes phase transitions and manages wave state.
    ///
    /// Responsibilities:
    /// - Single Writer for WaveStateSingleton
    /// - Handle phase transition side effects (sirens, debriefing, cleanup)
    /// - Publish PhaseTransitionEvent (ThreatNarrativeEvent)
    /// - Spawn request publishing
    /// - Attack в†’ Recovery transition (when threats cleared)
    ///
    /// Does NOT:
    /// - Decide WHEN to attack (WaveScheduler does)
    /// - Calculate timing or wave parameters
    ///
    /// Pattern: "Writer Notifies" - Executor writes BEFORE publishing events.
    /// </summary>
    // S004: WaveExecutor must run AFTER the dedicated shot stats flush system so that
    // ballistic shots fired this frame are reflected in DebriefingShotStats before debrief.
    [SingletonOwner(typeof(WaveStateSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
    public partial class WaveExecutor : CivicSystemBase, IResettable, IPostLoadValidation, ICivicSingletonOwner<WaveStateSingleton>
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // ECB command counter (encapsulated to avoid CA2211)
        // FIX S14_CODE1:74: Use Interlocked for both read and reset (was mixing atomic/non-atomic)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);

        private const float RECOVERY_PHASE_DURATION = 30f;

        // Minimum time in Attack before allowing Recovery transition.
        // Prevents race: ThreatsSpawned set via event, but ECB hasn't played back yet в†’ activeThreats=0 в†’ premature Recovery.
        private const float MIN_ATTACK_DURATION_BEFORE_RECOVERY = 2f;

        // W6-H3b: If zero threats spawned, recover after this timeout instead of waiting MaxAttackDuration (30min)
        private const float ZERO_SPAWN_RECOVERY_TIMEOUT = 5f;

        // Same guard as the kinetic phase: the raid entities are created through an ECB, so a
        // zero count on the creation frame must not end the beat before it began.
        private const float MIN_PSY_DURATION_BEFORE_RECOVERY = 2f;

        private static readonly LogContext Log = new("WaveExecutor");

        [System.NonSerialized] private CivicSingletonHandle<WaveStateSingleton> m_WaveState;
        [System.NonSerialized] private CivicSingletonHandle<DebriefingWaveStats> m_Debriefing;
        [System.NonSerialized] private bool m_Initialized; // Not serialized: re-initialized in OnStartRunning
        [System.NonSerialized] private List<ScheduleWaveCommand>? m_PendingScheduleCommands;

        // Pending spawn update (race condition fix)
        private int m_PendingThreatsSpawned = -1;
        [System.NonSerialized] private double m_LastOvertimeLogTime = double.NegativeInfinity;

        // Queries
        private EntityQuery m_ActiveThreatQuery;
        private EntityQuery m_ActivePsyRaidQuery;
        private EntityQuery m_LoadResumeStateQuery;
        private EntityQuery m_InterceptStatsQuery;
        private EntityQuery m_ThreatOutcomeStatsQuery;
        private EntityQuery m_WaveStateQuery;
        private EntityQuery m_DebriefingQuery;

        // System references
        private InterceptProcessingSystem m_InterceptProcessor = null!;
        [System.NonSerialized] private CivicDependencyWire m_DependencyWire = null!;

        // Throttled system reference for ForceNextUpdate on phase transitions (same domain only)
        private ThreatTargetSystem? m_ThreatTargetSystem;

        // Event facade
        private WaveEventPublisher m_Events;

        // ComponentLookups for split debriefing components
        private ComponentLookup<DebriefingWaveStats> m_WaveStatsLookup;
        private ComponentLookup<DebriefingShotStats> m_ShotStatsLookup;
        private ComponentLookup<DebriefingDamageStats> m_DamageStatsLookup;
        private ComponentLookup<DebriefingInfraStats> m_InfraStatsLookup;
        private ComponentLookup<ShahedCombatState> m_ShahedCombatStateLookup;
        private ComponentLookup<BallisticInterceptState> m_BallisticInterceptStateLookup;

        // Serialization state
        private ExecutorSaveState? m_SavedState;
        private bool m_HasSavedState;
        [System.NonSerialized] private bool m_ResetWaveStateAfterLoad;
        [System.NonSerialized] private bool m_DiscardRestoredThreatsAfterLoad;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Queries
#pragma warning disable CIVIC340 // Threat live-count query intentionally uses absent-or-disabled PendingDestruction semantics.
            m_ActiveThreatQuery = GetEntityQuery(
                ComponentType.ReadOnly<ActiveThreat>(),
                ComponentType.Exclude<PendingDestruction>(),
                ComponentType.Exclude<Deleted>());
#pragma warning restore CIVIC340
            // The info-war beat ends by its CONTENT, exactly like the kinetic strike above: the
            // raid entities are the phase, not a stopwatch running beside it. (PsyOpsAttack is a
            // Core component, so this crosses no domain boundary.)
            m_ActivePsyRaidQuery = GetEntityQuery(ComponentType.ReadOnly<PsyOpsAttack>());
            m_LoadResumeStateQuery = GetEntityQuery(ComponentType.ReadWrite<ThreatLoadResumeState>());
            m_InterceptStatsQuery = GetEntityQuery(ComponentType.ReadOnly<InterceptStatsSingleton>());
            m_ThreatOutcomeStatsQuery = GetEntityQuery(ComponentType.ReadOnly<ThreatOutcomeStatsSingleton>());
            m_WaveStateQuery = GetEntityQuery(ComponentType.ReadWrite<WaveStateSingleton>());

            m_DependencyWire = new CivicDependencyWire(nameof(WaveExecutor));

            // System references — InterceptProcessingSystem lives in ThreatsAirDefense
            // feature (priority 2511); resolved in OnStartRunning since this domain
            // (priority 2500) registers earlier. Null if ThreatsAirDefense closed.
            m_ThreatTargetSystem = World.GetExistingSystemManaged<ThreatTargetSystem>();

            m_Events = new WaveEventPublisher(EventBus);

            m_WaveState = CreateSingletonHandle<WaveStateSingleton>(m_WaveStateQuery);
            RestoreWaveStateSingleton();

            // R9-M16 FIX: Reuse serialized entity on load to avoid orphan duplicate
            m_DebriefingQuery = GetEntityQuery(ComponentType.ReadWrite<DebriefingWaveStats>());
            m_Debriefing = CreateSingletonHandle<DebriefingWaveStats>(m_DebriefingQuery);
            RestoreDebriefingEntity();

            m_WaveStatsLookup = GetComponentLookup<DebriefingWaveStats>(false);
            m_ShotStatsLookup = GetComponentLookup<DebriefingShotStats>(false);
            m_DamageStatsLookup = GetComponentLookup<DebriefingDamageStats>(false);
            m_InfraStatsLookup = GetComponentLookup<DebriefingInfraStats>(false);
            m_ShahedCombatStateLookup = GetComponentLookup<ShahedCombatState>(false);
            m_BallisticInterceptStateLookup = GetComponentLookup<BallisticInterceptState>(true);

            // Events
            SubscribeRequired<ScheduleWaveCommand>(OnScheduleWaveCommand);
            SubscribeRequired<ThreatsSpawnedEvent>(OnThreatsSpawned);
            SubscribeRequired<WaveLaunchWindowWaitEvent>(OnWaveLaunchWindowWait);
            SubscribeRequired<WaveForecastEvent>(OnWaveForecast);
#if DEBUG
            SubscribeRequired<DebugSkipPhaseCommand>(OnDebugSkipPhaseCommand);
#endif

            Log.Info(" Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            RestoreWaveStateSingleton();
            RestoreDebriefingEntity();
            ReResolveRuntimeRefs();
        }

        private void OnScheduleWaveCommand(ScheduleWaveCommand cmd)
        {
            if (!m_Initialized)
            {
                StageScheduleWaveCommand(cmd);
                return;
            }

            ExecuteScheduleWaveCommand(cmd);
        }

        private void ExecuteScheduleWaveCommand(ScheduleWaveCommand cmd)
        {
            Log.Info($" ScheduleWaveCommand: {cmd.TargetPhase}, Wave #{cmd.WaveNumber}");

            switch (cmd.TargetPhase)
            {
                case GamePhase.Calm:
                    ExecuteCalmTransition(cmd);
                    break;

                case GamePhase.Alert:
                    ExecuteAlertTransition(cmd);
                    break;

                case GamePhase.Attack:
                    ExecuteAttackTransition(cmd);
                    break;

                case GamePhase.Recovery:
                    // Recovery is triggered internally, not by command
                    Log.Warn(" Recovery should not be scheduled via command");
                    break;

                case GamePhase.PsyAttack:
                    // PsyAttack is entered internally from the Attack tick, not by command
                    Log.Warn(" PsyAttack should not be scheduled via command");
                    break;

                default:
                    Log.Warn($"Unhandled {nameof(GamePhase)}: {cmd.TargetPhase}");
                    break;
            }
        }

        private void StageScheduleWaveCommand(ScheduleWaveCommand cmd)
        {
            m_PendingScheduleCommands ??= new List<ScheduleWaveCommand>(2);
            m_PendingScheduleCommands.Add(cmd);

            Log.Info($" ScheduleWaveCommand staged until initialization: {cmd.TargetPhase}, Wave #{cmd.WaveNumber}");
        }

        private void FlushPendingScheduleWaveCommands()
        {
            if (!m_Initialized || m_PendingScheduleCommands == null || m_PendingScheduleCommands.Count == 0)
                return;

            var pending = m_PendingScheduleCommands;
            m_PendingScheduleCommands = null;

            for (int i = 0; i < pending.Count; i++)
            {
                var cmd = pending[i];
                Log.Info($" Accepting staged ScheduleWaveCommand: {cmd.TargetPhase}, Wave #{cmd.WaveNumber}");
                ExecuteScheduleWaveCommand(cmd);
            }

            pending.Clear();
        }

        private void OnThreatsSpawned(ThreatsSpawnedEvent evt)
        {
            int totalSpawned = evt.ShahedCount + evt.BallisticCount;
            m_PendingThreatsSpawned = totalSpawned;
            Log.Info($" ThreatsSpawnedEvent: {totalSpawned} threats (wave #{evt.WaveNumber}) -> pending");
        }

        /// <summary>
        /// Single writer for the launch-window wait fields. WaveScheduler derives the wait
        /// state and notifies here; Executor never sets these fields from phase transitions
        /// (avoids a second writer and frame-order races).
        /// </summary>
        private void OnWaveLaunchWindowWait(WaveLaunchWindowWaitEvent evt)
        {
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            singletonRef.ValueRW.WaitingForLaunchWindow = evt.Waiting;
        }

        /// <summary>
        /// Single writer for the next-wave forecast fields. WaveScheduler decides the next
        /// wave at Recovery/Calm entry and notifies here; the forecast is cleared on the
        /// Alert transition when it becomes the current wave. Intel reads these fields
        /// during Calm/Recovery instead of the (stale) CurrentWaveType of the finished wave.
        /// </summary>
        private void OnWaveForecast(WaveForecastEvent evt)
        {
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;
            singleton.NextWaveForecastReady = evt.HasForecast;
            singleton.NextWaveType = evt.HasForecast ? evt.NextWaveType : WaveType.Harassment;
            singleton.NextWaveNumber = evt.HasForecast ? evt.NextWaveNumber : 0;
            singleton.NextWaveThreatsForecast = evt.HasForecast ? evt.ThreatsForecast : 0;
        }

#if DEBUG
        private void OnDebugSkipPhaseCommand(DebugSkipPhaseCommand cmd)
        {
            DebugSkipPhase();
        }
#endif

        protected override void OnDestroy()
        {
            UnsubscribeSafe<ScheduleWaveCommand>(OnScheduleWaveCommand);
            UnsubscribeSafe<ThreatsSpawnedEvent>(OnThreatsSpawned);
            UnsubscribeSafe<WaveLaunchWindowWaitEvent>(OnWaveLaunchWindowWait);
            UnsubscribeSafe<WaveForecastEvent>(OnWaveForecast);
#if DEBUG
            UnsubscribeSafe<DebugSkipPhaseCommand>(OnDebugSkipPhaseCommand);
#endif

            if (!m_WaveStateQuery.IsEmpty)
                EntityManager.DestroyEntity(m_WaveStateQuery);
            m_WaveState.Invalidate();

            var debriefingEntity = m_Debriefing.Entity;
            if (debriefingEntity != Entity.Null && EntityManager.Exists(debriefingEntity))
                EntityManager.DestroyEntity(debriefingEntity);
            m_Debriefing.Invalidate();
            m_PendingScheduleCommands = null;

            base.OnDestroy();
        }

        protected override void OnUpdateImpl()
        {
            if (!m_Initialized)
            {
                ApplyInitialState();
                FlushPendingScheduleWaveCommands();
                return;
            }

            // C1: a save-interrupted Attack is resumed (or fallback-finalized when 0 live threats
            // were restored) inside ApplyInitialState on first init — no per-frame finalize hook.

            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;

            // Tick timer
            float deltaTime = SystemAPI.Time.DeltaTime;
#pragma warning disable CIVIC056 // Resets on phase transition in AdvancePhase()
            singleton.TimeInPhase += deltaTime;
#pragma warning restore CIVIC056
            singleton.SecondsUntilPhaseChange = math.max(0, singleton.PhaseEndTime - singleton.TimeInPhase);

            // Apply pending spawn
            if (m_PendingThreatsSpawned >= 0)
            {
                singleton.ThreatsSpawned = m_PendingThreatsSpawned;
                m_PendingThreatsSpawned = -1;
            }

            // Attack phase: check for completion
            if (singleton.CurrentPhase == GamePhase.Attack)
            {
                int activeThreats = GetActiveThreatsCount();

                // NOTE: ThreatsRemaining is read from ThreatStatsSingleton (Single Writer pattern)
                // No need to copy here - UI reads directly from ThreatStatsSingleton.TotalActiveCount

                // Transition to Recovery when all threats resolved
                // Guards: ThreatsSpawned > 0 (spawn happened) + MIN time (ECB entities visible in queries)
                // W6-H3b: Also recover if zero threats spawned (spawn failure) after timeout
                bool spawnConfirmed = singleton.ThreatsSpawned > 0;
                bool zeroSpawnTimeout = !spawnConfirmed && singleton.TimeInPhase > ZERO_SPAWN_RECOVERY_TIMEOUT;
                float maxAttackDuration = BalanceConfig.Current.Waves.MaxAttackDuration;
                if (activeThreats <= 0 && (spawnConfirmed || zeroSpawnTimeout)
                    && singleton.TimeInPhase > MIN_ATTACK_DURATION_BEFORE_RECOVERY)
                {
                    // Kinetic strike cleared → book the air-defence debriefing NOW (immediate
                    // intercept summary). Then, if this wave carries a PsyOps raid, open the
                    // info-war PsyAttack beat (Recovery deferred until it elapses); otherwise
                    // wind straight down to Recovery.
                    FinalizeKineticWave(ref singleton);
                    if (ShouldEnterPsyAttack(singleton.WaveNumber))
                        ExecutePsyAttackTransition(ref singleton);
                    else
                        EnterRecovery(ref singleton);
                }
                else if (singleton.TimeInPhase > maxAttackDuration)
                {
                    double now = SystemAPI.Time.ElapsedTime;
                    if (now - m_LastOvertimeLogTime >= maxAttackDuration)
                    {
                        m_LastOvertimeLogTime = now;
                        Log.Warn($"Attack overtime: {activeThreats} active threats remain after MaxAttackDuration; waiting for movement watchdog/crash pipeline");
                    }
                }
            }
            // PsyAttack phase: ends with its RAIDS, not on a stopwatch — same rule as the kinetic
            // strike above. The old timer counted simulation seconds while the raids themselves fly
            // in GAME HOURS (2h to land): at 3x speed the six-minute beat expired in ~77 seconds of
            // real time and the phase closed four minutes BEFORE its own raid landed, so the city's
            // "PSY ATTACK" status went quiet while the attack was still inbound. PhaseEndTime stays
            // as a failsafe ceiling in case a raid entity never appears (or gets stranded).
            else if (singleton.CurrentPhase == GamePhase.PsyAttack)
            {
                int inFlightRaids = GetInFlightPsyRaidsCount();
                bool minElapsed = singleton.TimeInPhase > MIN_PSY_DURATION_BEFORE_RECOVERY;
                bool stranded = singleton.TimeInPhase > singleton.PhaseEndTime && inFlightRaids > 0;

                if (stranded)
                {
                    Log.Warn($"PsyAttack overtime: {inFlightRaids} raid(s) still in flight past the failsafe ceiling — winding down anyway");
                    EnterRecovery(ref singleton);
                }
                else if (minElapsed && inFlightRaids <= 0)
                {
                    EnterRecovery(ref singleton);
                }
            }
            // Recovery phase: self-timeout failsafe if WaveScheduler fails to send Calm command
            else if (singleton.CurrentPhase == GamePhase.Recovery
                     && singleton.TimeInPhase > RECOVERY_PHASE_DURATION * 2f)
            {
                Log.Warn("Recovery timeout self-trigger: WaveScheduler did not send Calm command");
                EventBus?.SafePublish(new ScheduleWaveCommand(
                    TargetPhase: GamePhase.Calm,
                    WaveNumber: singleton.WaveNumber,
                    PhaseDuration: RECOVERY_PHASE_DURATION,
                    WaveRole: singleton.WaveRole), "WaveExecutor");
            }
        }

        // ============================================================================
        // Phase Transition Execution
        // ============================================================================

        private void ExecuteCalmTransition(ScheduleWaveCommand cmd)
        {
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;

            // Write BEFORE publishing event (Writer Notifies pattern)
            singleton.CurrentPhase = GamePhase.Calm;
            singleton.TimeInPhase = 0f;
            singleton.PhaseEndTime = cmd.PhaseDuration;
            singleton.WaveNumber = cmd.WaveNumber;
            singleton.WaveRole = cmd.WaveRole;
            singleton.ThreatsSpawned = 0;
            singleton.ThreatsExpected = 0;
            // NOTE: ThreatsRemaining is read from ThreatStatsSingleton directly
            singleton.IsUnderAttack = false;
            singleton.SecondsUntilPhaseChange = cmd.PhaseDuration;

            m_PendingThreatsSpawned = -1;
            SetSirensActive(false);

            // Publish AFTER write
            m_Events.NotifyPhaseChanged(GamePhase.Calm, cmd.WaveNumber);

            if (cmd.IsDoubleTap)
                Log.Info($" Calm started: DOUBLE TAP! Next wave in {cmd.PhaseDuration / 60f:F1}min");
            else
                Log.Info($" Calm started: {cmd.PhaseDuration / 60f:F1}min");
        }

        private void ExecuteAlertTransition(ScheduleWaveCommand cmd)
        {
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;

            // Write BEFORE publishing event
            singleton.CurrentPhase = GamePhase.Alert;
            singleton.TimeInPhase = 0f;
            singleton.PhaseEndTime = cmd.PhaseDuration;
            singleton.WaveNumber = cmd.WaveNumber;
            singleton.WaveRole = cmd.WaveRole;
            singleton.ThreatsExpected = cmd.ThreatsExpected;
            // NOTE: ThreatsRemaining is read from ThreatStatsSingleton directly
            singleton.CurrentWaveType = cmd.WaveType;
            singleton.IsUnderAttack = false;
            singleton.SecondsUntilPhaseChange = cmd.PhaseDuration;
            singleton.ScenarioStarted = true;
            // Forecast consumed: from Alert on the CURRENT wave fields describe this wave.
            singleton.NextWaveForecastReady = false;
            m_LastOvertimeLogTime = double.NegativeInfinity;

            // Reset intercept stats
            m_InterceptProcessor?.ResetForNewWave(cmd.WaveNumber);
            ResetLeftoverThreatCombatState();

            // Reset wave/damage/infra stats immediately. Shot stats deferred to Attack transition
            // to avoid race with AirDefenseOrchestrator's per-frame shot flush (cross-domain ordering).
            var debriefingEntity = ResolveDebriefingEntity();
            m_WaveStatsLookup.Update(this);
            m_DamageStatsLookup.Update(this);
            m_InfraStatsLookup.Update(this);
            if (m_WaveStatsLookup.HasComponent(debriefingEntity))
            {
                var waveStats = new DebriefingWaveStats();
                waveStats.Reset(cmd.WaveNumber);
                m_WaveStatsLookup[debriefingEntity] = waveStats;
                m_DamageStatsLookup[debriefingEntity] = new DebriefingDamageStats();
                m_InfraStatsLookup[debriefingEntity] = new DebriefingInfraStats();
            }

            SetSirensActive(true);

            // Publish AFTER write
            m_Events.NotifyPhaseChanged(GamePhase.Alert, cmd.WaveNumber);
            m_Events.NotifyThreatAlert(cmd.WaveNumber, cmd.ThreatsExpected);
            m_Events.NotifyWaveStarting(cmd.WaveNumber, cmd.ThreatsExpected, cmd.WaveRole);

            string waveTypeStr = cmd.WaveType == WaveType.MassiveStrike ? "MASSIVE STRIKE" : "Harassment";
            Log.Info($" Alert started: Wave #{cmd.WaveNumber} [{waveTypeStr}], expecting {cmd.ThreatsExpected} threats");
        }

        private void ExecuteAttackTransition(ScheduleWaveCommand cmd)
        {
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;

            // FIX S17-03: Reset pending spawn count on Attack transition.
            m_PendingThreatsSpawned = -1;

            // Reset shot stats here (not in Alert) — ADO needs one extra frame to flush
            // residual shots from the previous wave before the reset clears the counter.
            var debriefingEntity = ResolveDebriefingEntity();
            m_ShotStatsLookup.Update(this);
            if (m_ShotStatsLookup.HasComponent(debriefingEntity))
                m_ShotStatsLookup[debriefingEntity] = new DebriefingShotStats();
            CivicSurvival.Domains.AirDefense.Logic.AirDefenseShotCounter.Reset();

            // Write BEFORE publishing event — use cmd metadata (self-contained command)
            singleton.CurrentPhase = GamePhase.Attack;
            singleton.TimeInPhase = 0f;
            singleton.PhaseEndTime = cmd.PhaseDuration;
            singleton.WaveNumber = cmd.WaveNumber;
            singleton.WaveRole = cmd.WaveRole;
            singleton.CurrentWaveType = cmd.WaveType;
            singleton.ThreatsExpected = cmd.ThreatsExpected;
            singleton.IsUnderAttack = true;
            singleton.SecondsUntilPhaseChange = cmd.PhaseDuration;
            m_LastOvertimeLogTime = double.NegativeInfinity;

            // Trigger threat spawning
            m_Events.NotifySpawnRequest(cmd.ThreatsExpected, cmd.WaveNumber, cmd.WaveType, cmd.BallisticOverride, cmd.WaveRole);

            // FIX A1a: Force immediate threat stats update on attack start
            m_ThreatTargetSystem?.ForceNextUpdate();

            // Publish AFTER write
            m_Events.NotifyPhaseChanged(GamePhase.Attack, singleton.WaveNumber);

            Log.Info($" Attack started: {singleton.ThreatsExpected} threats requested");
        }

        /// <summary>True when the just-cleared wave carries a PsyOps raid, so the cycle
        /// inserts a PsyAttack beat before Recovery. Pure Core predicate over act +
        /// wave number (no Cognitive import — Axiom 5).</summary>
        private bool ShouldEnterPsyAttack(int waveNumber)
        {
            Act act = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var ca) ? ca.CurrentAct : Act.PreWar;
            return PsyOpsArming.WillFire(act, waveNumber);
        }

        /// <summary>Enter the info-war PsyAttack beat after the kinetic strike. Timer-driven
        /// (fixed config window); the wave stays "under attack" so the indicator, sirens and
        /// eligibility gates read as an ongoing assault. The kinetic debriefing already fired
        /// (FinalizeKineticWave) — only the transition to Recovery is deferred to when this beat
        /// times out (see the PsyAttack branch in the phase tick). Entering PsyAttack is the
        /// signal PsyOpsLaunchSystem subscribes to.</summary>
        private void ExecutePsyAttackTransition(ref WaveStateSingleton singleton)
        {
            float duration = WaveScalingService.GetPhaseDuration(GamePhase.PsyAttack, singleton.WaveNumber, 1f, 0.5f);

            // Write BEFORE publishing event (Writer Notifies pattern). Sirens stay as-is
            // (on from Attack) through the psy beat — Recovery turns them off.
            singleton.CurrentPhase = GamePhase.PsyAttack;
            singleton.TimeInPhase = 0f;
            singleton.PhaseEndTime = duration;
            singleton.IsUnderAttack = true;
            singleton.SecondsUntilPhaseChange = duration;

            m_Events.NotifyPhaseChanged(GamePhase.PsyAttack, singleton.WaveNumber);
            Log.Info($" PsyAttack started: Wave #{singleton.WaveNumber}, {duration / 60f:F1}min");
        }

        /// <summary>Wind the wave down into the short Recovery phase. The kinetic debriefing +
        /// NotifyWaveEnded already fired at the kinetic end (<see cref="FinalizeKineticWave"/>);
        /// this only flips the phase, clears the under-attack flag and cuts the sirens. Reached
        /// directly on no-psy waves, or after the PsyAttack beat elapses on psy waves.</summary>
        private void EnterRecovery(ref WaveStateSingleton singleton)
        {
            singleton.CurrentPhase = GamePhase.Recovery;
            singleton.TimeInPhase = 0f;
            singleton.PhaseEndTime = RECOVERY_PHASE_DURATION; // Recovery is short
            singleton.IsUnderAttack = false;
            singleton.SecondsUntilPhaseChange = RECOVERY_PHASE_DURATION;

            SetSirensActive(false);

            m_Events.NotifyPhaseChanged(GamePhase.Recovery, singleton.WaveNumber);
        }

        /// <summary>Book the air-defence outcome of the just-cleared kinetic strike — write the
        /// debriefing stats and publish NotifyWaveEnded — the moment the last threat resolves, so
        /// the player sees the intercept summary immediately, not after the info-war beat. Does NOT
        /// touch the phase; the caller then opens PsyAttack (psy waves) or enters Recovery.</summary>
        private void FinalizeKineticWave(ref WaveStateSingleton singleton)
        {
            m_WaveStatsLookup.Update(this);
            m_ShotStatsLookup.Update(this);
            m_DamageStatsLookup.Update(this);
            m_InfraStatsLookup.Update(this);
            var debriefingEntity = ResolveDebriefingEntity();

            // Read stats from singletons (Single Source of Truth)
            int intercepted = 0;
            int hits = 0;
            int crashed = 0;
            // Balance-telemetry breakdown (developer diagnostics): drone/ballistic split read from the
            // same singletons, booked at their decision/terminalization sites.
            int droneIntercepted = 0;
            int ballisticIntercepted = 0;
            int droneHits = 0;
            int ballisticHits = 0;

            var interceptStats = m_InterceptStatsQuery.TryGetSingleton<InterceptStatsSingleton>(out var iStats)
                ? iStats : InterceptStatsSingleton.Default;
            intercepted = interceptStats.InterceptedCount;
            droneIntercepted = interceptStats.InterceptedShahedCount;
            ballisticIntercepted = interceptStats.InterceptedBallisticCount;
            if (m_ThreatOutcomeStatsQuery.TryGetSingleton<ThreatOutcomeStatsSingleton>(out var outcomeStats))
            {
                if (outcomeStats.WaveNumber == singleton.WaveNumber)
                {
                    hits = outcomeStats.HitsCount;
                    droneHits = outcomeStats.DroneHitsCount;
                    ballisticHits = outcomeStats.BallisticHitsCount;
                    crashed = outcomeStats.CrashedCount;
                }
                else if (Log.IsDebugEnabled)
                {
                    Log.Debug($"Ignoring stale threat outcome stats from wave {outcomeStats.WaveNumber}; current wave is {singleton.WaveNumber}");
                }
            }

            // Finalize debriefing — write wave stats, read other components
            int shotsFired = 0;
            int roundsConsumed = 0;
            int missilesConsumed = 0;
            int casualties = 0;
            int damageCost = 0;
            long infraDamageCost = 0;

            if (m_WaveStatsLookup.HasComponent(debriefingEntity))
            {
                var waveStats = m_WaveStatsLookup[debriefingEntity];
                waveStats.Intercepted = intercepted;
                waveStats.Hits = hits;
                waveStats.TotalThreats = singleton.ThreatsSpawned;
                m_WaveStatsLookup[debriefingEntity] = waveStats;
            }
            if (m_ShotStatsLookup.HasComponent(debriefingEntity))
            {
                var shotStats = m_ShotStatsLookup[debriefingEntity];
                shotsFired = shotStats.ShotsFired;
                roundsConsumed = shotStats.RoundsConsumed;
                missilesConsumed = shotStats.MissilesConsumed;
            }
            if (m_DamageStatsLookup.HasComponent(debriefingEntity))
            {
                var dmg = m_DamageStatsLookup[debriefingEntity];
                casualties = dmg.Casualties;
                damageCost = dmg.DamageCost;
            }
            if (m_InfraStatsLookup.HasComponent(debriefingEntity))
            {
                infraDamageCost = m_InfraStatsLookup[debriefingEntity].InfrastructureDamageCost;
            }

            // Intercept rate (kills / threats) — the same metric the player sees in the
            // DebriefingModal (DebriefingWaveStats.InterceptRate). The previous "Efficiency =
            // intercepted / shotsFired" divided kills by *shells*, so it could never exceed
            // 1/BurstRounds and read as a bogus ~1% even when a third of the wave was downed.
            float interceptRate = singleton.ThreatsSpawned > 0 ? (float)intercepted / singleton.ThreatsSpawned * 100f : 0f;
            // Ammo economy in honest shell units: rounds spent per kill (separate from intercept rate).
            float shellsPerKill = intercepted > 0 ? (float)shotsFired / intercepted : 0f;
            Log.Info($" Wave #{singleton.WaveNumber} DEBRIEFING: " +
                $"Threats: {singleton.ThreatsSpawned}, Intercepted: {intercepted}, Hits: {hits}, Crashed: {crashed}, " +
                $"InterceptRate: {interceptRate:F0}%, Shots: {shotsFired}, Shells/kill: {shellsPerKill:F0}, " +
                $"Casualties: {casualties}, Damage: ${damageCost:N0}");

            // FIX A1a: Force immediate threat stats update on recovery (consumers need final counts)
            m_ThreatTargetSystem?.ForceNextUpdate();

            // Publish the wave outcome. The phase transition to Recovery happens separately in
            // EnterRecovery — immediately for no-psy waves, or after the PsyAttack beat.
            m_Events.NotifyWaveEnded(
                singleton.WaveNumber,
                singleton.WaveRole,
                intercepted,
                hits,
                shotsFired,
                casualties,
                damageCost,
                infraDamageCost,
                crashed,
                droneIntercepted,
                droneHits,
                ballisticIntercepted,
                ballisticHits,
                roundsConsumed,
                missilesConsumed);
        }

        // ============================================================================
        // Utilities
        // ============================================================================

        private void ApplyInitialState()
        {
            if (m_HasSavedState && m_SavedState.HasValue)
            {
                var saved = m_SavedState.Value;

                if (!SystemAPI.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                {
                    Log.Error("Restore blocked: WaveStateSingleton unavailable after owner restore");
                    return;
                }

                ref var singleton = ref singletonRef.ValueRW;
                singleton.CurrentPhase = saved.Phase;
                singleton.TimeInPhase = saved.TimeInPhase;
                singleton.PhaseEndTime = saved.PhaseEndTime;
                singleton.WaveNumber = saved.WaveNumber;
                singleton.WaveRole = saved.WaveRole;
                singleton.ThreatsExpected = saved.ThreatsExpected;
                singleton.ThreatsSpawned = saved.ThreatsSpawned;
                // NOTE: ThreatsRemaining is read from ThreatStatsSingleton (derived from live threats).
                singleton.CurrentWaveType = saved.WaveType;
                singleton.IsUnderAttack = saved.Phase == GamePhase.Attack || saved.Phase == GamePhase.PsyAttack;
                singleton.SecondsUntilPhaseChange = math.max(0, saved.PhaseEndTime - saved.TimeInPhase);
                singleton.ScenarioStarted = saved.ScenarioStarted;
                singleton.NextWaveType = saved.NextWaveType;
                singleton.NextWaveNumber = saved.NextWaveNumber;
                singleton.NextWaveThreatsForecast = saved.NextWaveThreatsForecast;
                singleton.NextWaveForecastReady = saved.NextWaveForecastReady;

                if (saved.Phase == GamePhase.Alert || saved.Phase == GamePhase.Attack || saved.Phase == GamePhase.PsyAttack)
                    SetSirensActive(true);

                m_HasSavedState = false;
                m_SavedState = null;

                Log.Info($" Restored from save: Phase={saved.Phase}, Wave #{saved.WaveNumber}");

                // C1 resume: threats now PERSIST across load. ThreatLoadRenderReinitSystem owns
                // terminal-state routing and publishes the live count in ModificationEnd before
                // GameSimulation. WaveExecutor is the sole owner of the phase decision.
                if (saved.Phase == GamePhase.Attack)
                {
                    var restoredThreats = GetRestoredThreatsForResume();
                    if (restoredThreats.LiveThreats > 0)
                    {
                        singleton.ThreatsSpawned = math.max(singleton.ThreatsSpawned, restoredThreats.TotalRestoredThreats);
                        Log.Info($" Resuming interrupted Attack wave: {restoredThreats.LiveThreats} threats restored, wave continues");
                    }
                    else
                    {
                        Log.Info(" Restored Attack phase with 0 live threats; finalizing through Recovery");
                        RefreshDebriefingEntityAfterLoad();
                        // Mirror the live kinetic-clear path: book the debriefing, then open the
                        // info-war beat on psy waves (fires the raid) or wind down to Recovery.
                        FinalizeKineticWave(ref singleton);
                        if (ShouldEnterPsyAttack(singleton.WaveNumber))
                            ExecutePsyAttackTransition(ref singleton);
                        else
                            EnterRecovery(ref singleton);
                    }
                }
            }
            else
            {
                // Fresh start - singleton already has defaults
                Log.Info(" Fresh start with default state");
            }

            m_Initialized = true;
        }

        private int GetActiveThreatsCount()
        {
            // Lifecycle decision must not depend on ThreatStatsSingleton: it is derived UI/cache
            // state and can be stale on paused-after-load or before the 10Hz target pass runs.
#pragma warning disable CIVIC220 // One small sync count on WaveExecutor's phase decision path.
            return m_ActiveThreatQuery.CalculateEntityCount();
#pragma warning restore CIVIC220
        }

        /// <summary>
        /// Raids still IN FLIGHT — the info-war beat is over once the last one lands. What a landed
        /// raid leaves behind (infection pressure, a panic run) outlives the phase on purpose: the
        /// city is no longer under attack, it is living with the damage. Counting landed raids here
        /// would hold the wave open for the whole landed window and stall the next one.
        /// </summary>
        private int GetInFlightPsyRaidsCount()
        {
            if (m_ActivePsyRaidQuery.IsEmpty)
                return 0;

            int inFlight = 0;
            foreach (var raid in SystemAPI.Query<RefRO<PsyOpsAttack>>())
            {
                if (raid.ValueRO.Phase == PsyOpsAttackPhase.InFlight)
                    inFlight++;
            }
            return inFlight;
        }

        /// <summary>
        /// Count live restored threats to decide resume-vs-Recovery after a save-interrupted Attack.
        /// </summary>
        /// <remarks>
        /// DESIGN — the two code paths are INTENTIONAL and both order-independent vs the reinit pass;
        /// this is not fragile coupling, do not "simplify" to one:
        /// - Fast path reads ThreatLoadResumeState published by ThreatLoadRenderReinitSystem in
        ///   ModificationEnd (phase 15). ApplyInitialState only ever runs from GameSimulation
        ///   (phase 18) or PostLoadValidationSystem (also GameSimulation), both AFTER 15 in the same
        ///   frame, so ReinitCompleted is already true when this runs — and on a paused-after-load
        ///   start neither caller ticks, so the decision is simply deferred to unpause (safe: a paused
        ///   wave does not advance). The resume-state singleton is durable until then.
        /// - Fallback scans the DURABLE Shahed/Ballistic state (intercept marker + IsArrived), which
        ///   is present on the restored entities immediately, before reinit re-adds any lifecycle tag.
        ///   It exists for the cold case where the resume-state singleton is somehow absent, and gives
        ///   the same answer without depending on ActiveThreat (which is stripped on save).
        /// </remarks>
        private RestoredThreatCounts GetRestoredThreatsForResume()
        {
            if (m_LoadResumeStateQuery.TryGetSingleton<ThreatLoadResumeState>(out var resumeState)
                && resumeState.ReinitCompleted)
            {
                return new RestoredThreatCounts(resumeState.LiveThreats, resumeState.LiveThreats + resumeState.PurgedTerminalThreats);
            }

            Log.Warn("Threat load resume state missing; falling back to durable threat-state scan");
            return CountRestoredThreatsFallback();
        }

        private RestoredThreatCounts CountRestoredThreatsFallback()
        {
            int live = 0;
            int terminal = 0;
            m_ShahedCombatStateLookup.Update(this);
            m_BallisticInterceptStateLookup.Update(this);
#pragma warning disable CIVIC343 // Load fallback scans durable threat state before lifecycle tags may exist.
            foreach (var (shahed, entity) in
                SystemAPI.Query<RefRO<Shahed>>()
                    .WithEntityAccess()
                    .WithNone<Deleted>())
#pragma warning restore CIVIC343
            {
                bool intercepted = m_ShahedCombatStateLookup.HasComponent(entity)
                    && m_ShahedCombatStateLookup[entity].IsIntercepted;
                if (!shahed.ValueRO.IsArrived && !intercepted)
                    live++;
                else
                    terminal++;
            }

#pragma warning disable CIVIC343 // Load fallback scans durable threat state before lifecycle tags may exist.
            foreach (var (ballistic, entity) in
                SystemAPI.Query<RefRO<Ballistic>>()
                    .WithEntityAccess()
                    .WithNone<Deleted>())
#pragma warning restore CIVIC343
            {
                bool intercepted = m_BallisticInterceptStateLookup.HasComponent(entity)
                    && m_BallisticInterceptStateLookup[entity].IsIntercepted;
                if (!ballistic.ValueRO.IsArrived && !intercepted)
                    live++;
                else
                    terminal++;
            }

            return new RestoredThreatCounts(live, live + terminal);
        }

        private readonly struct RestoredThreatCounts
        {
            public readonly int LiveThreats;
            public readonly int TotalRestoredThreats;

            public RestoredThreatCounts(int liveThreats, int totalRestoredThreats)
            {
                LiveThreats = liveThreats;
                TotalRestoredThreats = totalRestoredThreats;
            }
        }

        private void SetSirensActive(bool active)
        {
            if (Log.IsDebugEnabled) Log.Debug($" Phase siren state: {(active ? "ON" : "OFF")}");
        }

        private void RefreshDebriefingEntityAfterLoad()
        {
            ResolveDebriefingEntity();
        }

        private void RestoreWaveStateSingleton()
        {
            EnsureSingleton(ref m_WaveState, WaveStateSingleton.Default);
        }

        private void RestoreWaveStateSingleton(EntityManager entityManager)
        {
            EnsureSingleton(ref m_WaveState, entityManager, WaveStateSingleton.Default);
        }

        private void RestoreDebriefingEntity()
        {
            EnsureSingleton(
                ref m_Debriefing,
                default(DebriefingWaveStats),
                EnsureDebriefingShape);
        }

        private void RestoreDebriefingEntity(EntityManager entityManager)
        {
            EnsureSingleton(
                ref m_Debriefing,
                entityManager,
                default(DebriefingWaveStats),
                EnsureDebriefingShape);
        }

        private Entity ResolveDebriefingEntity()
        {
            return ResolveSingletonReadOnly(ref m_Debriefing);
        }

        private static void EnsureDebriefingShape(EntityManager em, Entity entity)
        {
#pragma warning disable CIVIC038 // Debriefing host is a dedicated mod-owned singleton entity.
            if (!em.HasComponent<DebriefingShotStats>(entity))
                em.AddComponent<DebriefingShotStats>(entity);
            if (!em.HasComponent<DebriefingDamageStats>(entity))
                em.AddComponent<DebriefingDamageStats>(entity);
            if (!em.HasComponent<DebriefingInfraStats>(entity))
                em.AddComponent<DebriefingInfraStats>(entity);
#pragma warning restore CIVIC038
            em.SetName(entity, "WaveDebriefing");
        }

        [CompletesDependency("Alert-transition (once per wave, not per-frame) reset run from the ScheduleWaveCommand path; uses a cached query + lookup deliberately to avoid binding SystemAPI.Query to the publisher's system context, so the leftover-threat scan is materialised via ToEntityArray")]
        private void ResetLeftoverThreatCombatState()
        {
            int resetCount = 0;
            // Safe on main thread from the ScheduleWaveCommand event handler: cached query/lookup,
            // no SystemAPI binding to the publisher's system context.
            m_ShahedCombatStateLookup.Update(this);
            using var activeThreats = m_ActiveThreatQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < activeThreats.Length; i++)
            {
                Entity entity = activeThreats[i];
                if (!m_ShahedCombatStateLookup.HasComponent(entity))
                    continue;

                var combatState = m_ShahedCombatStateLookup[entity];
                if (combatState.MissedShotsCount == 0 && !combatState.IsIntercepted)
                    continue;

                combatState.MissedShotsCount = 0;
                combatState.IsIntercepted = false;
                m_ShahedCombatStateLookup[entity] = combatState;
                resetCount++;
            }

            if (resetCount > 0)
                Log.Warn($"Reset combat state for {resetCount} leftover active threats on Alert transition");
        }

        // ============================================================================
        // Public API
        // ============================================================================

        public WaveStateSingleton GetCurrentState()
        {
            if (m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var singleton))
                return singleton;
            RestoreWaveStateSingleton();
            if (m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out singleton))
                return singleton;
            return WaveStateSingleton.Default;
        }

#if DEBUG
        private void DebugSkipPhase()
        {
            if (!m_Initialized) return;
            if (!m_WaveStateQuery.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                return;

            ref var singleton = ref singletonRef.ValueRW;
            singleton.TimeInPhase = singleton.PhaseEndTime;
            Log.Info(" Debug: Skipping to next phase");
        }
#endif

        // ============================================================================
        // IResettable
        // ============================================================================

        public void ResetState()
        {
            Log.Info(" ResetState - clearing session data");

            ResetBootDefaultsFields();
            m_ResetWaveStateAfterLoad = false;
            m_DiscardRestoredThreatsAfterLoad = false;
            ThreatLoadRestorePolicyLatch.Clear();

            if (SystemAPI.TryGetSingletonRW<WaveStateSingleton>(out var singletonRef))
                singletonRef.ValueRW = WaveStateSingleton.Default;
        }

        internal void ResetBootDefaultsFields()
        {
            m_Initialized = false;
            m_HasSavedState = false;
            m_SavedState = null;
            m_PendingThreatsSpawned = -1;
            m_LastOvertimeLogTime = double.NegativeInfinity;
            m_PendingScheduleCommands = null;
            ResetProcessRuntimeRefs();
            m_WaveState.Invalidate();
            m_Debriefing.Invalidate();
        }

        public void ValidateAfterLoad()
        {
            ReResolveRuntimeRefs();
            OnLoadRestore(EntityManager);
            if (!m_Initialized)
            {
                ApplyInitialState();
                FlushPendingScheduleWaveCommands();
            }
            // else: the resume / Recovery-fallback decision was already made inside
            // ApplyInitialState on first init (C1) — nothing to finalize here.
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            ReResolveRuntimeRefs();
            RestoreWaveStateSingleton(entityManager);
            RestoreDebriefingEntity(entityManager);
            if (m_DiscardRestoredThreatsAfterLoad)
                ArmDiscardRestoredThreatsPolicy(entityManager);
            if (!m_ResetWaveStateAfterLoad)
                return;

            // Write through the cached handle (resolved by RestoreWaveStateSingleton
            // above) + the passed EntityManager, not SystemAPI: this is a public
            // owner-lifecycle method and SystemAPI here would bind to whichever
            // system's context invoked it (CIVIC281).
            var entity = m_WaveState.Entity;
            if (entity != Entity.Null && entityManager.Exists(entity))
            {
                entityManager.SetComponentData(entity, WaveStateSingleton.Default);
                m_ResetWaveStateAfterLoad = false;
            }
        }

        private void ResetProcessRuntimeRefs()
        {
            m_InterceptProcessor = null!;
            m_DependencyWire?.Reset();
        }

        private void ReResolveRuntimeRefs()
        {
            if (!FeatureRegistry.IsInitialized || m_DependencyWire == null)
                return;

            // Resolved here because ThreatsAirDefense feature registers after Waves.
            m_DependencyWire.Reset();
            m_DependencyWire.EnsureWired(() =>
            {
                var interceptProcessor = FeatureRegistry.Instance.Query<InterceptProcessingSystem>();
                if (interceptProcessor == null)
                    return false;

                m_InterceptProcessor = interceptProcessor;
                return true;
            });
        }

        private void ArmDiscardRestoredThreatsPolicy(EntityManager entityManager)
        {
            ThreatLoadResumeState.EnsureExists(entityManager);
            if (!m_LoadResumeStateQuery.TryGetSingletonEntity<ThreatLoadResumeState>(out var resumeEntity)
                || !entityManager.Exists(resumeEntity))
                return;

            var state = entityManager.GetComponentData<ThreatLoadResumeState>(resumeEntity);
            state.RestorePolicy = ThreatLoadRestorePolicy.DiscardRestoredThreats;
            state.ReinitCompleted = false;
            state.LiveShaheds = 0;
            state.LiveBallistics = 0;
            state.PurgedTerminalThreats = 0;
            entityManager.SetComponentData(resumeEntity, state);
            m_DiscardRestoredThreatsAfterLoad = false;
        }

        // ============================================================================
        // Serialization Support
        // ============================================================================

        internal readonly struct ExecutorSaveState
        {
            public readonly GamePhase Phase;
            public readonly float TimeInPhase;
            public readonly float PhaseEndTime;
            public readonly int WaveNumber;
            public readonly WaveRole WaveRole;
            public readonly WaveType WaveType;
            public readonly int ThreatsExpected;
            public readonly int ThreatsSpawned;
            public readonly bool ScenarioStarted;
            public readonly WaveType NextWaveType;
            public readonly int NextWaveNumber;
            public readonly int NextWaveThreatsForecast;
            public readonly bool NextWaveForecastReady;

            public ExecutorSaveState(GamePhase phase, float timeInPhase, float phaseEndTime,
                int waveNumber, WaveRole waveRole, WaveType waveType, int threatsExpected, int threatsSpawned, bool scenarioStarted,
                WaveType nextWaveType, int nextWaveNumber, int nextWaveThreatsForecast, bool nextWaveForecastReady)
            {
                Phase = phase;
                TimeInPhase = timeInPhase;
                PhaseEndTime = phaseEndTime;
                WaveNumber = waveNumber;
                WaveRole = waveRole;
                WaveType = waveType;
                ThreatsExpected = threatsExpected;
                ThreatsSpawned = threatsSpawned;
                ScenarioStarted = scenarioStarted;
                NextWaveType = nextWaveType;
                NextWaveNumber = nextWaveNumber;
                NextWaveThreatsForecast = nextWaveThreatsForecast;
                NextWaveForecastReady = nextWaveForecastReady;
            }
        }

        internal ExecutorSaveState GetSaveState()
        {
            if (!m_Initialized && m_HasSavedState && m_SavedState.HasValue)
                return m_SavedState.Value;

            var singleton = GetCurrentState();
            int savedThreatsSpawned = m_PendingThreatsSpawned >= 0
                ? m_PendingThreatsSpawned
                : singleton.ThreatsSpawned;
            return new ExecutorSaveState(
                singleton.CurrentPhase,
                singleton.TimeInPhase,
                singleton.PhaseEndTime,
                singleton.WaveNumber,
                singleton.WaveRole,
                singleton.CurrentWaveType,
                singleton.ThreatsExpected,
                savedThreatsSpawned,
                singleton.ScenarioStarted,
                singleton.NextWaveType,
                singleton.NextWaveNumber,
                singleton.NextWaveThreatsForecast,
                singleton.NextWaveForecastReady);
        }

        internal void SetSaveState(ExecutorSaveState state)
        {
            m_SavedState = state;
            m_HasSavedState = true;
            m_Initialized = false;
        }
    }
}
