using System.Collections.Generic;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Domains.Narrative.Infrastructure;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves donor/diplomacy events into notification DTOs.
    /// Handles: conferences, funds, generators, patriot, sanctions.
    /// No batching needed - diplomatic events are rare and important.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class DonorNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }
        public void Reset() { }

        public string Domain => "Donor";

        private static readonly LogContext Log = new("DonorNarrativeResolver");
        private const float SKEPTIC_THRESHOLD_RATIO = 0.9f;
        private static readonly IReadOnlyDictionary<string, string> s_RefusalMessageKeys = new Dictionary<string, string>
        {
            ["generator_cap"] = DonorMessageIds.RefusalGeneratorCap,
            ["patriot_cap"] = DonorMessageIds.RefusalPatriotCap,
            ["trust_source_unavailable"] = DonorMessageIds.RefusalTrustSourceUnavailable
        };

        private readonly NotificationState m_Sink;
        private IEventBus? m_EventBus;

        public DonorNarrativeResolver(NotificationState sink)
        {
            m_Sink = sink;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<DonorEvent>(OnDonorEvent);
            m_EventBus.Subscribe<DebtEvent>(OnDebtEvent);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<DonorEvent>(OnDonorEvent);
            m_EventBus.Unsubscribe<DebtEvent>(OnDebtEvent);
            m_EventBus = null;
        }

        /// <summary>
        /// Consolidated handler for all donor/diplomacy events.
        /// Replaces 9 separate handlers.
        /// </summary>
        private void OnDonorEvent(DonorEvent evt)
        {
            switch (evt.Type)
            {
                case DonorEventType.ConferenceCalled:
                    HandleConferenceCalled(evt);
                    break;
                case DonorEventType.FundsReceived:
                    HandleFundsReceived(evt);
                    break;
                case DonorEventType.GeneratorsReceived:
                    HandleGeneratorsReceived(evt);
                    break;
                case DonorEventType.PatriotReceived:
                    HandlePatriotReceived();
                    break;
                case DonorEventType.PatriotExpired:
                    HandlePatriotExpired();
                    break;
                case DonorEventType.Refused:
                    HandleRefused(evt);
                    break;
                case DonorEventType.SanctionsApplied:
                    HandleSanctionsApplied(evt);
                    break;
                case DonorEventType.SanctionsExpired:
                    HandleSanctionsExpired();
                    break;
                case DonorEventType.Scandal:
                    HandleScandal();
                    break;
                case DonorEventType.AidPackageReceived:
                    break;
                default:
                    Log.Warn($"Unhandled {nameof(DonorEventType)}: {evt.Type}");
                    break;
            }
        }

        private void HandleConferenceCalled(DonorEvent evt)
        {
            string message = evt.Trust switch
            {
                TrustLevel.Full => LocalizationManager.Get("NOTIFY_DONOR_CONF_FULL"),
                TrustLevel.Partial => LocalizationManager.Get("NOTIFY_DONOR_CONF_PARTIAL"),
                TrustLevel.Minimal => LocalizationManager.Get("NOTIFY_DONOR_CONF_MINIMAL"),
                TrustLevel.Refused => LocalizationManager.Get("NOTIFY_DONOR_CONF_REFUSED"),
                _ => LocalizationManager.Get("NOTIFY_DONOR_CONF_DEFAULT")
            };

            NarrativeEmitter.Alert(m_Sink, "donor_conference",
                "NOTIFY_TITLE_DONOR_CONF", message);

            NarrativeEmitter.EmitNews(m_EventBus,
                "UN_AID", LocalizationManager.GetRandom("NEWS_DONOR_CALLED"), string.Empty);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                evt.Trust == TrustLevel.Full
                    ? LocalizationManager.GetRandom("CHIRP_DONOR_HOPE")
                    : LocalizationManager.GetRandom("CHIRP_DONOR_SKEPTIC"),
                evt.Trust == TrustLevel.Full ? SocialMood.Neutral : SocialMood.Suspicious);
        }

        private void HandleFundsReceived(DonorEvent evt)
        {
            NarrativeEmitter.Alert(m_Sink, "donor_funds",
                "NOTIFY_TITLE_INT_AID",
                LocalizationManager.Get("NOTIFY_DONOR_FUNDS_MSG", evt.Amount),
                NotificationStatus.Success);

            NarrativeEmitter.EmitNews(m_EventBus,
                "UN_AID", LocalizationManager.GetRandom("NEWS_DONOR_AID_FUNDS", evt.Amount), string.Empty);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DONOR_HOPE"));

            if (evt.Amount < BalanceConfig.Current.Diplomacy.FundsFull * SKEPTIC_THRESHOLD_RATIO)
            {
                NarrativeEmitter.EmitSocial(m_EventBus,
                    "MARIANA",
                    LocalizationManager.GetRandom("CHIRP_DONOR_SKEPTIC"),
                    SocialMood.Suspicious);
            }
        }

        private void HandleGeneratorsReceived(DonorEvent evt)
        {
            int totalMW = evt.Count * evt.MWEach;

            NarrativeEmitter.Alert(m_Sink, "donor_generators",
                "NOTIFY_TITLE_EMERG_POWER",
                LocalizationManager.Get("NOTIFY_DONOR_GENERATORS_MSG", evt.Count, totalMW),
                NotificationStatus.Success);

            NarrativeEmitter.EmitNews(m_EventBus,
                "NATO_SUPPORT",
                LocalizationManager.GetRandom("NEWS_DONOR_AID_POWER", evt.Count, evt.MWEach), string.Empty);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DONOR_HOPE"));
        }

        private void HandlePatriotReceived()
        {
            NarrativeEmitter.Alert(m_Sink, "donor_patriot",
                "NOTIFY_TITLE_AD_UPGRADE",
                LocalizationManager.Get("NOTIFY_DONOR_PATRIOT_MSG"),
                NotificationStatus.Success);

            NarrativeEmitter.EmitNews(m_EventBus,
                "NATO_DEFENSE",
                LocalizationManager.GetRandom("NEWS_DONOR_AID_DEFENSE", "Patriot"), string.Empty);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DONOR_HOPE"));
        }

        private void HandlePatriotExpired()
        {
            NarrativeEmitter.Alert(m_Sink, "donor_patriot_expired",
                "NOTIFY_TITLE_AD_EXPIRED",
                LocalizationManager.Get("NOTIFY_DONOR_PATRIOT_EXPIRED_MSG"),
                NotificationStatus.Warning);

            NarrativeEmitter.EmitNews(m_EventBus,
                "NATO_DEFENSE",
                LocalizationManager.GetRandom("NEWS_PATRIOT_EXPIRED"), string.Empty);
        }

        private void HandleRefused(DonorEvent evt)
        {
            string refusalMessage = !string.IsNullOrEmpty(evt.Message)
                ? ResolveRefusalMessage(evt.Message!)
                : ResolveRefusalMessage(evt.Trust);

            NarrativeEmitter.Alert(m_Sink, "donor_refused",
                "NOTIFY_TITLE_AID_DENIED", refusalMessage,
                NotificationStatus.Warning);

            NarrativeEmitter.EmitSocial(m_EventBus,
                "MARIANA",
                SatireRegistry.GetMessage("SATIRE_DONOR_REFUSED"),
                SocialMood.Suspicious);

            NarrativeEmitter.EmitSocial(m_EventBus,
                "KOTLETA",
                LocalizationManager.Get("KOTLETA_DONOR_REFUSED"),
                SocialMood.Angry);

            NarrativeEmitter.EmitSocial(m_EventBus,
                "BABCYA",
                LocalizationManager.Get("BABCYA_DONOR_REFUSED"),
                SocialMood.Suffering);
        }

        private static string ResolveRefusalMessage(string message)
        {
            if (s_RefusalMessageKeys.TryGetValue(message, out string key))
                return LocalizationManager.Get(key);

            if (message.StartsWith("DONOR_REFUSAL_", System.StringComparison.Ordinal))
                return LocalizationManager.Get(message);

            Log.Warn($"Unknown donor refusal message id: {message}");
            return LocalizationManager.Get(DonorMessageIds.RefusalGeneric);
        }

        private static string ResolveRefusalMessage(TrustLevel trust)
        {
            return trust switch
            {
                TrustLevel.Full => LocalizationManager.Get("NOTIFY_DONOR_CONF_FULL"),
                TrustLevel.Partial => LocalizationManager.Get("NOTIFY_DONOR_CONF_PARTIAL"),
                TrustLevel.Minimal => LocalizationManager.Get("NOTIFY_DONOR_CONF_MINIMAL"),
                TrustLevel.Refused => LocalizationManager.Get("NOTIFY_DONOR_REFUSED_MSG"),
                _ => LocalizationManager.Get("NOTIFY_DONOR_REFUSED_MSG")
            };
        }

        private void HandleSanctionsApplied(DonorEvent evt)
        {
            NarrativeEmitter.Alert(m_Sink, "sanctions",
                "NOTIFY_TITLE_SANCTIONS",
                LocalizationManager.Get("NOTIFY_SANCTIONS_MSG", evt.Days),
                NotificationStatus.Error);

            NarrativeEmitter.EmitNews(m_EventBus,
                "UN_COUNCIL",
                LocalizationManager.GetRandom("NEWS_SANCTIONS_APPLIED", evt.Days),
                string.Empty,
                SocialMood.Warning);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_SANCTIONS_HAPPY"));

            NarrativeEmitter.EmitSocial(m_EventBus,
                "KOTLETA",
                SatireRegistry.GetMessage("SATIRE_KOTLETA"),
                SocialMood.Angry);
        }

        private void HandleSanctionsExpired()
        {
            NarrativeEmitter.Alert(m_Sink, "sanctions_expired",
                "NOTIFY_TITLE_SANCTIONS_END",
                LocalizationManager.Get("NOTIFY_SANCTIONS_EXPIRED_MSG"),
                NotificationStatus.Success);

            // @CityAlert is an official-feed handle → Herald (preserves demux routing).
            NarrativeEmitter.EmitNews(m_EventBus,
                "CITY_ALERT",
                LocalizationManager.Get("SANCTIONS_EXPIRED_SOCIAL"), string.Empty);
        }

        private void HandleScandal()
        {
            NarrativeEmitter.Alert(m_Sink, "international_scandal",
                "NOTIFY_TITLE_SCANDAL",
                LocalizationManager.Get("NOTIFY_SCANDAL_MSG"),
                NotificationStatus.Warning);

            NarrativeEmitter.EmitNews(m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_SCANDAL_REPORT"),
                string.Empty,
                SocialMood.Suspicious);

            NarrativeEmitter.EmitSocial(m_EventBus,
                "MARIANA",
                LocalizationManager.GetRandom("CHIRP_SCANDAL_MARIANA"),
                SocialMood.Suspicious);
        }

        // ============================================================================
        // DEBT EVENT HANDLERS
        // ============================================================================

        private void OnDebtEvent(DebtEvent evt)
        {
            switch (evt.Type)
            {
                case DebtEventType.DebtWarning:
                    HandleDebtWarning(evt);
                    break;
                case DebtEventType.DebtRestructured:
                    HandleDebtRestructured();
                    break;
                case DebtEventType.DebtRelief:
                    HandleDebtRelief(evt);
                    break;
                case DebtEventType.PaymentMissed:
                    HandlePaymentMissed();
                    break;
                default:
                    // Other debt events (DebtAdded, PaymentMade, etc.) — no narrative
                    break;
            }
        }

        private void HandleDebtWarning(DebtEvent evt)
        {
            NarrativeEmitter.Alert(m_Sink, "debt_warning",
                "NOTIFY_TITLE_DEBT_WARNING",
                LocalizationManager.Get("NOTIFY_DEBT_WARNING_MSG", evt.TotalDebt),
                NotificationStatus.Warning);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DEBT_WARNING"),
                SocialMood.Suspicious);
        }

        private void HandleDebtRestructured()
        {
            NarrativeEmitter.Alert(m_Sink, "debt_restructured",
                "NOTIFY_TITLE_DEBT_RESTRUCTURED",
                LocalizationManager.Get("NOTIFY_DEBT_RESTRUCTURED_MSG"),
                NotificationStatus.Error);

            NarrativeEmitter.EmitNews(m_EventBus,
                "UN_AID",
                LocalizationManager.GetRandom("CHIRP_DEBT_IMF"),
                string.Empty,
                SocialMood.Warning);
        }

        private void HandleDebtRelief(DebtEvent evt)
        {
            NarrativeEmitter.Alert(m_Sink, "debt_relief",
                "NOTIFY_TITLE_DEBT_RELIEF",
                LocalizationManager.Get("NOTIFY_DEBT_RELIEF_MSG", evt.Amount),
                NotificationStatus.Success);

            NarrativeEmitter.EmitNews(m_EventBus,
                "UN_AID",
                LocalizationManager.GetRandom("NEWS_DEBT_RELIEF"), string.Empty);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DEBT_RELIEF"));
        }

        /// <summary>
        /// FIX N2-04: PaymentMissed was silently ignored — debt crisis sneaks up with no warning.
        /// </summary>
        private void HandlePaymentMissed()
        {
            NarrativeEmitter.Alert(m_Sink, "debt_payment_missed",
                "NOTIFY_TITLE_DEBT_MISSED",
                LocalizationManager.Get("NOTIFY_DEBT_MISSED_MSG"),
                NotificationStatus.Warning);

            NarrativeEmitter.EmitSocial(m_EventBus, "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_DEBT_MISSED"),
                SocialMood.Suspicious);
        }

        /// <summary>
        /// No-op Update() - diplomatic events are rare and don't need batching.
        /// </summary>
        public void Update(float currentTime)
        {
            if (m_EventBus == null)
                Subscribe();
        }

        /// <summary>
        /// FIX NAR-P2-005: No batches to flush - diplomatic events are not batched.
        /// </summary>
        public void FlushAll()
        {
            // No-op - this resolver has no batch state
        }
    }
}
