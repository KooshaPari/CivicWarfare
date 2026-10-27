using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Intel;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

// LogContext already in CivicSurvival.Core.Utils

namespace CivicSurvival.Domains.Intel.Systems
{
    /// <summary>
    /// Intel state system - calculates and publishes intel predictions.
    /// Implements "Signal vs Noise" mechanic:
    /// - Without insider: wide ranges, hidden wave type, unknown count
    /// - With insider: precise values, revealed wave type, exact count
    ///
    /// Phase contract: in Calm/Recovery (the insider purchase window) predictions describe
    /// the NEXT wave via WaveStateSingleton.NextWave* (decided by WaveScheduler at
    /// Recovery/Calm entry); in Alert/Attack they describe the scheduled wave via
    /// CurrentWaveType/ThreatsExpected. Never reveal the finished wave as the coming one.
    ///
    /// Owns persistent state (HasInsider, IntelUpgradeLevel) and serializes it.
    /// Updates IntelStateSingleton every frame for UI consumption.
    ///
    /// See also: IntelPurchaseSystem (handles purchase requests)
    /// </summary>
    [ActIndependent]
#pragma warning disable CIVIC228 // OnWaveEnded uses ForceNextUpdate (correct) instead of ResetThrottleCounter (save/load only)
    public partial class IntelStateSystem : ThrottledSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation
#pragma warning restore CIVIC228
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // ===== Tension level constants =====
        private const int TENSION_ALERT = 75;
        private const int TENSION_RECOVERY = 25;
        private const int TENSION_CALM_SCALE = 50;
        private const int TENSION_INVALID_FALLBACK = 0;
        // S3-05 FIX: moved to RemoteBalanceConfig.IntelConfig.IntelUpgradeCostPerLevel

        private static readonly LogContext Log = new("IntelStateSystem");

        private EntityQuery m_PowerGridQuery;

        // Persistent state (serialized)
        private bool m_HasInsider;
        private int m_IntelUpgradeLevel;

        // Log spam prevention — NOT persisted: warning must fire once per session, not once per save
        [System.NonSerialized] private bool m_InvalidEndTimeLogged;

        // PERF: Throttle updates (UI doesn't need 60fps intel data)
        protected override int UpdateInterval => 10;  // ~6fps at 60fps game

        // Cached singletons (refreshed each frame, not persisted)
        [System.NonSerialized] private bool m_HasWaveState;
        [System.NonSerialized] private WaveStateSingleton m_WaveState;

        public const int MAX_INTEL_UPGRADE_LEVEL = 2;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PowerGridQuery = GetEntityQuery(ComponentType.ReadOnly<PowerGridSingleton>());

