using Colossal.Logging;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using static CivicSurvival.Core.UI.B;

namespace CivicSurvival.Domains.Tutorial.Systems
{
    /// <summary>
    /// Crisis-act tutorial system. Two distinct responsibilities:
    ///
    /// <para><b>1. Modal tutorials (event-driven, real UI):</b></para>
    /// Owns FirstStrike (after first wave) and ExodusWarning (population drop)
    /// modals. Both have live UI components (FirstStrikeModal.tsx,
    /// ExodusWarningModal.tsx) and go through ModalCoordinator. One-shot per
    /// Crisis act.
    ///
    /// <para><b>2. Tab-open analytics (telemetry-only, no UI):</b></para>
    /// Records the first GRID/SHADOW tab open during Crisis as telemetry events
    /// (grid_tab_first_open, shadow_tab_first_open) for the analytics server.
    /// Pre-Crisis tab opens are remembered and counted on Crisis entry, so the
    /// analytics signal is not lost if the player visited the tab before war
    /// started.
    /// No UI modal is shown — what was once a tutorial modal here was removed
    /// in commit 76e060de9 in favour of unified per-section "?" HelpSection
    /// portals (handled by HelpStateSystem, independent of this system).
    ///
    /// <para><b>Update Order:</b></para>
    /// UIUpdate reads scenario state after the GameSimulation scenario systems
    /// have published their current frame state.
    /// </summary>
    public partial class CrisisTutorialSystem : CivicUISystemBase, IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CrisisTutorial");

        // ===== Modal State (event-driven, real UI) =====
        private bool m_FirstStrikeShown;
        private bool m_ExodusWarningShown;

        // ===== Tab-Open Analytics State (telemetry-only, no UI modal) =====
        // Set true on first GRID/SHADOW tab open during Crisis. Reset per Crisis act.
        private bool m_GridTabOpenedInCrisis;
        private bool m_ShadowTabOpenedInCrisis;

        // Pre-Crisis tab opens are remembered and counted on Crisis entry, so
        // the analytics signal is not lost if the player visited the tab before
        // war started.
        private bool m_GridTabOpenedPreCrisis;
        private bool m_ShadowTabOpenedPreCrisis;

        // ===== Crisis Act Tracking =====
        private bool m_CrisisActActive;
        private int m_CrisisStartDay;
        private int m_PopulationAtCrisisStart;
        private bool m_FirstWaveEnded;
        private bool m_FirstWaveCausedDamage;

        // PERF: Throttle modal checks (GetCitizenCount is expensive!)
        private float m_ThrottleTimer;
        private const float CHECK_INTERVAL_SECONDS = 1.0f;

        // Telemetry: track modal show time for dismiss duration (ephemeral — not serialized)
        [System.NonSerialized] private float m_ModalShowTime;
        [System.NonSerialized] private string m_ActiveModalName = "";
        [System.NonSerialized] private bool m_NeedBootDefaultCrisisRestore;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Modal dismiss triggers (FirstStrike, ExodusWarning have live UI modals)
            // Lifecycle wrap: DismissModal mutates persisted ScenarioSingleton.
            AddBinding(new TriggerBinding(Group, DismissFirstStrike,
                CivicGameLifecycle.GameplayOnly(() => DismissModal("FirstStrike"))));
            AddBinding(new TriggerBinding(Group, DismissExodusWarning,
                CivicGameLifecycle.GameplayOnly(() => DismissModal("ExodusWarning"))));

            // Tab-open analytics signals from UI (Dashboard's TutorialTabSignals component)
            // Lifecycle wrap: tab-open triggers nudge gameplay UI panels; ignore in menu.
            AddBinding(new TriggerBinding(Group, Core.UI.B.OnOpenGridTab,
                CivicGameLifecycle.GameplayOnly(OnOpenGridTab)));
            AddBinding(new TriggerBinding(Group, Core.UI.B.OnOpenShadowTab,
                CivicGameLifecycle.GameplayOnly(OnOpenShadowTab)));

