using Game;
using Game.Simulation;
using Unity.Entities;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;

namespace CivicSurvival.Domains.Scenario.Systems
{
    /// <summary>
    /// Handles scenario milestone modals (War Fatigue, Victory).
    /// Checks conditions and triggers modals at appropriate times.
    /// Serializes modal state to prevent re-showing after load.
    /// </summary>
    [ActIndependent]
    public partial class ScenarioMilestonesSystem : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private const int VICTORY_YEAR_DAYS = 365;

        private static readonly LogContext Log = new("ScenarioMilestonesSystem");

        private bool m_WarFatigueShown;
        private bool m_WarFatigueDismissed;
        private bool m_VictoryShown;

        // Post-victory state (serialized via StateMachine.PostVictoryMode)
        private int m_VictoryTargetDay;
        private bool m_VictoryDismissed;
        private int m_OneMoreYearCount;

        // Dependencies (initialized in OnStartRunning, not serialized)
        [System.NonSerialized] private ScenarioStateMachine m_StateMachine = null!;
        [System.NonSerialized] private ScenarioStatisticsSystem m_Statistics = null!;
        [System.NonSerialized] private EntityQuery m_CountermeasuresQuery;
        [System.NonSerialized] private bool m_NeedOneMoreYearSelfHeal;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CountermeasuresQuery = GetEntityQuery(ComponentType.ReadOnly<CountermeasuresCoreFsm>());
            m_VictoryTargetDay = BalanceConfig.Current.Scenario.VictoryDays;

