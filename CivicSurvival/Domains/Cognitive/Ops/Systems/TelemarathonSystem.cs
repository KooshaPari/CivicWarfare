using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Cognitive.Core.Systems;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Cognitive;

using CivicSurvival.Core.Services;
namespace CivicSurvival.Domains.Cognitive.Ops.Systems
{
    /// <summary>
    /// Telemarathon system - unified state media broadcasting.
    ///
    /// Mechanics:
    /// 1. Narrative Slider - 3 modes with different effects on Panic/Trust/Stress
    /// 2. Shock Effect - if Soothing mode during active attack, population sees dissonance
    /// 3. Audience Fatigue - same mode for too long reduces effectiveness
    ///
    /// "Press and forget = lose the info war"
    ///
    /// PERF: Throttled to 1 second - narrative effects are slow-moving.
    ///
    /// S17a-5 ACCEPTED: Shock trigger vs mode-change is sequential within single OnUpdate; no race.
    /// S17a-6 ACCEPTED: Already fixed — fatigue skipped during shock (line: if IsInShock return).
    /// </summary>
#pragma warning disable CIVIC065 // Singleton created conditionally; IDefaultSerializable handles reload
    [ActIndependent]
    [SingletonOwner(typeof(TelemarathonRuntimeState))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    public partial class TelemarathonSystem : ThrottledSystemBase, IPostLoadValidation
#pragma warning restore CIVIC065
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("TelemarathonSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        // Reference speed the cooldown scale divides by (cooldown = ShockCooldownHours × speed / this).
        // NOTE: CS2's actual maximum speed is 3x, so against this 4x reference every real cooldown runs
        // ~25% shorter than ShockCooldownHours suggests (0.75× at 3x speed, 0.25× at 1x). Whether that
        // shorter cadence is the desired balance is an open tuning question — revisit together with the
        // psych-rate pass; changing this constant to 3 lengthens all shock cooldowns by a third.
        private const float SHOCK_CALIBRATION_SPEED = 4f;

        // CW-01: m_StateQuery kept for Serialization partial (SystemAPI unavailable in Serialize/Deserialize)
        private EntityQuery m_StateQuery;
        private EntityQuery m_ConfigQuery;
        // Cached act query: TryApplyMode/TryApplyActive run from sync UI trigger callbacks
        // (CognitiveUISystem.OnSetNarrativeMode/OnSetTelemarathonActive), outside this system's
        // OnUpdate — SystemAPI.* resolves against CheckedStateRef and throws from a foreign context.
        private EntityQuery m_CurrentActQuery;
        private EntityQuery m_WaveStateQuery;
        private SimulationSystem m_SimulationSystem = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            // CW-01: m_StateQuery kept for Serialization partial (SystemAPI unavailable there)
            m_StateQuery = GetEntityQuery(ComponentType.ReadWrite<TelemarathonRuntimeState>());
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<TelemarathonConfig>());
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_WaveStateQuery = GetEntityQuery(ComponentType.ReadOnly<WaveStateSingleton>());
            EnsureRuntimeStateExists();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            Log.Info(" Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }

        protected override bool ShouldSkipUpdate()
        {
            if (!m_StateQuery.TryGetSingleton<TelemarathonRuntimeState>(out var state))
                return true;

            return !state.IsActive && !state.IsInShock;
        }

        protected override void OnThrottledUpdate()
        {
            if (!m_StateQuery.TryGetSingletonRW<TelemarathonRuntimeState>(out var stateRef))
                return;

            if (!m_ConfigQuery.TryGetSingleton<TelemarathonConfig>(out var cfg))
                return;

            if (!GameTimeSystem.TryGetGameHours(out var currentHour))
            {
                Log.Warn("[TelemarathonSystem] TimeProvider unavailable; throttled update skipped");
                return;
            }
            float deltaHours = GameRate.HoursDelta(ThrottledDeltaSeconds);

            // 1. Maintain shock even while the telemarathon is inactive.
            RefreshShockState(ref stateRef.ValueRW, currentHour);
            if (!stateRef.ValueRO.IsActive)
            {
                PublishDerivedFacts(ref stateRef.ValueRW, in cfg);
                return;
            }

            // 2. Check for Shock condition (Soothing during attack)
            ProcessShockCheck(ref stateRef.ValueRW, in cfg, currentHour);

            // 3. Update Trust based on mode (now sees updated IsInShock)
            ProcessTrustDecay(ref stateRef.ValueRW, in cfg, deltaHours);

            // 4. Update Audience Fatigue (now sees updated IsInShock)
            ProcessFatigue(ref stateRef.ValueRW, in cfg, deltaHours, currentHour);

            // 5. Publish derived facts for cross-domain readers
            PublishDerivedFacts(ref stateRef.ValueRW, in cfg);
        }

        private static void RefreshShockState(ref TelemarathonRuntimeState state, float currentHour)
        {
            if (state.ShockEndHour <= 0f)
            {
                state.ShockHoursRemaining = 0f;
                return;
            }

            state.ShockHoursRemaining = math.max(0f, state.ShockEndHour - currentHour);
            if (state.ShockHoursRemaining <= 0f)
                state.ShockEndHour = 0f;
        }

        /// <summary>
        /// Check if Soothing mode during active attack -> trigger Shock.
        /// "TV says everything is fine, but explosions outside the window"
        /// </summary>
        private void ProcessShockCheck(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg, float currentHour)
        {
            // Already in shock? Skip
            if (state.IsInShock)
                return;

            // Only Soothing mode can trigger shock
            if (state.Mode != NarrativeMode.Soothing)
                return;

            // Cooldown: prevent shock spam from consecutive waves
            // S18-H10 FIX: Use pre-computed ShockCooldownEndHour instead of re-evaluating
            // cooldown each tick. Old approach was exploitable: switching from 4x→1x after shock
            // shortened cooldown from 6h to 1.5h game-time.
            if (currentHour < state.ShockCooldownEndHour)
                return;

            // Check if there's an active attack
            if (!HasActiveAttack())
                return;

            // FIX W2-M7: Use cached config (was live BalanceConfig.Current without null-safety)
            state.ShockHoursRemaining = cfg.ShockDurationHours;
            state.ShockEndHour = currentHour + cfg.ShockDurationHours;
            state.Trust = math.max(0f, state.Trust - cfg.ShockTrustPenalty);
            // S18-H10 FIX: Lock cooldown end at trigger time — immune to speed changes
            float baseCooldown = cfg.ShockCooldownHours;
            float speed = math.max(m_SimulationSystem.selectedSpeed, 1f);
            float cooldown = baseCooldown * (speed / SHOCK_CALIBRATION_SPEED);
            state.ShockCooldownEndHour = currentHour + cooldown;

            Log.Warn($" SHOCK! Soothing mode during attack. Trust: -{cfg.ShockTrustPenalty * 100:F0}%");

            EventBus?.SafePublish(new TelemarathonShockEvent(state.Trust), "TelemarathonSystem");
        }

        /// <summary>
        /// Trust decays or recovers based on current mode.
        /// </summary>
        private static void ProcessTrustDecay(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg, float deltaHours)
        {
            // Shock state = trust frozen (people not watching TV)
            if (state.IsInShock)
                return;

            float trustDecayRate = state.Mode switch
            {
                NarrativeMode.Soothing => cfg.SoothingTrustDecay,
                NarrativeMode.Alarmist => cfg.AlarmistTrustDecay,
                NarrativeMode.Realistic => -cfg.RealisticTrustRecovery,
                _ => 0f
            };
            float trustChange = -trustDecayRate * deltaHours;
            state.Trust = math.clamp(state.Trust + trustChange, 0f, 1f);
        }

        /// <summary>
        /// Audience fatigue increases over time in same mode.
        /// </summary>
        private static void ProcessFatigue(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg, float deltaHours, float currentHour)
        {
            // S17a-9/S17a-6: No fatigue while in shock — audience isn't watching
            if (state.IsInShock) return;

            // Guard: if the anchors were never set because time was unavailable at activation,
            // hoursInWindow can be huge → instant fatigue. Treat as fresh start.
            if (state.FatigueWindowStartHour < 0f)
            {
                state.FatigueWindowStartHour = currentHour;
                if (state.LastModeChangeHour < 0f)
                    state.LastModeChangeHour = currentHour;
                return;
            }

            // Fatigue accumulates once the audience has watched past the 24h grace window.
            // Anchor is FatigueWindowStartHour, NOT LastModeChangeHour: a quick mode flick
            // does not restart the window (see ApplyModeSwitchFatigueRecovery).
            float hoursInWindow = currentHour - state.FatigueWindowStartHour;
            if (hoursInWindow > GameRate.HOURS_PER_DAY) // Start fatiguing after 24 hours
            {
                // Split-delta on boundary crossing — only count hours above 24h threshold
                // Same pattern as BlackoutCalculator.cs:66-76 (patience→panic split)
                float prevHoursInWindow = hoursInWindow - deltaHours;
                float effectiveDelta = prevHoursInWindow >= GameRate.HOURS_PER_DAY
                    ? deltaHours
                    : hoursInWindow - GameRate.HOURS_PER_DAY;
                state.AudienceFatigue = math.min(1f, state.AudienceFatigue + cfg.FatigueRatePerHour * effectiveDelta);
            }
        }

        /// <summary>
        /// Fatigue bookkeeping for a real mode switch (marathon active).
        ///
        /// Recovery is proportional to the pause in accumulation instead of a fixed chunk:
        /// the audience recovers at the same rate it fatigues (<c>FatigueRatePerHour</c>),
        /// but only for the hours since the previous switch that fell inside the 24h grace
        /// window — i.e. hours when fatigue was NOT accumulating. A micro-flick minutes after
        /// the previous switch therefore recovers almost nothing, while an honest daily
        /// rotation earns a full day's worth.
        ///
        /// The grace window itself restarts only after a full-day dwell in one mode
        /// (>= 24h since the previous switch). A quick flick keeps the old window anchor,
        /// so fatigue hours keep counting and the 24h grace cannot be farmed by switching.
        /// </summary>
        private static void ApplyModeSwitchFatigueRecovery(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg, float currentHour)
        {
            if (state.LastModeChangeHour < 0f || state.FatigueWindowStartHour < 0f)
            {
                // Anchors were never set (time unavailable at activation): fresh start, no recovery.
                state.LastModeChangeHour = currentHour;
                state.FatigueWindowStartHour = currentHour;
                return;
            }

            float hoursSinceSwitch = math.max(0f, currentHour - state.LastModeChangeHour);
            float graceEndHour = state.FatigueWindowStartHour + GameRate.HOURS_PER_DAY;
            // Hours since the previous switch during which fatigue was not accumulating
            // (the part of [LastModeChangeHour, currentHour] inside the grace window).
            float recoveryHours = math.clamp(
                math.min(currentHour, graceEndHour) - math.max(state.LastModeChangeHour, state.FatigueWindowStartHour),
                0f, hoursSinceSwitch);
            state.AudienceFatigue = math.max(0f, state.AudienceFatigue - cfg.FatigueRatePerHour * recoveryHours);

            if (hoursSinceSwitch >= GameRate.HOURS_PER_DAY)
                state.FatigueWindowStartHour = currentHour; // full-day dwell → audience treats the new tone as fresh

            state.LastModeChangeHour = currentHour;
        }

        /// <summary>
        /// Write precomputed derived facts for cross-domain readers.
        /// Called after all state mutations in OnThrottledUpdate.
        /// </summary>
        private static void PublishDerivedFacts(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg)
        {
            state.EffectivenessMult = 1f - (state.AudienceFatigue * cfg.FatigueMaxReduction);
            state.SpotterDetectionBonus = state.Mode == NarrativeMode.Alarmist ? cfg.AlarmistSpotterBonus : 0f;
            state.StressRate = state.Mode == NarrativeMode.Alarmist ? cfg.AlarmistStressRate : 0f;
        }

        /// <summary>
        /// Check if there's an active threat (drones/missiles in flight).
        /// WaveStateSingleton is the authoritative immediate phase source; throttled
        /// ThreatStatsSingleton is intentionally not used to avoid suppressing wave-start shocks.
        /// </summary>
        private bool HasActiveAttack()
        {
            if (!m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var waveState))
                return false;

            return waveState.IsUnderAttack && (waveState.CurrentPhase == GamePhase.Attack || waveState.CurrentPhase == GamePhase.PsyAttack);
        }

        // === Public API for UI ===

        /// <summary>
        /// Set narrative mode. Called from UI.
        /// </summary>
        public bool TrySetMode(NarrativeMode mode, out ReasonId reasonId)
        {
            reasonId = ReasonId.None;

            if (!m_StateQuery.TryGetSingletonRW<TelemarathonRuntimeState>(out var stateRef))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            if (!m_ConfigQuery.TryGetSingleton<TelemarathonConfig>(out var cfg))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            if (!GameTimeSystem.TryGetGameHours(out var currentHour))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }
            bool applied = TryApplyMode(ref stateRef.ValueRW, in cfg, mode, currentHour);
            if (!applied)
            {
                Log.Debug(" SetMode request was not applied");
                reasonId = ReasonIds.TelemarathonRejected;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Toggle marathon on/off.
        /// </summary>
        public bool TrySetActive(bool active, out ReasonId reasonId)
        {
            reasonId = ReasonId.None;

            if (!m_StateQuery.TryGetSingletonRW<TelemarathonRuntimeState>(out var stateRef))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            if (!GameTimeSystem.TryGetGameHours(out var currentHour))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }
            bool applied = TryApplyActive(ref stateRef.ValueRW, active, currentHour);
            if (!applied)
            {
                Log.Debug(" SetActive request was not applied");
                reasonId = ReasonIds.TelemarathonRejected;
                return false;
            }

            return true;
        }

        private bool TryApplyMode(ref TelemarathonRuntimeState state, in TelemarathonConfig cfg, NarrativeMode mode, float currentHour)
        {
            // R3-D-8 FIX: Act gate — telemarathon only available during war
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton) && actSingleton.CurrentAct == Act.PreWar)
            {
                Log.Info(" Mode change rejected: PreWar gate");
                return false;
            }

