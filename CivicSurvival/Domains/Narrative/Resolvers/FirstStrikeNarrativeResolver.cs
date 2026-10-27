using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Domains.Narrative.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves First Strike cascade events into narrative notifications.
    /// Handles satirical reactions to catastrophic power plant targeting.
    /// No batching needed: the upstream CrisisActCoordinator persists m_CrisisActive
    /// and publishes this event only on the transition into the crisis act.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class FirstStrikeNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }

        public string Domain => "FirstStrike";

        private static readonly LogContext Log = new("FirstStrikeNarrativeResolver");

        private readonly NotificationState m_Sink;
        private IEventBus? m_EventBus;

        public FirstStrikeNarrativeResolver(NotificationState sink)
        {
            m_Sink = sink;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<FirstStrikeCascadeEvent>(OnFirstStrikeCascade);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<FirstStrikeCascadeEvent>(OnFirstStrikeCascade);
            m_EventBus = null;
        }

        /// <summary>
        /// Handle First Strike cascade planned event.
        /// Generate satirical narrative about catastrophic targeting.
        /// </summary>
        private void OnFirstStrikeCascade(FirstStrikeCascadeEvent evt)
        {
            // System alert (toast): keeps the ephemeral TimedId + sink/cooldown path.
            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId("first_strike_alert"),
                LocalizationManager.Get("NOTIFY_TITLE_FIRST_STRIKE"),
                LocalizationManager.Get("NOTIFY_FIRST_STRIKE_MSG", evt.PlannedHits),
                Status: NotificationStatus.Error
            ));

            // NEWS: Official DSNS emergency report (@RescueService_Official → Herald).
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "DSNS",
                LocalizationManager.GetRandom("NEWS_FIRST_STRIKE", evt.PlannedHits),
                string.Empty,
                SocialMood.Warning);

            // CHIRP: Citizen panic reaction.
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_FIRST_STRIKE_PANIC"),
                SocialMood.Suffering);

            // CHIRP: Mariana's investigative thread.
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "MARIANA",
                LocalizationManager.GetRandom("MARIANA_FIRST_STRIKE_ANALYSIS", evt.PlannedHits),
                SocialMood.Angry);
        }

        public void Update(float currentTime)
        {
            // No batching: feed posts publish directly onto their channels.
        }

        public void FlushAll()
        {
            // No-op: no pending state to flush.
        }

        public void Reset()
        {
            // No-op: no pending state to reset.
        }
    }
}
