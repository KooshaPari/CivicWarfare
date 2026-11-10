using System.Collections.Generic;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Domains.Narrative.Infrastructure;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// FIX N2-03: Resolves cognitive compromise/recovery events into notifications.
    /// Without this, CognitiveCompromisedEvent/CognitiveRecoveredEvent had zero narrative
    /// consumers — players got no feedback when districts were compromised by propaganda.
    /// No batching needed — compromise transitions are rare (per-district, hysteresis-gated).
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class CognitiveNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }

        public string Domain => "Cognitive";

        private static readonly LogContext Log = new("CognitiveNarrativeResolver");
        private const float TRANSITION_COOLDOWN_HOURS = 1f / 60f;

        private readonly NotificationState m_Sink;
        private readonly IDistrictStateReader? m_DistrictService;
        private IEventBus? m_EventBus;
        [NonEntityIndex] private readonly Dictionary<int, float> m_LastTransitionHourByDistrict = new();

        public CognitiveNarrativeResolver(NotificationState sink, IDistrictStateReader? districtService)
        {
            m_Sink = sink;
            m_DistrictService = districtService;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<CognitiveCompromisedEvent>(OnCompromised);
            m_EventBus.Subscribe<CognitiveRecoveredEvent>(OnRecovered);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<CognitiveCompromisedEvent>(OnCompromised);
            m_EventBus.Unsubscribe<CognitiveRecoveredEvent>(OnRecovered);

            m_EventBus = null;
        }

        public NarrativeCognitiveCooldownPersistEntry[] CapturePersistState()
        {
            var entries = new NarrativeCognitiveCooldownPersistEntry[m_LastTransitionHourByDistrict.Count];
            int index = 0;
            foreach (var kvp in m_LastTransitionHourByDistrict)
                entries[index++] = new NarrativeCognitiveCooldownPersistEntry(kvp.Key, kvp.Value);
            return entries;
        }

        public void RestorePersistState(IReadOnlyList<NarrativeCognitiveCooldownPersistEntry> entries)
        {
            m_LastTransitionHourByDistrict.Clear();
            for (int i = 0; i < entries.Count; i++)
                m_LastTransitionHourByDistrict[entries[i].DistrictIndex] = entries[i].LastTransitionHour;
        }

        private void OnCompromised(CognitiveCompromisedEvent evt)
        {
            if (!CanEmitTransition(evt.DistrictIndex)) return;
            string districtName = m_DistrictService?.GetDistrictName(evt.DistrictIndex) ?? DistrictUtils.GetFallbackName(evt.DistrictIndex);
            int integrityPct = (int)System.Math.Round(evt.Integrity * 100f);

            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId($"cognitive_compromised_{evt.DistrictIndex}"),
                LocalizationManager.Get("NOTIFY_TITLE_COGNITIVE_COMPROMISED"),
                LocalizationManager.Get("NOTIFY_COGNITIVE_COMPROMISED_MSG", districtName, integrityPct),
                Status: NotificationStatus.Error
            ));

            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_COGNITIVE_COMPROMISED", districtName),
                SocialMood.Suffering
            );
        }

        private void OnRecovered(CognitiveRecoveredEvent evt)
        {
            if (!CanEmitTransition(evt.DistrictIndex)) return;
            string districtName = m_DistrictService?.GetDistrictName(evt.DistrictIndex) ?? DistrictUtils.GetFallbackName(evt.DistrictIndex);

            m_Sink.Push(new NarrativeToastDto(
                NotificationType.SystemAlert,
                NarrativeEmitter.TimedId($"cognitive_recovered_{evt.DistrictIndex}"),
                LocalizationManager.Get("NOTIFY_TITLE_COGNITIVE_RECOVERED"),
                LocalizationManager.Get("NOTIFY_COGNITIVE_RECOVERED_MSG", districtName),
                Status: NotificationStatus.Success
            ));

            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_COGNITIVE_RECOVERED", districtName),
                SocialMood.Neutral
            );
        }

        private bool CanEmitTransition(int districtIndex)
        {
            if (!GameTimeSystem.TryGetGameHours(out var now))
                return false;
            if (m_LastTransitionHourByDistrict.TryGetValue(districtIndex, out var last) &&
                now - last < TRANSITION_COOLDOWN_HOURS)
            {
                return false;
            }

            m_LastTransitionHourByDistrict[districtIndex] = now;
            return true;
        }

        public void Update(float currentTime)
        {
            // No batching needed — compromise transitions are rare
        }

        public void FlushAll()
        {
            // No-op — no batch state
        }

        public void Reset()
        {
            m_LastTransitionHourByDistrict.Clear();
        }
    }
}
