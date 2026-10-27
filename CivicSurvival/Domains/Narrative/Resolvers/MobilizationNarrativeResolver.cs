using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Narrative.Infrastructure;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves mobilization events into notification DTOs.
    /// Handles: ManpowerCritical, ConscriptionActivated, InsufficientManpower.
    ///
    /// N3-05 ACCEPTED: ManpowerCritical has 24h cooldown (S14a-5) — global rate limiting by design.
    /// N3-06 ACCEPTED: InsufficientManpowerEvent is publisher-deduped per entity, then
    /// narrative-collapsed per AA type per cooldown window to avoid repeated same-type toasts.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class MobilizationNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }
        public void Reset() { }

        public string Domain => "Mobilization";

        private static readonly LogContext Log = new("MobilizationNarrativeResolver");

        private readonly NotificationState m_Sink;
        private IEventBus? m_EventBus;

        public MobilizationNarrativeResolver(NotificationState sink)
        {
            m_Sink = sink;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<ManpowerCriticalEvent>(OnManpowerCritical);
            m_EventBus.Subscribe<ConscriptionActivatedEvent>(OnConscriptionActivated);
            m_EventBus.Subscribe<ConscriptionDeactivatedEvent>(OnConscriptionDeactivated);
            m_EventBus.Subscribe<InsufficientManpowerEvent>(OnInsufficientManpower);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<ManpowerCriticalEvent>(OnManpowerCritical);
            m_EventBus.Unsubscribe<ConscriptionActivatedEvent>(OnConscriptionActivated);
            m_EventBus.Unsubscribe<ConscriptionDeactivatedEvent>(OnConscriptionDeactivated);
            m_EventBus.Unsubscribe<InsufficientManpowerEvent>(OnInsufficientManpower);
            m_EventBus = null;
        }

        private void OnManpowerCritical(ManpowerCriticalEvent evt)
        {
            // System alert
            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId("manpower_critical"),
                LocalizationManager.Get("NOTIFY_TITLE_MANPOWER"),
                LocalizationManager.Get("NOTIFY_MANPOWER_CRITICAL_MSG", evt.Available, evt.Total, (int)System.Math.Round(evt.Percent * 100)),
                Status: NotificationStatus.Warning
            ));

            // NEWS: Official warning
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "MILITARY_COMMAND",
                LocalizationManager.GetRandom("NEWS_MANPOWER_CRITICAL"),
                string.Empty,
                SocialMood.Warning
            );

            // CHIRP: Citizen reaction
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_MANPOWER_CRITICAL"),
                SocialMood.Warning
            );
        }

        private void OnConscriptionActivated(ConscriptionActivatedEvent evt)
        {
            // System alert
            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId("conscription_activated"),
                LocalizationManager.Get("NOTIFY_TITLE_CONSCRIPTION"),
                LocalizationManager.Get("NOTIFY_CONSCRIPTION_ACTIVE_MSG"),
                Status: NotificationStatus.Warning
            ));

            // NEWS: Official announcement
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "MILITARY_COMMAND",
                LocalizationManager.GetRandom("NEWS_CONSCRIPTION_ACTIVATED"),
                string.Empty,
                SocialMood.Warning
            );

            // CHIRP: Citizen distress
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_CONSCRIPTION"),
                SocialMood.Suffering
            );
        }

        private void OnConscriptionDeactivated(ConscriptionDeactivatedEvent evt)
        {
            // System alert - neutral
            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId("conscription_deactivated"),
                LocalizationManager.Get("NOTIFY_TITLE_CONSCRIPTION"),
                LocalizationManager.Get("NOTIFY_CONSCRIPTION_ENDED_MSG"),
                Status: NotificationStatus.Success
            ));
        }

        private void OnInsufficientManpower(InsufficientManpowerEvent evt)
        {
            // System alert - warning
            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId($"insufficient_manpower_{evt.BuildingType}"),
                LocalizationManager.Get("NOTIFY_TITLE_MANPOWER"),
                LocalizationManager.Get("NOTIFY_INSUFFICIENT_MANPOWER_MSG", evt.BuildingType, evt.Required, evt.Available),
                Status: NotificationStatus.Warning
            ));
        }

        /// <summary>
        /// No batching needed - notifications are immediate.
        /// </summary>
        public void Update(float currentTime)
        {
            // No-op: this resolver doesn't use batching
        }

        /// <summary>
        /// No pending batches to flush.
        /// </summary>
        public void FlushAll()
        {
            // No-op: this resolver doesn't use batching
        }
    }
}
