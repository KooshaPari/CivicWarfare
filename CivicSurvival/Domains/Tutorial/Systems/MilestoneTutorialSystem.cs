using Colossal.Logging;
using Colossal.UI.Binding;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Spotters;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Systems;
using static CivicSurvival.Core.UI.B;

namespace CivicSurvival.Domains.Tutorial.Systems
{
    /// <summary>
    /// Manages 8 milestone tutorial modals that fire once per save.
    /// Uses ScenarioSingleton.ShownModals bitmask (persisted via ScenarioStateMachine serialization).
    ///
    /// Event-driven: WarStarted, DonorEvent, WaveEnded, CorruptionNarrative, ActChanged
    /// Poll-driven (throttled): SpotterAlert, GhostTown
    /// </summary>
    public partial class MilestoneTutorialSystem : CivicUISystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("MilestoneTutorial");

        // Cached ModalFlags → string (avoids ToString() allocation on each call)
#pragma warning disable CIVIC148 // Immutable compile-time lookup — no stale data possible
        private static readonly System.Collections.Generic.Dictionary<ModalFlags, string> s_FlagNames = new()
        {
            [ModalFlags.WarBegins] = nameof(ModalFlags.WarBegins),
            [ModalFlags.FirstDonorAid] = nameof(ModalFlags.FirstDonorAid),
            [ModalFlags.FirstSuccessfulDefense] = nameof(ModalFlags.FirstSuccessfulDefense),
            [ModalFlags.GeneratorEra] = nameof(ModalFlags.GeneratorEra),
            [ModalFlags.SpotterAlert] = nameof(ModalFlags.SpotterAlert),
            [ModalFlags.CorruptionOffer] = nameof(ModalFlags.CorruptionOffer),
            [ModalFlags.GhostTown] = nameof(ModalFlags.GhostTown),
            [ModalFlags.WhoStaysBehind] = nameof(ModalFlags.WhoStaysBehind),
        };
#pragma warning restore CIVIC148

        private static string FlagName(ModalFlags flag) =>
            s_FlagNames.TryGetValue(flag, out var name) ? name : flag.ToString();

        // ===== Entity Queries =====
        private EntityQuery m_ScenarioQuery;
        private EntityQuery m_SpotterQuery;

        // ===== Performance =====
        private float m_ThrottleTimer;
        private const float POLL_INTERVAL_SECONDS = 1.0f;
        private const float GHOST_TOWN_POPULATION_RATIO = 0.3f;

        // Queue for event-driven modals that couldn't show because slot was occupied
        private readonly System.Collections.Generic.List<ModalFlags> m_PendingModals = new();

        protected override void OnCreate()
        {
            base.OnCreate();

            // Dismiss triggers. Lifecycle wrap: Dismiss mutates persisted ModalFlags
            // on ScenarioSingleton. Without the gate, a JS call from cold-boot menu
            // would touch the last loaded save's modal state.
            AddBinding(new TriggerBinding(Group, DismissWarBegins,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.WarBegins))));
            AddBinding(new TriggerBinding(Group, DismissFirstDonorAid,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.FirstDonorAid))));
            AddBinding(new TriggerBinding(Group, DismissFirstSuccessfulDefense,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.FirstSuccessfulDefense))));
            AddBinding(new TriggerBinding(Group, DismissGeneratorEra,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.GeneratorEra))));
            AddBinding(new TriggerBinding(Group, DismissSpotterAlert,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.SpotterAlert))));
            AddBinding(new TriggerBinding(Group, DismissCorruptionOffer,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.CorruptionOffer))));
            AddBinding(new TriggerBinding(Group, DismissGhostTown,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.GhostTown))));
            AddBinding(new TriggerBinding(Group, DismissWhoStaysBehind,
                CivicGameLifecycle.GameplayOnly(() => Dismiss(ModalFlags.WhoStaysBehind))));

            // Entity queries — read ScenarioSingleton, the ECS projection of ScenarioStateMachine state.
            m_ScenarioQuery = GetEntityQuery(ComponentType.ReadOnly<ScenarioSingleton>());
            m_SpotterQuery = GetEntityQuery(ComponentType.ReadOnly<SpotterStatsSingleton>());

            // Event subscriptions
            SubscribeRequired<WarStartedEvent>(OnWarStarted);
            SubscribeRequired<DonorEvent>(OnDonorEvent);
            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);
            SubscribeRequired<CorruptionNarrativeEvent>(OnCorruptionNarrative);
            SubscribeRequired<ActChangedEvent>(OnActChanged);
            SubscribeRequired<ModalResetEvent>(OnModalReset);
            SubscribeRequired<ModalActivatedEvent>(OnModalActivated);


            Log.Info("[MilestoneTutorial] Created");
        }

        public void ValidateAfterLoad()
        {
            m_PendingModals.Clear();

            if (!m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var state))
                return;
            var currentAct = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                ? actSingleton.CurrentAct
                : Act.PreWar;

            EnqueueRecoverableModal(state, ModalFlags.WarBegins, currentAct >= Act.Crisis);
            EnqueueRecoverableModal(state, ModalFlags.WhoStaysBehind, currentAct >= Act.Exodus);

            EnqueueRecoverableModal(state, ModalFlags.FirstDonorAid, state.DonorAidReceived > 0);