            // PERF: All work is event-driven — no per-frame polling needed
            SubscribeBufferedUntilReady<WarDayChangedEvent>(OnWarDayChanged);
            SubscribeBufferedUntilReady<ScenarioTypeDetectedEvent>(OnPostLoad);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_StateMachine ??= FeatureRegistry.Instance.Require<ScenarioStateMachine>();
            m_Statistics ??= FeatureRegistry.Instance.Require<ScenarioStatisticsSystem>();
            MarkEventHandlersReady();
        }

        protected override void OnUpdateImpl()
        {
            // PERF: All work is event-driven via OnWarDayChanged — nothing to do per-frame
        }

        public void ValidateAfterLoad()
        {
            // W2-REG-044 self-heal restored: cross-system OneMoreYear repair
            // runs after all deserialize work, not inside boot reset/deserialize.
            if (m_NeedOneMoreYearSelfHeal)
                ReconcileOneMoreYearAfterLoad();
        }

        private void ReconcileOneMoreYearAfterLoad()
        {
            int configDays = BalanceConfig.Current.Scenario.VictoryDays;
            if (m_StateMachine.PostVictoryMode == PostVictoryMode.OneMoreYear
                && m_OneMoreYearCount == 0)
            {
                m_OneMoreYearCount = 1;
                m_VictoryShown = true;
                m_VictoryDismissed = true;
                Log.Warn("Post-victory state drift detected after load: mode=OneMoreYear but count=0; self-healed to count=1");
            }

            int expectedTarget = configDays + m_OneMoreYearCount * VICTORY_YEAR_DAYS;
            if (m_VictoryTargetDay != expectedTarget)
            {
                Log.Info($"VictoryTargetDay recalculated after load: {m_VictoryTargetDay} -> {expectedTarget} (config={configDays}, extensions={m_OneMoreYearCount})");
                m_VictoryTargetDay = expectedTarget;
            }

            m_NeedOneMoreYearSelfHeal = false;
        }

        /// <summary>
        /// Handle WarDayChangedEvent — check milestone thresholds once per game day.
        /// Replaces per-frame polling with event-driven approach.
        /// </summary>
        private void OnWarDayChanged(WarDayChangedEvent evt)
        {
            // S18b-5 FIX: Only check milestones during Crisis act (WarDay=0 during PreWar could false-trigger)
            if (m_StateMachine.CurrentAct < Act.Crisis)
                return;

            int warDay = evt.WarDay;

            // War Fatigue - Day 180
            int warFatigueDay = BalanceConfig.Current.Scenario.WarFatigueDay;
            if (!m_WarFatigueShown && warDay >= warFatigueDay)
            {
                ShowWarFatigueModal();
                return;
            }

            // Victory - Day 365 (or next target after OneMoreYear)
            // S18b-8 FIX: Use day check instead of fragile m_WarFatigueShown dependency.
            // If serialization loses the flag, victory was permanently blocked.
            // Day >= WarFatigueDay is equivalent — if they survived past 180, fatigue was shown or should have been.
            bool pastWarFatigue = m_WarFatigueShown || warDay >= warFatigueDay;
            if (!m_VictoryShown && !m_StateMachine.IsDefeated
                && pastWarFatigue && warDay >= m_VictoryTargetDay)
            {
                if (CheckVictoryConditions())
                {
                    ShowVictoryModal();
                }
            }
        }

        private bool CheckVictoryConditions()
        {
            // Victory conditions:
            // 1. Survived VictoryDays (checked by caller)
            // 2. Population above VictoryMinPopulation of peak
            // 3. Defended at least VictoryMinWaves waves
            // 4. Corruption below VictoryMaxCorruption

            var config = BalanceConfig.Current.Scenario;

            int currentPop = this.GetCitizenCount();
            int peakPop = m_StateMachine.PeakPopulation;

            if (peakPop <= 0) return false;

            bool populationHealthy = currentPop >= (peakPop * config.VictoryMinPopulation);
            bool wavesDefended = m_Statistics.TotalWavesDefended >= config.VictoryMinWaves;
            bool corruptionOk = true;

            if (m_CountermeasuresQuery.TryGetSingleton<CountermeasuresCoreFsm>(out var cm))
                corruptionOk = cm.CorruptionScore < config.VictoryMaxCorruption;

            return populationHealthy && wavesDefended && corruptionOk;
        }

        private void ShowWarFatigueModal()
        {
            m_WarFatigueShown = true;

            EventBus?.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.Milestone180.ToKey()), "ScenarioMilestones");

            Log.Info("War Fatigue modal shown (Day 180)");
        }

        private void ShowVictoryModal()
        {
            m_VictoryShown = true;

            EventBus?.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.MilestoneVictory.ToKey()), "ScenarioMilestones");

            Log.Info("Victory modal shown (Day 365)");
        }

        // ===== Public API =====

        public bool WarFatigueShown => m_WarFatigueShown;
        public bool WarFatigueDismissed => m_WarFatigueDismissed;
        public bool VictoryShown => m_VictoryShown;
        public bool VictoryDismissed => m_VictoryDismissed;
        public int VictoryTargetDay => m_VictoryTargetDay;

        public void DismissWarFatigue()
        {
            m_WarFatigueDismissed = true;
        }

        /// <summary>
        /// Called by ScenarioUISystem when player picks a post-victory option.
        /// </summary>
        public void SetPostVictoryMode(PostVictoryMode mode)
        {
            m_StateMachine.SetPostVictoryMode(mode);
            m_VictoryDismissed = true;

#pragma warning disable CIVIC102 // Only OneMoreYear/Continue have logic; Quit handled elsewhere
            if (mode == PostVictoryMode.OneMoreYear)
#pragma warning restore CIVIC102
            {
#pragma warning disable CIVIC226 // Player-initiated, one-shot per "One More Year"
                m_OneMoreYearCount++;
                m_VictoryTargetDay = BalanceConfig.Current.Scenario.VictoryDays + m_OneMoreYearCount * VICTORY_YEAR_DAYS;
#pragma warning restore CIVIC226
                m_VictoryShown = false;
                m_VictoryDismissed = false;
                Log.Info($"One More Year: next victory at day {m_VictoryTargetDay}");
            }
            else if (mode == PostVictoryMode.Endless)
            {
                Log.Info("Endless Mode: no more victory/defeat checks");
            }
        }

        private void OnPostLoad(ScenarioTypeDetectedEvent evt)
        {
            // Re-check WarFatigue after load (WarDayChangedEvent won't fire until next midnight).
            // FIX W7-M5: Skip victory check — GetCitizenCount may return 0 before entities are fully loaded.
            // Victory will be checked on next real WarDayChangedEvent at midnight.
            int warDay = m_StateMachine.WarDay;
            if (warDay > 0 && !m_WarFatigueShown && warDay >= BalanceConfig.Current.Scenario.WarFatigueDay)
            {
                ShowWarFatigueModal();
            }

            // Do not re-check victory on load: post-victory state can be self-healed
            // during deserialize, and WarDayChanged will handle the next real boundary.
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<WarDayChangedEvent>(OnWarDayChanged);
            UnsubscribeSafe<ScenarioTypeDetectedEvent>(OnPostLoad);
            base.OnDestroy();
        }
    }
}
