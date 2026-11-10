using System;
using System.Collections.Generic;
using Game;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Domains.Narrative.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves blackout events into notification DTOs.
    /// Handles: blackout started/ended per district.
    /// Uses batching to aggregate multiple simultaneous blackouts into single notification.
    ///
    /// S14-07 ACCEPTED: District name read at flush time (not capture). 1s batch window +
    /// district rename during active blackout = practically impossible. Null fallback guards it.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class BlackoutNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }

        public string Domain => "Blackout";

        private static readonly LogContext Log = new("BlackoutNarrativeResolver");

        private readonly NotificationState m_Sink;
        private readonly IDistrictStateReader? m_DistrictService;
        private IEventBus? m_EventBus;

        // Batch state
        private readonly BatchAggregator<int> m_PendingBlackoutStarts = new(BatchIdentityPolicy.NoDedup<int>());
        private readonly BatchAggregator<int> m_PendingBlackoutEnds = new(BatchIdentityPolicy.NoDedup<int>());

        public BlackoutNarrativeResolver(NotificationState sink, IDistrictStateReader? districtService)
        {
            m_Sink = sink;
            m_DistrictService = districtService;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<BlackoutStartedEvent>(OnBlackoutStarted);
            m_EventBus.Subscribe<BlackoutEndedEvent>(OnBlackoutEnded);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<BlackoutStartedEvent>(OnBlackoutStarted);
            m_EventBus.Unsubscribe<BlackoutEndedEvent>(OnBlackoutEnded);

            m_EventBus = null;
        }

        /// <summary>
        /// Called every frame for batch flushing.
        /// Flushes pending blackout start/end events after batch window.
        /// </summary>
        public void Update(float currentTime)
        {
            try
            {
                if (m_PendingBlackoutStarts.IsReadyToFlush())
                {
                    EmitBlackoutStartNotifications(m_PendingBlackoutStarts.FlushAndGet());
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing starts: {ex}");
                m_PendingBlackoutStarts.Clear();
            }

            try
            {
                if (m_PendingBlackoutEnds.IsReadyToFlush())
                {
                    EmitBlackoutEndNotifications(m_PendingBlackoutEnds.FlushAndGet());
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing ends: {ex}");
                m_PendingBlackoutEnds.Clear();
            }
        }

        /// <summary>
        /// Discard all pending batched events without processing them.
        /// Called on SetDefaults/ResetState to clear stale data after load failure.
        /// </summary>
        public void Reset()
        {
            m_PendingBlackoutStarts.Clear();
            m_PendingBlackoutEnds.Clear();
        }

        public void FlushAll()
        {
            var starts = m_PendingBlackoutStarts.ForceFlush();
            if (starts.Count > 0)
            {
                EmitBlackoutStartNotifications(starts);
            }

            var ends = m_PendingBlackoutEnds.ForceFlush();
            if (ends.Count > 0)
            {
                EmitBlackoutEndNotifications(ends);
            }
        }

        /// <summary>
        /// Accumulate blackout started event for batching.
        /// Will flush after BATCH_WINDOW_SECONDS.
        /// </summary>
        private void OnBlackoutStarted(BlackoutStartedEvent evt)
        {
#pragma warning disable CIVIC230 // NoDedup policy intentionally preserves start/end transition repeats in one batch
            bool forceFlush = m_PendingBlackoutStarts.Add(evt.DistrictIndex);
#pragma warning restore CIVIC230

            if (forceFlush)
            {
                EmitBlackoutStartNotifications(m_PendingBlackoutStarts.ForceFlush());
            }
        }

        /// <summary>
        /// Accumulate blackout ended event for batching.
        /// Will flush after BATCH_WINDOW_SECONDS.
        /// </summary>
        private void OnBlackoutEnded(BlackoutEndedEvent evt)
        {
            bool forceFlush = m_PendingBlackoutEnds.Add(evt.DistrictIndex);

            if (forceFlush)
            {
                EmitBlackoutEndNotifications(m_PendingBlackoutEnds.ForceFlush());
            }

            // A2 FIX: Removed BlackoutRecoveryEvent publish (was L3→L1 backward flow).
            // BlackoutRecoveredEvent now published by BlackoutEventProducerSystem (L1→L1).
        }

        /// <summary>
        /// Emit notifications for blackout starts.
        /// Single district: normal notification with district name.
        /// Multiple districts: aggregated notification with count.
        /// </summary>
        private void EmitBlackoutStartNotifications(IReadOnlyList<int> districtIndices)
        {
            if (districtIndices.Count == 0) return;

            int districtCount = districtIndices.Count;

            if (districtCount == 1)
            {
                // Single district - normal notification
                var districtIndex = districtIndices[0];
                string districtName = m_DistrictService?.GetDistrictName(districtIndex) ?? $"District {districtIndex}";

                m_Sink.Push(new NarrativeToastDto(
                    NotificationType.SystemAlert,
                    NarrativeEmitter.TimedId($"blackout_{districtIndex}"),
                    LocalizationManager.Get("NOTIFY_TITLE_OUTAGE"),
                    SatireRegistry.GetMessage("SATIRE_BLACKOUT", districtName),
                    Status: NotificationStatus.Error
                ));

                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "BABCYA",
                    SatireRegistry.GetMessage("SATIRE_BABCYA"),
                    SocialMood.Suffering
                );
            }
            else
            {
                // FIX NAR-P1-003: Replace hardcoded batch messages with localization
                // Multiple districts - aggregated notification
                m_Sink.Push(new NarrativeToastDto(
                    NotificationType.SystemAlert,
                    NarrativeEmitter.TimedId("blackout_batch"),
                    LocalizationManager.Get("NOTIFY_TITLE_OUTAGE"),
                    LocalizationManager.Get("NOTIFY_BLACKOUT_BATCH_MSG", districtCount),
                    Status: NotificationStatus.Error
                ));

                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "BABCYA",
                    LocalizationManager.Get("BABCYA_BLACKOUT_BATCH", districtCount),
                    SocialMood.Suffering
                );
            }
        }

        /// <summary>
        /// Emit notifications for blackout ends.
        /// Single district: normal notification with district name.
        /// Multiple districts: aggregated notification with count.
        /// </summary>
        private void EmitBlackoutEndNotifications(IReadOnlyList<int> districtIndices)
        {
            if (districtIndices.Count == 0) return;

            int districtCount = districtIndices.Count;

            if (districtCount == 1)
            {
                // Single district - normal notification
                var districtIndex = districtIndices[0];
                string districtName = m_DistrictService?.GetDistrictName(districtIndex) ?? $"District {districtIndex}";

                m_Sink.Push(new NarrativeToastDto(
                    NotificationType.SystemAlert,
                    NarrativeEmitter.TimedId($"restored_{districtIndex}"),
                    LocalizationManager.Get("NOTIFY_TITLE_RESTORED"),
                    SatireRegistry.GetMessage("SATIRE_RESTORED", districtName),
                    Status: NotificationStatus.Success
                ));
            }
            else
            {
                // FIX NAR-P1-003: Replace hardcoded batch message with localization
                // Multiple districts - aggregated notification
                m_Sink.Push(new NarrativeToastDto(
                    NotificationType.SystemAlert,
                    NarrativeEmitter.TimedId("restored_batch"),
                    LocalizationManager.Get("NOTIFY_TITLE_RESTORED"),
                    LocalizationManager.Get("NOTIFY_RESTORED_BATCH_MSG", districtCount),
                    Status: NotificationStatus.Success
                ));
            }
        }
    }
}