#pragma warning disable CIVIC211 // Post-load recovery reads spotter aggregate; no write dependency
            EnqueueRecoverableModal(
                state,
                ModalFlags.SpotterAlert,
                m_SpotterQuery.TryGetSingleton<SpotterStatsSingleton>(out var stats) && stats.TotalCount > 0);
#pragma warning restore CIVIC211

            int currentPopulation = this.GetCitizenCount();
            EnqueueRecoverableModal(
                state,
                ModalFlags.GhostTown,
                state.PopulationPeak > 0
                    && currentPopulation > 0
                    && currentPopulation < state.PopulationPeak * GHOST_TOWN_POPULATION_RATIO);

            EnqueueRecoverableModal(
                state,
                ModalFlags.CorruptionOffer,
                SystemAPI.TryGetSingleton<CountermeasuresCoreFsm>(out var cm)
                    && cm.CurrentPhase >= CivicSurvival.Core.Types.CountermeasuresPhase.Suspicion);

            if (m_PendingModals.Count > 0)
                Log.Info($"[MilestoneTutorial] ValidateAfterLoad: re-enqueued {m_PendingModals.Count} pending modals");
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<WarStartedEvent>(OnWarStarted);
            UnsubscribeSafe<DonorEvent>(OnDonorEvent);
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            UnsubscribeSafe<CorruptionNarrativeEvent>(OnCorruptionNarrative);
            UnsubscribeSafe<ActChangedEvent>(OnActChanged);
            UnsubscribeSafe<ModalResetEvent>(OnModalReset);
            UnsubscribeSafe<ModalActivatedEvent>(OnModalActivated);

            base.OnDestroy();
        }

#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
        private void EnqueueRecoverableModal(ScenarioSingleton state, ModalFlags flag, bool triggerActive)
        {
            if (!triggerActive || state.HasShownModal(flag) || m_PendingModals.Contains(flag))
                return;

            m_PendingModals.Add(flag);
        }

        protected override void OnUpdateImpl()
        {
            if (!m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var state))
                return;
            var currentAct = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                ? actSingleton.CurrentAct
                : Act.PreWar;

            m_ThrottleTimer += UnityEngine.Time.deltaTime;
            if (m_ThrottleTimer < POLL_INTERVAL_SECONDS)
                return;
            m_ThrottleTimer = 0f;

            // Drain pending event-driven modals that were queued when slot was occupied
            if (m_PendingModals.Count > 0)
            {
                var flag = m_PendingModals[0];
                if (state.HasShownModal(flag))
                {
                    m_PendingModals.RemoveAt(0); // Already activated — discard
                }
                else
                {
                    if (TryShow(flag))
                        m_PendingModals.RemoveAt(0);
                }
                return; // One modal per poll cycle
            }

            // Recovery: re-check event-driven modals that may have been lost on save/load
            // (m_PendingModals is ephemeral — lost on load; events don't re-fire)
            // WarBegins: fires once from ScenarioStateMachine.StartWar() — never re-published
            if (currentAct >= Act.Crisis)
                _ = TryShow(ModalFlags.WarBegins);
            // WhoStaysBehind: fires once on Act.Exodus — CrisisActCoordinator re-publishes on load
            // but if m_PendingModals was lost, re-check here
            if (currentAct >= Act.Exodus)
                _ = TryShow(ModalFlags.WhoStaysBehind);

            // Poll: SpotterAlert