            // Subscribe to orchestration events
            SubscribeRequired<ActChangedEvent>(OnActChanged);
            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);
            SubscribeRequired<ModalActivatedEvent>(OnModalActivated);

            Log.Info("[CrisisTutorial] Created");
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<ActChangedEvent>(OnActChanged);
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            UnsubscribeSafe<ModalActivatedEvent>(OnModalActivated);

            base.OnDestroy();
        }

        /// <summary>
        /// React to act changes — initialize Crisis state when entering Crisis
        /// act, emit telemetry summary and reset state on exit.
        /// </summary>
        private void OnActChanged(ActChangedEvent evt)
        {
#pragma warning disable CIVIC102 // Tutorial only reacts to Crisis act, other acts no-op
            if (evt.NewAct == Act.Crisis)
#pragma warning restore CIVIC102
            {
                bool wasCrisis = m_CrisisActActive;
                m_CrisisActActive = true;

                // Idempotency: skip re-init on post-load re-publish (flags already deserialized)
                if (wasCrisis) return;

                var timeProvider = GameTimeSystem.Instance;
                if (m_CrisisStartDay <= 0)
                    m_CrisisStartDay = timeProvider != null ? timeProvider.Current.CurrentDay : 0;
                if (m_PopulationAtCrisisStart <= 0 && !TryCaptureCrisisStartPopulation())
                    Log.Debug("[CrisisTutorial] Crisis population baseline not ready; retrying later");
                m_FirstWaveEnded = false;
                m_FirstWaveCausedDamage = false;

                // Reset per-Crisis-act state
                m_FirstStrikeShown = false;
                m_ExodusWarningShown = false;
                m_GridTabOpenedInCrisis = false;
                m_ShadowTabOpenedInCrisis = false;

                Log.Info($"[CrisisTutorial] Crisis act started — scheduling tutorials (Day {m_CrisisStartDay}, Pop {m_PopulationAtCrisisStart})");

                // Pre-Crisis tab opens count toward in-Crisis analytics: if the
                // player visited GRID/SHADOW before war started, "first open in
                // Crisis" still fires on entry rather than being lost.
                if (m_GridTabOpenedPreCrisis)
                {
                    m_GridTabOpenedPreCrisis = false;
                    if (!m_GridTabOpenedInCrisis)
                    {
                        Log.Info("[CrisisTutorial] Pre-Crisis GRID tab open → counting on Crisis entry");
                        RecordFirstGridTabOpen();
                    }
                }
                if (m_ShadowTabOpenedPreCrisis)
                {
                    m_ShadowTabOpenedPreCrisis = false;
                    if (!m_ShadowTabOpenedInCrisis)
                    {
                        Log.Info("[CrisisTutorial] Pre-Crisis SHADOW tab open → counting on Crisis entry");
                        RecordFirstShadowTabOpen();
                    }
                }
            }
            else if (evt.PreviousAct == Act.Crisis)
            {
                m_CrisisActActive = false;

                // Telemetry: emit crisis tutorial summary before reset
                int stepsShown = (m_FirstStrikeShown ? 1 : 0) + (m_ExodusWarningShown ? 1 : 0)
                               + (m_GridTabOpenedInCrisis ? 1 : 0) + (m_ShadowTabOpenedInCrisis ? 1 : 0);
                int crisisDays = 0;
                var gts = GameTimeSystem.Instance;
                if (gts != null && m_CrisisStartDay > 0)
                    crisisDays = gts.Current.CurrentDay - m_CrisisStartDay;
                EventBus?.SafePublish(new TutorialCrisisSummaryEvent(stepsShown, 4, crisisDays));

                // BUG-T-007 FIX: Reset modal state for potential re-entry
                m_FirstStrikeShown = false;
                m_ExodusWarningShown = false;
                m_GridTabOpenedInCrisis = false;
                m_ShadowTabOpenedInCrisis = false;
                m_FirstWaveEnded = false;
                m_FirstWaveCausedDamage = false;
                m_PopulationAtCrisisStart = 0;
                m_GridTabOpenedPreCrisis = false;
                m_ShadowTabOpenedPreCrisis = false;

                // Scoped dismiss: only Crisis tutorial modal ids that have real
                // UI. Tab-open analytics (Grid/Shadow) have no modal to dismiss.
#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
                ModalCoordinator.Instance.DismissMany("FirstStrike", "ExodusWarning");
#pragma warning restore CIVIC098

                Log.Info("[CrisisTutorial] Crisis act ended — tutorial system reset");
            }
        }

        /// <summary>
        /// React to wave completion — trigger First Strike modal after first wave ends.
        /// </summary>
        private void OnWaveEnded(WaveEndedEvent evt)
        {
            if (!m_CrisisActActive) return;
            if (m_FirstWaveEnded) return;

            m_FirstWaveEnded = true;
            m_FirstWaveCausedDamage = evt.HadDamage;
            Log.Info($"[CrisisTutorial] First wave ended (wave {evt.WaveNumber}, hits: {evt.Hits}, intercepted: {evt.Intercepted}, damaged: {m_FirstWaveCausedDamage})");

            // Claim the modal slot on the SAME event Debriefing reacts to, so
            // FirstStrike (priority 60) is active before ThreatUISystem's UIUpdate
            // tick calls TryShow("Debriefing") (priority 10). Debriefing then queues
            // and shows once after FirstStrike is dismissed — no flicker, no
            // double-show. Previously FirstStrike fired from the 1s-throttled
            // CheckModalTriggers poll, which let Debriefing show first, get evicted,
            // and re-show (Debriefing → FirstStrike → Debriefing churn).
            if (m_FirstWaveCausedDamage && !m_FirstStrikeShown)
                ShowFirstStrikeModal();
        }

        protected override void OnUpdateImpl()
        {
            if (!m_CrisisActActive)
                return;

            // PERF: Throttle — GetCitizenCount() in CheckModalTriggers is expensive!
            m_ThrottleTimer += UnityEngine.Time.deltaTime;
            if (m_ThrottleTimer < CHECK_INTERVAL_SECONDS) return;
            m_ThrottleTimer = 0f;

            CheckModalTriggers();
        }

        public void ValidateAfterLoad()
        {
            if (m_NeedBootDefaultCrisisRestore)
            {
                m_NeedBootDefaultCrisisRestore = false;

                // W2-REG-055 self-heal restored: boot reset is field-only; ECS
                // act/baseline repair waits for post-load validation.
                bool crisisActive = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                    && actSingleton.CurrentAct == Act.Crisis;
                m_CrisisActActive = crisisActive;
                if (crisisActive)
                {
                    var timeProvider = GameTimeSystem.Instance;
                    if (m_CrisisStartDay <= 0)
                        m_CrisisStartDay = timeProvider != null ? timeProvider.Current.CurrentDay : 0;
                    if (m_PopulationAtCrisisStart <= 0 && !TryCaptureCrisisStartPopulation())
                        Log.Debug("[CrisisTutorial] Post-load population baseline not ready; retrying later");

                    Log.Info($"[CrisisTutorial] Boot-default fallback restored after load: CrisisActive={m_CrisisActActive}, Pop={m_PopulationAtCrisisStart}");
                }
            }

            // FirstStrike lost across save/load: the modal queue is not persisted
            // (ModalCoordinator resets on load). If the save landed between first-wave
            // end and FirstStrike activation (m_FirstStrikeShown flips only on
            // ModalActivatedEvent), re-fire it here. Previously covered by the
            // CheckModalTriggers poll, which now no longer owns FirstStrike.
            if (m_CrisisActActive && m_FirstWaveEnded && m_FirstWaveCausedDamage && !m_FirstStrikeShown)
            {
                Log.Info("[CrisisTutorial] Post-load: re-showing FirstStrike lost with the modal queue");
                ShowFirstStrikeModal();
            }
        }

        /// <summary>
        /// Check and trigger event-based modals (FirstStrike, ExodusWarning).
        /// Tab-open analytics fire from OnOpenGridTab/OnOpenShadowTab signals.
        /// </summary>
        private void CheckModalTriggers()
        {
            // FirstStrike is shown immediately in OnWaveEnded (same event Debriefing
            // reacts to), not polled here — see OnWaveEnded for the ordering rationale.

            // ExodusWarning - when population drops (event-based, keep)
            float startPop = m_PopulationAtCrisisStart;
            if (startPop <= 0f && !TryCaptureCrisisStartPopulation())
                return;

            startPop = m_PopulationAtCrisisStart;
            if (!m_ExodusWarningShown && startPop > 0f)
            {
                int currentPopulation = this.GetCitizenCount();
                if (currentPopulation <= 0)
                    return;

                float populationLost = (startPop - currentPopulation) / startPop;
                if (populationLost >= BalanceConfig.Current.Scenario.ExodusWarningThreshold)
                {
                    ShowExodusWarningModal();
                }
            }
        }

        private bool TryCaptureCrisisStartPopulation()
        {
            int population = this.GetCitizenCount();
            if (population <= 0)
                return false;

            m_PopulationAtCrisisStart = population;
            return true;
        }

        // ===== Tab-Open Analytics =====

        /// <summary>
        /// Called when the player switches to the GRID tab. Records "first open
        /// in Crisis" telemetry; pre-Crisis opens are remembered for replay on
        /// Crisis entry.
        /// </summary>
        private void OnOpenGridTab()
        {
            if (m_GridTabOpenedInCrisis) return;
            if (!m_CrisisActActive)
            {
                m_GridTabOpenedPreCrisis = true;
                return;
            }
            RecordFirstGridTabOpen();
        }

        /// <summary>
        /// Called when the player switches to the SHADOW tab. Records "first
        /// open in Crisis" telemetry; pre-Crisis opens are remembered for
        /// replay on Crisis entry.
        /// </summary>
        private void OnOpenShadowTab()
        {
            if (m_ShadowTabOpenedInCrisis) return;
            if (!m_CrisisActActive)
            {
                m_ShadowTabOpenedPreCrisis = true;
                return;
            }
            RecordFirstShadowTabOpen();
        }

