using System;
using Game.Simulation;
using Unity.Entities;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Scenario.Systems
{
    /// <summary>
    /// Checks defeat conditions every ~1 second.
    /// Defeat triggers when:
    ///   1. Population falls below absolute threshold (e.g., 100 citizens)
    ///   2. Average cognitive integrity stays below threshold for N hours
    ///
    /// Skips checks if war hasn't started or PostVictoryMode == Endless.
    /// Publishes GameOverEvent on defeat.
    /// </summary>
    [ActIndependent]
    public partial class DefeatCheckSystem : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("DefeatCheckSystem");

        private const float THROTTLE_SECONDS = 1.0f;
        private const int DEFEAT_GRACE_DAYS = 3;
        [NonSerialized] private float m_AccumulatedTime;

        // Local state (serialized — only system-internal tracking)
        private float m_IntegrityBelowThresholdHours;

        // FIX S5-07: Track act to reset defeat timer on Crisis exit.
        // Synced from StateMachine in Deserialize (loaded game) and ValidatePostLoad (new game).
        // Reset guard: m_IntegrityBelowThresholdHours > 0f makes missed transitions harmless.
        [NonSerialized] private Act m_LastCheckedAct;

        // FIX S7-03: Track PostVictoryMode to reset defeat timer on OneMoreYear
#pragma warning disable CIVIC221 // Ephemeral — re-derived from StateMachine on load (Deserialize syncs)
        private PostVictoryMode m_LastPostVictoryMode;
#pragma warning restore CIVIC221

        // Dependencies — managed system ref, not serialized
        [System.NonSerialized] private ScenarioStateMachine m_StateMachine = null!;
        private ComponentLookup<CognitiveState> m_CogWarfareLookup;
        private BufferLookup<CognitiveIntegrityBuffer> m_CogIntegrityBufferLookup;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CogWarfareLookup = GetComponentLookup<CognitiveState>(true);
            m_CogIntegrityBufferLookup = GetBufferLookup<CognitiveIntegrityBuffer>(true);


            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_StateMachine ??= FeatureRegistry.Instance.Require<ScenarioStateMachine>();
        }

        protected override void OnUpdateImpl()
        {
            m_CogWarfareLookup.Update(this);
            m_CogIntegrityBufferLookup.Update(this);

            if (m_StateMachine.IsDefeated) return;

            // Throttle to ~1Hz (game-time aware — respects speed multiplier)
            m_AccumulatedTime += SystemAPI.Time.DeltaTime;
            if (m_AccumulatedTime < THROTTLE_SECONDS) return;
            float elapsedSeconds = Math.Min(m_AccumulatedTime, THROTTLE_SECONDS * 2f);
            m_AccumulatedTime = 0f;

            // Skip if war hasn't started
            if (m_StateMachine.WarDay < 1) return;

            // Skip if Endless mode
            if (m_StateMachine.PostVictoryMode == PostVictoryMode.Endless) return;

            // FIX S7-03: Reset defeat timer when player chooses OneMoreYear
            if (m_StateMachine.PostVictoryMode != m_LastPostVictoryMode)
            {
                m_LastPostVictoryMode = m_StateMachine.PostVictoryMode;

                if (m_StateMachine.PostVictoryMode == PostVictoryMode.OneMoreYear && m_IntegrityBelowThresholdHours > 0f)
                {
                    Log.Info($"OneMoreYear selected: defeat timer reset ({m_IntegrityBelowThresholdHours:F1}h → 0)");
                    m_IntegrityBelowThresholdHours = 0f;
                }
            }

            // FIX S5-07: Reset integrity defeat timer on act transition from Crisis.
            // Player survived the crisis → fresh start. Without this, timer accumulated
            // during Crisis bleeds into Adaptation, causing defeat after "successful" survival.
            // W4-LATENT2 FIX: Read managed CurrentAct (always fresh) instead of ECS singleton (up to 1s stale).
            var currentAct = m_StateMachine.CurrentAct;
            if (currentAct != m_LastCheckedAct)
            {
                Act previousAct = m_LastCheckedAct;
                m_LastCheckedAct = currentAct;

                if (previousAct == Act.Crisis && m_IntegrityBelowThresholdHours > 0f)
                {
                    Log.Info($"Act transition Crisis → {currentAct}: defeat timer reset ({m_IntegrityBelowThresholdHours:F1}h → 0)");
                    m_IntegrityBelowThresholdHours = 0f;
                }
            }

            var config = BalanceConfig.Current.Scenario;

            // Check 1: Population collapse (grace period for small villages at war start)
            int citizenCount = this.GetCitizenCount();
            int populationThreshold = GetPopulationDefeatThreshold(config.DefeatPopulationThreshold);
            if (citizenCount < populationThreshold && m_StateMachine.WarDay > DEFEAT_GRACE_DAYS)
            {
                TriggerDefeat(DefeatCause.PopulationCollapse);
                return;
            }

            // Check 2: Cognitive integrity below threshold for extended period
            float avgIntegrity = GetAverageIntegrity();
            if (avgIntegrity >= 0f) // -1 means CognitiveWarfare not active
            {
                if (avgIntegrity < config.DefeatIntegrityThreshold)
                {
                    // Accumulate hours below threshold (game-time aware)
                    float deltaHours = GameRate.HoursDelta(elapsedSeconds);
                    m_IntegrityBelowThresholdHours += deltaHours;

                    if (m_IntegrityBelowThresholdHours >= config.DefeatIntegrityHours)
                    {
                        TriggerDefeat(DefeatCause.LostControl);
                    }
                }
                else
                {
                    // Reset timer when integrity recovers
                    m_IntegrityBelowThresholdHours = 0f;
                }
            }
        }

        private int GetPopulationDefeatThreshold(int configuredThreshold)
        {
            int originalPopulation = m_StateMachine.State.OriginalPopulation;
            if (originalPopulation <= 0)
                return configuredThreshold;

            int scaledThreshold = Math.Max(1, (int)Math.Round(originalPopulation * 0.1f));
            return Math.Min(configuredThreshold, scaledThreshold);
        }

        /// <summary>
        /// Get average cognitive integrity across all districts.
        /// Returns -1 if CognitiveWarfare is not active.
        /// </summary>
        private float GetAverageIntegrity()
        {
#pragma warning disable CIVIC070 // Integrity changes gradually; 1-frame lag invisible for defeat checks
            if (!SystemAPI.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
#pragma warning restore CIVIC070
                return -1f;

            if (!m_CogWarfareLookup.TryGetComponent(stateEntity, out var state) || !state.IsActive)
                return -1f;

            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var buffer))
                return -1f;
            if (buffer.Length == 0)
                return -1f;

            float total = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                total += buffer[i].Integrity;
            }

            return total / buffer.Length;
        }

        private void TriggerDefeat(DefeatCause cause)
        {
            m_StateMachine.SetDefeated(cause);

            int daysSurvived = m_StateMachine.WarDay;
            EventBus?.SafePublish(new GameOverEvent(cause, daysSurvived), "DefeatCheckSystem");

            Log.Info($"DEFEAT: {cause}, survived {daysSurvived} days");
        }

        /// <summary>
        /// S10-01: Force immediate defeat check after load.
        /// Sets accumulator to throttle threshold so next OnUpdateImpl fires immediately.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_StateMachine ??= FeatureRegistry.Instance.Require<ScenarioStateMachine>();
            m_AccumulatedTime = THROTTLE_SECONDS;

            // Sync act tracking regardless of deserialization order
            m_LastCheckedAct = m_StateMachine.CurrentAct;
            m_LastPostVictoryMode = m_StateMachine.PostVictoryMode;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }
    }
}