#pragma warning disable CIVIC211 // Tutorial triggers on spotter deployment — read-only
            if (m_SpotterQuery.TryGetSingleton<SpotterStatsSingleton>(out var stats))
#pragma warning restore CIVIC211
            {
                if (stats.TotalCount > 0)
                    _ = TryShow(ModalFlags.SpotterAlert);
            }

            // Poll: GhostTown — population < 30% of peak
            if (state.PopulationPeak > 0)
            {
                int currentPop = this.GetCitizenCount();
                if (currentPop > 0 && currentPop < state.PopulationPeak * GHOST_TOWN_POPULATION_RATIO)
                    _ = TryShow(ModalFlags.GhostTown);
            }
        }

        // ===== Event Handlers =====

        private void OnWarStarted(WarStartedEvent evt)
        {
            _ = TryShow(ModalFlags.WarBegins);
        }

        private void OnDonorEvent(DonorEvent evt)
        {
            switch (evt.Type)
            {
                case DonorEventType.AidPackageReceived:
                    _ = TryShow(ModalFlags.FirstDonorAid);
                    break;
                case DonorEventType.GeneratorsReceived:
                    _ = TryShow(ModalFlags.GeneratorEra);
                    break;
                default:
                    break;
            }
        }

        private void OnWaveEnded(WaveEndedEvent evt)
        {
            if (evt.Intercepted > evt.Hits)
                _ = TryShow(ModalFlags.FirstSuccessfulDefense);
        }

        private void OnCorruptionNarrative(CorruptionNarrativeEvent evt)
        {
            if (evt.Type == CorruptionNarrativeEventType.SuspicionRising)
                _ = TryShow(ModalFlags.CorruptionOffer);
        }

        private void OnActChanged(ActChangedEvent evt)
        {
            if (evt.NewAct == Act.Exodus)
                _ = TryShow(ModalFlags.WhoStaysBehind);
        }

        private void OnModalReset(ModalResetEvent evt)
        {
            m_PendingModals.Clear();
        }

        private void OnModalActivated(ModalActivatedEvent evt)
        {
            if (!TryFlagForId(evt.Id, out var flag))
                return;

            if (!m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var state) || state.HasShownModal(flag))
                return;

#pragma warning disable CIVIC244 // ModalShownEvent persists the milestone flag when coordinator activation is real
            EventBus.SafePublish(new ModalShownEvent(flag));
#pragma warning restore CIVIC244
            Log.Info($"[MilestoneTutorial] Activated {flag}");
        }

        // ===== Core Logic =====

        private bool TryShow(ModalFlags flag)
        {
            if (!m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var state))
                return false;

            if (state.HasShownModal(flag))
                return true; // Already shown — caller can remove from pending

            if (!ModalCoordinator.Instance.TryShow(FlagName(flag)))
            {
                // Slot occupied — queue for retry on next poll cycle
                if (!m_PendingModals.Contains(flag))
                    m_PendingModals.Add(flag);
                return false;
            }

            Log.Info($"[MilestoneTutorial] Requested {flag}");
            return true;
        }

        private void Dismiss(ModalFlags flag)
        {
            ModalCoordinator.Instance.Dismiss(FlagName(flag));
        }

        private static bool TryFlagForId(string id, out ModalFlags flag)
        {
            foreach (var pair in s_FlagNames)
            {
                if (pair.Value == id)
                {
                    flag = pair.Key;
                    return true;
                }
            }

            flag = default;
            return false;
        }
#pragma warning restore CIVIC098
    }
}