            // Domain-Driven Initialization (Static Factory)
            IntelStateSingleton.EnsureExists(EntityManager);

            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);

            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            base.OnDestroy();
        }

        protected override void OnThrottledUpdate()
        {
            using (PerformanceProfiler.Measure("IntelStateSystem.OnUpdate"))
            {
                // Cache singletons for this frame
                CacheSingletons();

                // Update singleton with current intel state
                UpdateSingleton();
            }
        }

        private void CacheSingletons()
        {
            m_HasWaveState = SystemAPI.TryGetSingleton(out m_WaveState);
        }

        private void UpdateSingleton()
        {
            if (!SystemAPI.TryGetSingletonRW<IntelStateSingleton>(out var singleton))
                return;

            ref var s = ref singleton.ValueRW;

            // Tension (cache to avoid redundant calculation)
            int tensionLevel = TensionLevel;
            s.TensionLevel = tensionLevel;
            s.TensionStatus = GetTensionStatusFromLevel(tensionLevel);

            // Wave prediction
            s.WaveTypePrediction = WaveTypePrediction;
            s.IsMassiveStrikePredicted = IsMassiveStrikePredicted;

            // Target focus
            var energy = EnergyFocusRange;
            s.EnergyFocusMin = energy.min;
            s.EnergyFocusMax = energy.max;

            var infra = InfraFocusRange;
            s.InfraFocusMin = infra.min;
            s.InfraFocusMax = infra.max;

            var residential = ResidentialFocusRange;
            s.ResidentialFocusMin = residential.min;
            s.ResidentialFocusMax = residential.max;

            // Time estimate
            var time = TimeEstimate;
            s.TimeEstimateMinHours = time.minHours;
            s.TimeEstimateMaxHours = time.maxHours;
            s.TimeEstimateStatus = TimeEstimateStatus;

            // Threat count. The counts ARE the composition — the UI phrases them; a second
            // pre-phrased field said the same numbers again, in English only.
            var (shaheds, ballistics) = ThreatCountEstimate;
            s.EstimatedShaheds = shaheds ?? -1;
            s.EstimatedBallistics = ballistics ?? -1;

            // Insider state
            s.HasInsider = m_HasInsider;
            s.InsiderCost = InsiderCost;

            // Economy impact — FIX S20_CODE3:25: Use cached tensionLevel to avoid redundant TensionLevel recomputation
            s.PriceMultiplier = GetPriceMultiplierForLevel(tensionLevel);
            s.PriceModifierPercent = (int)Math.Round((s.PriceMultiplier - 1f) * 100f);

            // Intel upgrades
            s.IntelUpgradeLevel = m_IntelUpgradeLevel;
            s.IsMaxIntelUpgrade = m_IntelUpgradeLevel >= MAX_INTEL_UPGRADE_LEVEL;
            s.IntelUpgradeCost = GetIntelUpgradeCost();
        }

        private void OnWaveEnded(WaveEndedEvent evt)
        {
            ForceNextUpdate();
            if (m_HasInsider)
            {
                m_HasInsider = false;
                Log.Info($"Insider expired after wave #{evt.WaveNumber}");
            }
        }

        // ============================================================================
        // Public State Accessors (for IntelPurchaseSystem)
        // ============================================================================

        public bool HasInsider => m_HasInsider;
        public int IntelUpgradeLevel => m_IntelUpgradeLevel;

        // R9-L19: internal — only IntelPurchaseSystem calls these (same assembly)
        internal void SetInsider(bool value)
        {
            m_HasInsider = value;
        }

        internal void IncrementUpgradeLevel()
        {
            if (m_IntelUpgradeLevel < MAX_INTEL_UPGRADE_LEVEL)
                m_IntelUpgradeLevel++;
        }

        // ============================================================================
        // Intel Properties
        // ============================================================================

        public int TensionLevel
        {
            get
            {
                if (!m_HasWaveState || !m_WaveState.ScenarioStarted) return 0;

                return m_WaveState.CurrentPhase switch
                {
                    GamePhase.Attack => 100,
                    GamePhase.PsyAttack => 100,
                    GamePhase.Alert => TENSION_ALERT,
                    GamePhase.Recovery => TENSION_RECOVERY,
                    GamePhase.Calm => m_WaveState.PhaseEndTime > 0
                        ? (int)Math.Round(math.clamp(m_WaveState.TimeInPhase / m_WaveState.PhaseEndTime, 0f, 1f) * TENSION_CALM_SCALE)
                        : LogInvalidEndTime(),
                    _ => 0
                };
            }
        }

        public string TensionStatus => GetTensionStatusFromLevel(TensionLevel);

        private static string GetTensionStatusFromLevel(int level)
        {
            var intel = BalanceConfig.Current.Intel;
            if (level > intel.TensionHighMax) return "CRITICAL";
            if (level > intel.TensionElevatedMax) return "HIGH";
            if (level > intel.TensionLowMax) return "ELEVATED";
            return "LOW";
        }

        /// <summary>
        /// Phase-aware source for "which wave does the intel describe".
        /// During Calm/Recovery — the only insider purchase window — CurrentWaveType /
        /// ThreatsExpected still hold the FINISHED wave, so revealing them there sold the
        /// player last wave's data as the next one's. WaveScheduler now decides the next
        /// wave at Recovery/Calm entry and mirrors it into WaveStateSingleton.NextWave*;
        /// this helper picks the forecast in Calm/Recovery and the scheduled wave in
        /// Alert/Attack. Returns false when nothing is knowable yet (pre-war, legacy save) —
        /// callers must surface an explicit pending state, never stale previous-wave data.
        /// </summary>
        private bool TryGetPredictedWave(out WaveType waveType, out int expectedThreats, out int waveNumber, out bool intro)
        {
            if (m_HasWaveState)
            {
                switch (m_WaveState.CurrentPhase)
                {
                    case GamePhase.Calm:
                    case GamePhase.Recovery:
                        if (m_WaveState.NextWaveForecastReady)
                        {
                            waveType = m_WaveState.NextWaveType;
                            expectedThreats = m_WaveState.NextWaveThreatsForecast;
                            waveNumber = m_WaveState.NextWaveNumber;
                            intro = false; // forecasts always describe a regular wave
                            return true;
                        }
                        break;

                    case GamePhase.Alert:
                    case GamePhase.Attack:
                    case GamePhase.PsyAttack: // psy-beat runs inside the wave — same in-flight read
                        // The current wave IS the one in flight.
                        waveType = m_WaveState.CurrentWaveType;
                        expectedThreats = m_WaveState.ThreatsExpected;
                        waveNumber = m_WaveState.WaveNumber;
                        intro = m_WaveState.WaveRole == WaveRole.Intro;
                        return true;

                    default:
                        break; // unmapped phase value — fall through to the pending read below
                }
            }

            waveType = WaveType.Harassment;
            expectedThreats = 0;
            waveNumber = 0;
            intro = false;
            return false;
        }

        /// <summary>
        /// Locale id (not display text) for the wave-type readout — the UI resolves it, so the
        /// string reaches a Ukrainian player in Ukrainian.
        /// </summary>
        public string WaveTypePrediction
        {
            get
            {
                if (!m_HasWaveState) return "UI_NO_DATA";

                // No insider ⇒ the TYPE of the wave is genuinely unread. It used to answer with the
                // game phase instead ("Attack In Progress"), which the ETA readout already reports —
                // two fields saying the same thing, and a phase dressed up as intelligence.
                if (!m_HasInsider) return "INTEL_UNKNOWN";

                // Honest pending state instead of last wave's type sold as the next one's.
                if (!TryGetPredictedWave(out var waveType, out _, out _, out _))
                    return "INTEL_FORECAST_PENDING";

                return waveType == WaveType.MassiveStrike
                    ? "INTEL_WAVE_MASSIVE"
                    : "INTEL_WAVE_HARASSMENT";
            }
        }

        public bool IsMassiveStrikePredicted
        {
            get
            {
                if (!m_HasInsider || !m_HasWaveState) return false;
                return TryGetPredictedWave(out var waveType, out _, out _, out _)
                    && waveType == WaveType.MassiveStrike;
            }
        }

        public (int min, int max) EnergyFocusRange
        {
            get
            {
                if (!TryGetPredictedWave(out var waveType, out _, out _, out var intro)) return (0, 100);
                var ratios = WaveHelper.GetTargetingRatios(waveType, intro: intro);
                return ApplyNoise(ratios.energy * 100f);
            }
        }

        public (int min, int max) InfraFocusRange
        {
            get
            {
                if (!TryGetPredictedWave(out var waveType, out _, out _, out var intro)) return (0, 100);
                var ratios = WaveHelper.GetTargetingRatios(waveType, intro: intro);
                return ApplyNoise((ratios.critical + ratios.service) * 100f);
            }
        }

        public (int min, int max) ResidentialFocusRange
        {
            get
            {
                if (!TryGetPredictedWave(out var waveType, out _, out _, out var intro)) return (0, 100);
                var ratios = WaveHelper.GetTargetingRatios(waveType, intro: intro);
                return ApplyNoise(ratios.civilian * 100f);
            }
        }

        public (float minHours, float maxHours) TimeEstimate
        {
            get
            {
                // Use (-1,-1) sentinel when unavailable or when phase makes estimate meaningless.
                // UI receives TimeEstimateStatus and must not reuse a previous value.
                if (!m_HasWaveState) return (-1f, -1f);
                // During Attack/Recovery the countdown is time-until-phase-ends, not time-to-next-wave
                var phase = m_WaveState.CurrentPhase;
                if (phase == GamePhase.Attack || phase == GamePhase.PsyAttack || phase == GamePhase.Recovery) return (-1f, -1f);

                // Waiting for the dawn/dusk launch window: the phase clock is stale (Calm expired,
                // SecondsUntilPhaseChange pinned at 0). There is no meaningful countdown — sentinel
                // out so UI shows the awaiting-window status string instead of a fake zero.
                if (phase == GamePhase.Calm && m_WaveState.WaitingForLaunchWindow) return (-1f, -1f);

                // Clamp to 0 when the phase clock is beyond the phase end.
                float secondsUntil = math.max(0f, m_WaveState.SecondsUntilPhaseChange);
                float exactHours = GameRate.HoursDelta(secondsUntil);

                if (m_HasInsider)
                    return (exactHours, exactHours);

                float minHours = exactHours * BalanceConfig.Current.Intel.TimeNoiseMin;
                float maxHours = exactHours * BalanceConfig.Current.Intel.TimeNoiseMax;

                if (maxHours - minHours < 1f)
                {
                    // H12: proportional fallback — avoids inflating near-zero windows (e.g. 3min → "0-33min")
                    float halfSpread = math.max(0.5f, exactHours * 0.5f);
                    minHours = math.max(0f, exactHours - halfSpread);
                    maxHours = exactHours + halfSpread;
                }
                if (minHours > maxHours) minHours = maxHours; // H12: bad config guard (TimeNoiseMin > TimeNoiseMax)

                return (minHours, maxHours);
            }
        }

        public FixedString32Bytes TimeEstimateStatus
        {
            get
            {
                if (!m_HasWaveState) return "unknown";
                return m_WaveState.CurrentPhase switch
                {
                    // Waiting for dawn/dusk → status string (no meaningful countdown).
                    GamePhase.Calm => m_WaveState.WaitingForLaunchWindow ? "awaiting-window" : "available",
                    GamePhase.Alert => "available",
                    GamePhase.Attack => "in-attack",
                    GamePhase.PsyAttack => "in-attack",
                    GamePhase.Recovery => "in-recovery",
                    _ => "unknown"
                };
            }
        }

        public (int? shaheds, int? ballistics) ThreatCountEstimate
        {
            get
            {
                if (!m_HasInsider || !m_HasWaveState)
                    return (null, null);

                // Phase-aware: in Calm/Recovery this is the scheduler's forecast of the NEXT
                // wave (deterministic pre-noise estimate); in Alert/Attack the exact scheduled
                // count. No knowable wave yet → (null, null) → -1 counts → the UI says "unknown swarm".
                if (!TryGetPredictedWave(out _, out int expectedThreats, out int waveNumber, out _))
                    return (null, null);

                int citySizeMW = GetCitySizeMW();
                int ballistics = WaveHelper.EstimateBallisticCount(citySizeMW, waveNumber);

                // S20-H8 FIX: ThreatsExpected is total (shaheds + ballistics).
                // Subtract ballistics to avoid double-counting in UI display.
                ballistics = math.min(ballistics, expectedThreats); // M15: prevent negative shahed count from misconfigured balance
                return (math.max(0, expectedThreats - ballistics), ballistics);
            }
        }

        public long InsiderCost => BalanceConfig.Current.Economy.InsiderCost; // long matches wallet API

        public float PriceMultiplier => GetPriceMultiplierForLevel(TensionLevel);

        public int PriceModifierPercent => (int)Math.Round((PriceMultiplier - 1f) * 100f);

        private static float GetPriceMultiplierForLevel(int level)
        {
            var intel = BalanceConfig.Current.Intel;
            if (level > intel.TensionHighMax) return intel.PriceMultCritical;
            if (level > intel.TensionElevatedMax) return intel.PriceMultHigh;
            if (level > intel.TensionLowMax) return intel.PriceMultElevated;
            return intel.PriceMultLow;
        }

        public long GetIntelUpgradeCost()
        {
            if (m_IntelUpgradeLevel >= MAX_INTEL_UPGRADE_LEVEL) return 0;
            return (m_IntelUpgradeLevel + 1) * BalanceConfig.Current.Intel.IntelUpgradeCostPerLevel;
        }

        // ============================================================================
        // Private Helpers
        // ============================================================================

        private int GetCitySizeMW()
        {
            if (m_PowerGridQuery.IsEmptyIgnoreFilter) return 0;
            if (!m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid)) return 0;

            // City SIZE from built nameplate (snapshot) — NOT live production: the ballistic estimate
            // tracks city size like the real wave. As a bonus this fixes the old S7-3 symptom at the
            // source — nameplate does not drop during a collapse, so the estimate no longer needs the
            // floor to avoid reading 0 (the floor is kept as a defensive minimum).
            int citySizeMW = WaveContextGatherer.ResolveCitySizeMW(grid.Production);
            return math.max(citySizeMW, BalanceConfig.Current.Waves.BallisticMinProductionMw);
        }

        private (int min, int max) ApplyNoise(float exactPercent)
        {
            var intelCfg = BalanceConfig.Current.Intel;
            float noise = m_HasInsider
                ? exactPercent * intelCfg.InsiderNoisePercent
                : exactPercent * intelCfg.NoisePercent;

            float rawMin = exactPercent - noise;
            float rawMax = exactPercent + noise;

            // Clamp each bound independently, then ensure minVal <= maxVal
            // (possible when exactPercent ~100 and noise is large: rawMin clamped, rawMax already at 100)
            int minVal = math.max(0, (int)Math.Round(rawMin));
            int maxVal = math.min(100, (int)Math.Round(rawMax));
            if (minVal > maxVal) minVal = maxVal;

            return (minVal, maxVal);
        }

        private int LogInvalidEndTime()
        {
            // Log once per session (prevents spam). After first log, silently returns 0.
            // UI shows TensionLevel=0 which is indistinguishable from pre-scenario, but PhaseEndTime=0
            // means the wave scheduler hasn't set a phase duration yet — expected on first Calm tick.
            if (!m_InvalidEndTimeLogged)
            {
                m_InvalidEndTimeLogged = true;
                Log.Warn($"PhaseEndTime is 0 during Calm phase (TimeInPhase={m_WaveState.TimeInPhase:F1}s) — TensionLevel will report {TENSION_INVALID_FALLBACK} until phase transitions");
            }
            return TENSION_INVALID_FALLBACK;
        }

        public void ResetState()
        {
            m_HasInsider = false;
            m_IntelUpgradeLevel = 0;
            m_InvalidEndTimeLogged = false; // CIVIC229 FIX
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            // W1 S096 — restored upgrade levels were silently lost when singleton missing.
            IntelStateSingleton.EnsureExists(EntityManager);
            Log.Info("State reset");
        }

        /// <summary>
        /// Sync-seed Step 2: publish reconciled IntelStateSingleton in PLVS Phase 2
        /// so AirDefenseUIPanel sees real predictions on the first post-load publish
        /// instead of the Default ("LOW" / "Unknown swarm size") that the singleton
        /// otherwise carries until the first throttled tick after unpause.
        /// Persisted state (m_HasInsider, m_IntelUpgradeLevel) is already restored by
        /// Deserialize at this point; UpdateSingleton derives the rest from WaveState
        /// and PowerGrid.
        /// </summary>
        public void ValidateAfterLoad()
        {
            IntelStateSingleton.EnsureExists(EntityManager);
            CacheSingletons();
            UpdateSingleton();
        }
    }
}