#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
        // ===== Modal Display Methods =====

        private void ShowFirstStrikeModal()
        {
            if (!ModalCoordinator.Instance.TryShow("FirstStrike"))
                return;
            Log.Info("[CrisisTutorial] Requested First Strike modal");
        }

        private void ShowExodusWarningModal()
        {
            if (!ModalCoordinator.Instance.TryShow("ExodusWarning"))
                return;
            Log.Info("[CrisisTutorial] Requested Exodus Warning modal");
        }

        // ===== Tab-Open Analytics Recording (no UI modal) =====

        private void RecordFirstGridTabOpen()
        {
            m_GridTabOpenedInCrisis = true;
            Log.Info("[CrisisTutorial] First GRID tab open in Crisis — analytics recorded");
            RecordStepShown("grid_tab_first_open", "tab_open");
        }

        private void RecordFirstShadowTabOpen()
        {
            m_ShadowTabOpenedInCrisis = true;
            Log.Info("[CrisisTutorial] First SHADOW tab open in Crisis — analytics recorded");
            RecordStepShown("shadow_tab_first_open", "tab_open");
        }

        private void OnModalActivated(ModalActivatedEvent evt)
        {
            if (evt.Id == "FirstStrike")
            {
                if (m_FirstStrikeShown) return;
                m_FirstStrikeShown = true;
                Log.Info("[CrisisTutorial] Activated First Strike modal");
                RecordStepShown("first_strike", "event");
            }
            else if (evt.Id == "ExodusWarning")
            {
                if (m_ExodusWarningShown) return;
                m_ExodusWarningShown = true;
                Log.Info("[CrisisTutorial] Activated Exodus Warning modal");
                RecordStepShown("exodus_warning", "poll");
            }
        }

        private void DismissModal(string modalId)
        {
            ModalCoordinator.Instance.Dismiss(modalId);

            // Telemetry: record dismiss with time spent
            if (!string.IsNullOrEmpty(m_ActiveModalName))
            {
                float elapsed = UnityEngine.Time.realtimeSinceStartup - m_ModalShowTime;
                EventBus?.SafePublish(new TutorialStepDismissedEvent(m_ActiveModalName, elapsed));
                m_ActiveModalName = "";
            }
        }

        private void RecordStepShown(string stepName, string trigger)
        {
            if (trigger != "tab_open")
            {
                m_ActiveModalName = stepName;
                m_ModalShowTime = UnityEngine.Time.realtimeSinceStartup;
            }
            EventBus?.SafePublish(new TutorialStepEvent(stepName, trigger));
        }

        /// <summary>
        /// BUG-T-001 FIX: Reset all serializable state to defaults.
        /// Called on new game and when save version is incompatible.
        /// </summary>
        public void ResetState()
        {
            m_FirstStrikeShown = false;
            m_ExodusWarningShown = false;
            m_GridTabOpenedInCrisis = false;
            m_ShadowTabOpenedInCrisis = false;
            m_GridTabOpenedPreCrisis = false;
            m_ShadowTabOpenedPreCrisis = false;
            m_CrisisActActive = false;
            m_CrisisStartDay = 0;
            m_PopulationAtCrisisStart = 0;
            m_FirstWaveEnded = false;
            m_FirstWaveCausedDamage = false;
            m_ThrottleTimer = 0f;
            m_ModalShowTime = 0f;
            m_ActiveModalName = "";
            m_NeedBootDefaultCrisisRestore = false;
            ModalCoordinator.Instance.Reset();

            Log.Info("[CrisisTutorial] State reset");
        }

        public void ResetToBootDefaults(ResetReason reason)
        {
            m_FirstStrikeShown = false;
            m_ExodusWarningShown = false;
            m_GridTabOpenedInCrisis = false;
            m_ShadowTabOpenedInCrisis = false;
            m_GridTabOpenedPreCrisis = false;
            m_ShadowTabOpenedPreCrisis = false;
            m_CrisisActActive = false;
            m_CrisisStartDay = 0;
            m_PopulationAtCrisisStart = 0;
            m_FirstWaveEnded = false;
            m_FirstWaveCausedDamage = false;
            m_ThrottleTimer = 0f;
            m_ModalShowTime = 0f;
            m_ActiveModalName = "";
            m_NeedBootDefaultCrisisRestore = false;

            Log.Info($"[BOOT-RESET] CrisisTutorialSystem reason={reason} PopulationAtCrisisStart={m_PopulationAtCrisisStart} FirstWaveCausedDamage={m_FirstWaveCausedDamage} FirstStrikeShown={m_FirstStrikeShown} ExodusWarningShown={m_ExodusWarningShown} CrisisActActive={m_CrisisActActive}");
        }
#pragma warning restore CIVIC098
    }
}