            if (state.Mode == mode)
                return true;

            var oldMode = state.Mode;
            state.Mode = mode;

            // While inactive the audience isn't watching: a switch grants no recovery and moves
            // no anchors (they stay frozen and are shifted forward on reactivation, TryApplyActive).
            if (state.IsActive)
                ApplyModeSwitchFatigueRecovery(ref state, in cfg, currentHour);

            // Publish derived facts immediately so cross-domain readers see new mode effects
            PublishDerivedFacts(ref state, in cfg);

            string oldName = GetModeName(oldMode);
            string newName = GetModeName(mode);
            Log.Info($" Mode changed: {oldName} -> {newName}");

            EventBus?.SafePublish(new TelemarathonModeChangedEvent(oldMode, mode), "TelemarathonSystem");
            return true;
        }

        private bool TryApplyActive(ref TelemarathonRuntimeState state, bool active, float currentHour)
        {
            // R3-D-8 FIX: Act gate — telemarathon only available during war
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton) && actSingleton.CurrentAct == Act.PreWar)
            {
                Log.Info(" Active toggle rejected: PreWar gate");
                return false;
            }

            if (state.IsActive == active)
                return true;

            if (active)
            {
                // Off-air time is a freeze, not a reset: shift both fatigue anchors forward by
                // the off duration so the audience resumes exactly where it left off. A quick
                // off→on toggle therefore grants no fresh 24h grace (the old behavior of
                // re-seeding LastModeChangeHour here made fatigue indefinitely deferrable),
                // while a long off period still doesn't cause instant fatigue on return.
                if (state.InactiveSinceHour >= 0f)
                {
                    float offHours = math.max(0f, currentHour - state.InactiveSinceHour);
                    if (state.LastModeChangeHour >= 0f)
                        state.LastModeChangeHour = math.min(state.LastModeChangeHour + offHours, currentHour);
                    if (state.FatigueWindowStartHour >= 0f)
                        state.FatigueWindowStartHour = math.min(state.FatigueWindowStartHour + offHours, currentHour);
                    state.InactiveSinceHour = -1f;
                }

                // First-ever activation (or time was unavailable before): seed the anchors so
                // ProcessFatigue doesn't see currentHour - 0 > 24 → instant fatigue.
                if (state.LastModeChangeHour < 0f)
                    state.LastModeChangeHour = currentHour;
                if (state.FatigueWindowStartHour < 0f)
                    state.FatigueWindowStartHour = currentHour;
            }
            else
            {
                state.InactiveSinceHour = currentHour;
            }

            state.IsActive = active;

            string status = active ? "ACTIVATED" : "DEACTIVATED";
            Log.Info($" {status}");
            return true;
        }

        /// <summary>
        /// Apply an external hit to media trust — a FakeVideo deepfake undermining the credibility
        /// of the player's own channels. One-writer respected (CIVIC175): the write lives
        /// on the singleton owner; callers (the Cognitive-domain PSYOPS launcher) invoke this
        /// synchronously on the main thread, exactly like the UI <c>TrySet*</c> entry points, instead
        /// of writing <see cref="TelemarathonRuntimeState.Trust"/> themselves. Applies regardless of
        /// active/shock state (a deepfake erodes believability even while the marathon is off — the
        /// loss is felt when it next broadcasts). No-op until the runtime singleton exists.
        /// </summary>
        /// <param name="delta">Trust change (negative for a hit), applied clamped to [0,1].</param>
        /// <param name="reason">Diagnostic label.</param>
        /// <summary>Media trust [0..1], or -1 when the state singleton is not up yet. Read-only —
        /// diagnostics (PsyOps harm log) and read-side consumers; the write path stays TrySetActive /
        /// ApplyMediaTrustHit (one writer, CIVIC175).</summary>
        public float CurrentTrust =>
            m_StateQuery.TryGetSingleton<TelemarathonRuntimeState>(out var s) ? s.Trust : -1f;

        /// <summary>True while the marathon is broadcasting (the standing defence is up).</summary>
        public bool IsOnAir =>
            m_StateQuery.TryGetSingleton<TelemarathonRuntimeState>(out var s) && s.IsActive;

        public void ApplyMediaTrustHit(float delta, string reason)
        {
            if (!m_StateQuery.TryGetSingletonRW<TelemarathonRuntimeState>(out var stateRef))
                return;

            float old = stateRef.ValueRO.Trust;
            stateRef.ValueRW.Trust = math.clamp(old + delta, 0f, 1f);
            Log.Info($" Media trust hit ({reason}): {old:F2} -> {stateRef.ValueRO.Trust:F2} ({delta:+0.00;-0.00})");
        }

        private static string GetModeName(NarrativeMode mode) => mode switch
        {
            NarrativeMode.Soothing => "Soothing (Warm Bath)",
            NarrativeMode.Alarmist => "Alarmist (Mobilization)",
            NarrativeMode.Realistic => "Realistic (Harsh Truth)",
            _ => "Unknown"
        };

        // Fallback values match BalanceConfig defaults (used when config not yet loaded)
        // W2-L5: SoothingPanicMult/RealisticPanicMult removed (dead fields — never read)
        private const float FALLBACK_SOOTHING_TRUST_DECAY = 0.05f;
        private const float FALLBACK_ALARMIST_TRUST_DECAY = 0.02f;
        private const float FALLBACK_REALISTIC_TRUST_RECOVERY = 0.03f;
        private const float FALLBACK_ALARMIST_SPOTTER_BONUS = 0.3f;
        private const float FALLBACK_ALARMIST_STRESS_RATE = 0.1f;
        private const float FALLBACK_FATIGUE_MAX_REDUCTION = 0.75f;
        private const float FALLBACK_FATIGUE_RATE_PER_HOUR = 0.02f;
        private const float FALLBACK_SHOCK_DURATION_HOURS = 2f;
        private const float FALLBACK_SHOCK_TRUST_PENALTY = 0.3f;
        private const float FALLBACK_SHOCK_COOLDOWN_HOURS = 6f;

        private static TelemarathonConfig BuildConfig()
        {
            var cfg = BalanceConfig.Current?.Cognitive;
            return new TelemarathonConfig
            {
                SoothingTrustDecay = cfg?.SoothingTrustDecay ?? FALLBACK_SOOTHING_TRUST_DECAY,
                AlarmistTrustDecay = cfg?.AlarmistTrustDecay ?? FALLBACK_ALARMIST_TRUST_DECAY,
                RealisticTrustRecovery = cfg?.RealisticTrustRecovery ?? FALLBACK_REALISTIC_TRUST_RECOVERY,
                AlarmistSpotterBonus = cfg?.AlarmistSpotterBonus ?? FALLBACK_ALARMIST_SPOTTER_BONUS,
                AlarmistStressRate = cfg?.AlarmistStressRate ?? FALLBACK_ALARMIST_STRESS_RATE,
                FatigueMaxReduction = cfg?.FatigueMaxReduction ?? FALLBACK_FATIGUE_MAX_REDUCTION,
                FatigueRatePerHour = cfg?.FatigueRatePerHour ?? FALLBACK_FATIGUE_RATE_PER_HOUR,
                // FIX W2-M7: Previously read live — now cached with null-safety
                ShockDurationHours = cfg?.ShockDurationHours ?? FALLBACK_SHOCK_DURATION_HOURS,
                ShockTrustPenalty = cfg?.ShockTrustPenalty ?? FALLBACK_SHOCK_TRUST_PENALTY,
                ShockCooldownHours = cfg?.ShockCooldownHours ?? FALLBACK_SHOCK_COOLDOWN_HOURS,
            };
        }
    }
}
