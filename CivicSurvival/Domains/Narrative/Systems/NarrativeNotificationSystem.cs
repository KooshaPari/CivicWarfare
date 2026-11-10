using System;
using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Domains.Narrative.Infrastructure;
using CivicSurvival.Domains.Narrative.Resolvers;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Narrative.Systems
{
    /// <summary>
    /// Wiring system that initializes and connects all narrative resolvers.
    /// Resolvers convert Core events into NarrativeToastDto and push to Sink.
    /// Also handles NarrativeTriggerEvent for generic trigger-based posts.
    ///
    /// N2-08 ACCEPTED: No cross-domain event coalescing — compound events in same frame
    /// produce 6+ toasts. Per-resolver batching (BatchAggregator) handles within-domain
    /// dedup but cross-domain coalescing would require new priority/grouping system.
    /// Current behavior: all toasts fire, UI queue renders in order.
    /// </summary>
    [ActIndependent]
    public partial class NarrativeNotificationSystem : ThrottledSystemBase, IInitializable
    {
        public int InitOrder => CivicSurvival.Core.Interfaces.Core.InitPriority.DEFAULT;

        private static readonly LogContext Log = new("NarrativeNotificationSystem");
        private static readonly NarrativeTrigger[] s_AllTriggers = (NarrativeTrigger[])Enum.GetValues(typeof(NarrativeTrigger));

        // All resolvers (array prevents future omissions in FlushAll/NotifyDeserialized)
        private INarrativeResolver[] m_AllResolvers = Array.Empty<INarrativeResolver>();
        [System.NonSerialized]
        private string[] m_ResolverProfileKeys = Array.Empty<string>();
        [System.NonSerialized] private readonly object[] m_OneArgBuffer = new object[1];
        [System.NonSerialized] private readonly object[] m_TwoArgBuffer = new object[2];

        // Not serialized: reset by re-running Initialize() on load (OnStartRunning or
        // the PLVS post-load pass via IInitializable.OnInitialize — whichever comes first)
        [System.NonSerialized] private bool m_Initialized;
        // Not serialized: transient post-load deferred flag (consumed in Initialize())
        [System.NonSerialized] private bool m_NeedNotifyDeserialized;
        [System.NonSerialized] private bool m_HasDeserializedPendingTriggers;
        [System.NonSerialized] private bool m_HasDeserializedPendingToasts;
        [System.NonSerialized] private NarrativeResolverPersistState m_DeserializedResolverState;
        [System.NonSerialized] private bool m_HasDeserializedResolverState;

        // PERF FIX: Cache GameTimeSystem to avoid ServiceRegistry lookup every 10 frames
        private GameTimeSystem? m_TimeSystem;

        // PERF: Budgeted trigger drain — event handler enqueues, OnThrottledUpdate drains up to
        // MAX_TRIGGERS_PER_TICK items. Coalesces duplicate TriggerKeys at enqueue time.
        // Eliminates 85ms spike from unbounded batch processing of accumulated NarrativeTriggerEvents.
        // BEHAVIORAL CHANGE: Toasts arrive with latency proportional to queue depth.
        // At MAX_TRIGGERS_PER_TICK=2 and UpdateInterval=10, a burst of 10 events drains in ~50 frames
        // (~800ms at 60fps, ~2.5s at 20fps). Acceptable for social feed UI.
        private const int MAX_TRIGGERS_PER_TICK = 2;
        private const int MAX_PENDING_TRIGGERS = 256;
        private const int MAX_PENDING_TOASTS = 256;

        private readonly struct PendingNarrativeTrigger
        {
            public readonly NarrativeTriggerEvent Event;
            public readonly string CoalescingKey;
            public readonly long EnqueuedGameTimeSeconds;

            public PendingNarrativeTrigger(NarrativeTriggerEvent evt, string coalescingKey, long enqueuedGameTimeSeconds)
            {
                Event = evt;
                CoalescingKey = coalescingKey;
                EnqueuedGameTimeSeconds = enqueuedGameTimeSeconds;
            }
        }

#pragma warning disable CIVIC278 // Cleared in Initialize() which runs on every load/reset (m_Initialized is [NonSerialized])
        private readonly List<PendingNarrativeTrigger> m_PendingTriggers = new();
        private int m_PendingTriggerHead;
#pragma warning restore CIVIC278
        private readonly List<NarrativeToastDto> m_PendingToasts = new();
        private int m_PendingToastHead;
        // Coalescing: track enqueued TriggerKeys to skip duplicates with identical context
#pragma warning disable CIVIC278 // Cleared alongside m_PendingTriggers in Initialize() and DrainPendingTriggers()
        private readonly HashSet<string> m_EnqueuedKeys = new();
#pragma warning restore CIVIC278
        private NotificationState? m_Sink;

        protected override int UpdateInterval => 10;

        protected override bool ShouldSkipUpdate() => !m_Initialized;

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            if (!m_Initialized)
                Initialize();
        }

        /// <summary>
        /// PLVS post-load pass: per-load re-init, symmetric to the per-load IResettable reset.
        /// OnStartRunning alone is not enough — CS2 reuses system instances across loads, so it
        /// fires once per game session. A new game started mid-session runs ResetState (sink
        /// cleared, resolvers unsubscribed) with no stop/start transition, leaving the system
        /// dead for the whole city without this hook.
        /// </summary>
        public void OnInitialize()
        {
            if (!m_Initialized)
                Initialize();
        }

        protected override void OnThrottledUpdate()
        {
            // Call Update() on all resolvers for batch flushing
            float currentTime;
            using (Core.Utils.PerformanceProfiler.Measure("NarrativeNotification.GetTime"))
            {
                m_TimeSystem ??= GameTimeSystem.Instance;
                if (m_TimeSystem == null) { Log.Error("[NarrativeNotificationSystem] TimeSystem unavailable"); return; }
                currentTime = m_TimeSystem.Current.TotalGameHours;
            }

            for (int i = 0; i < m_AllResolvers.Length; i++)
            {
                var resolver = m_AllResolvers[i];
#pragma warning disable CIVIC022 // Profiler name: only runs during flush, not truly per-frame
                using (Core.Utils.PerformanceProfiler.Measure(m_ResolverProfileKeys[i]))
#pragma warning restore CIVIC022
                {
                    resolver.Update(currentTime);
                }
            }

            // PERF: Drain deferred NarrativeTriggerEvent queue (enqueued by OnNarrativeTrigger).
            // Previously processed inline in event handler → 79ms spike on tier change cascades.
            if (m_PendingTriggers.Count > 0)
            {
                using (Core.Utils.PerformanceProfiler.Measure("NarrativeNotification.DrainTriggers"))
                {
                    DrainPendingTriggers();
                }
            }

            if (m_PendingToasts.Count > 0)
            {
                using (Core.Utils.PerformanceProfiler.Measure("NarrativeNotification.DrainToasts"))
                {
                    DrainPendingToasts();
                }
            }
        }

        /// <summary>
        /// Initialize resolvers. Called from OnStartRunning — all services are registered by then.
        /// </summary>
        private void Initialize()
        {
            // Unsubscribe old resolvers before re-init (prevents handler leak on save/load:
            // m_Initialized is [NonSerialized] → false after load → Initialize re-runs,
            // but old resolvers still subscribed to EventBus → duplicate notifications)
            for (int i = 0; i < m_AllResolvers.Length; i++)
                m_AllResolvers[i].Unsubscribe();
            UnsubscribeSafe<NarrativeTriggerEvent>(OnNarrativeTrigger);

            // Discard stale triggers from previous session — prevents old toasts firing after load
            if (!m_HasDeserializedPendingTriggers)
            {
                m_PendingTriggers.Clear();
                m_PendingTriggerHead = 0;
                m_EnqueuedKeys.Clear();
            }
            else
            {
                m_HasDeserializedPendingTriggers = false;
            }
            if (!m_HasDeserializedPendingToasts)
            {
                m_PendingToasts.Clear();
                m_PendingToastHead = 0;
            }
            else
            {
                m_HasDeserializedPendingToasts = false;
            }

            // Subscribe early: events published during resolver Subscribe() calls below
            // would be lost if we subscribed after them (R4 race fix).
            SubscribeRequired<NarrativeTriggerEvent>(OnNarrativeTrigger);

            var sink = ServiceRegistry.Instance.Require<NotificationState>();
            m_Sink = sink;

            var districtService = ServiceRegistry.Instance.Require<IDistrictStateReader>();

            // Create all resolvers (array prevents future omissions in FlushAll/NotifyDeserialized)
            m_AllResolvers = new INarrativeResolver[]
            {
                new ThreatNarrativeResolver(sink),
                new InfraNarrativeResolver(sink),
                new CorruptionNarrativeResolver(sink),
                new ShadowNarrativeResolver(sink, districtService),
                new DonorNarrativeResolver(sink),
                new BlackoutNarrativeResolver(sink, districtService),
                new CognitiveNarrativeResolver(sink, districtService),
                new FirstStrikeNarrativeResolver(sink),
                new MobilizationNarrativeResolver(sink),
            };

            m_ResolverProfileKeys = new string[m_AllResolvers.Length];
            for (int i = 0; i < m_AllResolvers.Length; i++)
                m_ResolverProfileKeys[i] = string.Concat("NarrativeNotification.", m_AllResolvers[i].Domain, "Resolver");

            bool restoredResolverState = m_HasDeserializedResolverState;
            if (restoredResolverState)
            {
                RestoreResolverState(m_DeserializedResolverState);
                m_DeserializedResolverState = default;
                m_HasDeserializedResolverState = false;
            }

            // Subscribe all resolvers
            for (int i = 0; i < m_AllResolvers.Length; i++)
            {
                m_AllResolvers[i].Subscribe();
            }

            // PERF FIX: Cache GameTimeSystem reference
            m_TimeSystem = GameTimeSystem.Instance;

            m_Initialized = true;

            // FIX S19-#13: Deserialize runs before Initialize → NotifyDeserialized missed.
            // Deferred call ensures resolvers get suppress window after creation.
            if (m_NeedNotifyDeserialized)
            {
                m_NeedNotifyDeserialized = false;
                if (!restoredResolverState)
                {
                    for (int i = 0; i < m_AllResolvers.Length; i++)
                        m_AllResolvers[i].NotifyDeserialized();
                    Log.Info("Deferred NotifyDeserialized applied to all resolvers");
                }
            }

            // Dev-time content-authoring aid: list NarrativeTrigger enum values that have no
            // SatireRegistry config yet. This is NOT a production warning — satire providers are
            // registered only for OPEN features (FeatureRegistry calls RegisterContent per open
            // feature), and an open feature always registers every config it owns. So the only
            // "missing" configs are triggers owned by not-yet-open features (later beta phases) —
            // intentional, not a defect. Logged at Debug so it helps while authoring future-phase
            // content without crying wolf in the shipped log. The real run-time signal — a trigger
            // that actually fires with no config — is the Warn at "Unknown trigger key" below.
            // Milestone180 and MilestoneVictory are handled by NarrativeSystem (character reactions), not here.
            if (Log.IsDebugEnabled)
            {
                var missing = new List<string>();
                for (int j = 0; j < s_AllTriggers.Length; j++)
                {
                    var trigger = s_AllTriggers[j];
                    if (trigger == NarrativeTrigger.Milestone180 || trigger == NarrativeTrigger.MilestoneVictory)
                        continue;
                    string key = trigger.ToKey();
                    if (!SatireRegistry.TryGetConfig(key, out _))
                        missing.Add(key);
                }
                if (missing.Count > 0)
                    Log.Debug($"SatireRegistry has no configs yet for {missing.Count} not-yet-open-feature triggers: {string.Join(", ", missing)}");
            }

            Log.Info("All resolvers initialized");
        }

        private NarrativeResolverPersistState CaptureResolverState()
        {
            var infra = default(NarrativeInfraResolverPersistState);
            var corruption = default(NarrativeCorruptionResolverPersistState);
            var cognitive = Array.Empty<NarrativeCognitiveCooldownPersistEntry>();

            for (int i = 0; i < m_AllResolvers.Length; i++)
            {
                if (m_AllResolvers[i] is InfraNarrativeResolver infraResolver)
                    infra = infraResolver.CapturePersistState();
                else if (m_AllResolvers[i] is CorruptionNarrativeResolver corruptionResolver)
                    corruption = corruptionResolver.CapturePersistState();
                else if (m_AllResolvers[i] is CognitiveNarrativeResolver cognitiveResolver)
                    cognitive = cognitiveResolver.CapturePersistState();
            }

            return new NarrativeResolverPersistState(infra, corruption, cognitive);
        }

        private void RestoreResolverState(in NarrativeResolverPersistState state)
        {
            for (int i = 0; i < m_AllResolvers.Length; i++)
            {
                if (m_AllResolvers[i] is InfraNarrativeResolver infraResolver)
                    infraResolver.RestorePersistState(state.Infra);
                else if (m_AllResolvers[i] is CorruptionNarrativeResolver corruptionResolver)
                    corruptionResolver.RestorePersistState(state.Corruption);
                else if (m_AllResolvers[i] is CognitiveNarrativeResolver cognitiveResolver)
                    cognitiveResolver.RestorePersistState(state.CognitiveCooldowns);
            }
        }

        protected override void OnDestroy()
        {
            for (int i = 0; i < m_AllResolvers.Length; i++)
            {
                m_AllResolvers[i].Unsubscribe();
            }

            UnsubscribeSafe<NarrativeTriggerEvent>(OnNarrativeTrigger);

            base.OnDestroy();
        }

        /// <summary>
        /// Central handler for NarrativeTriggerEvent.
        /// PERF: Only enqueues — actual processing deferred to OnThrottledUpdate.DrainPendingTriggers().
        /// Previously processed inline → 79ms spike when WorldShockSystem tier change published 2-3 events.
        /// </summary>
        private void OnNarrativeTrigger(NarrativeTriggerEvent evt)
        {
            // Milestone180/MilestoneVictory: NarrativeSystem handles character reactions,
            // but if all characters are gone (IsActive=false), no reaction fires.
            // SatireConfig entries in ScenarioSatireProvider serve as fallback (R6 fix).
            // NarrativeSystem character reactions have cooldown gate; NNS has coalescing gate —
            // both paths can fire without true duplication.

            // Coalesce: same TriggerKey + same context discriminator = same toast, skip duplicate
            string coalescingKey = BuildCoalescingKey(evt);
#pragma warning disable CIVIC230 // Coalescing guard replaces Contains check — HashSet.Add returns false for duplicates
            if (!m_EnqueuedKeys.Add(coalescingKey))
                return;
            if (m_PendingTriggers.Count - m_PendingTriggerHead >= MAX_PENDING_TRIGGERS)
            {
                var dropped = m_PendingTriggers[m_PendingTriggerHead++];
                m_EnqueuedKeys.Remove(dropped.CoalescingKey);
                if (Log.IsDebugEnabled) Log.Debug($"Dropped oldest narrative trigger after queue cap: {dropped.Event.TriggerKey}");
                CompactPendingTriggersIfNeeded();
            }
            m_PendingTriggers.Add(new PendingNarrativeTrigger(evt, coalescingKey, GetCurrentGameTimeSeconds()));
#pragma warning restore CIVIC230
            ForceNextUpdate();
        }

        /// <summary>
        /// Budgeted drain: process up to MAX_TRIGGERS_PER_TICK events, leave rest for next tick.
        /// Converts one 85ms spike into many sub-1ms ticks.
        /// </summary>
        private void DrainPendingTriggers()
        {
            if (EventBus == null)
            {
                Log.Warn("EventBus unavailable during trigger drain; retaining queue for retry");
                return;
            }

            int processed = 0;
            int visited = 0;
            // FIX M18: Cap total items visited (not just successfully processed) to prevent
            // unbounded loop when items fail validation (TryGetConfig/HasAuthor).
            const int MAX_VISITED_PER_TICK = MAX_TRIGGERS_PER_TICK * 4;
            while (m_PendingTriggerHead < m_PendingTriggers.Count && processed < MAX_TRIGGERS_PER_TICK && visited < MAX_VISITED_PER_TICK)
            {
                var pending = m_PendingTriggers[m_PendingTriggerHead++];
                m_EnqueuedKeys.Remove(pending.CoalescingKey);
                var evt = pending.Event;
                visited++;

                if (!SatireRegistry.TryGetConfig(evt.TriggerKey, out var config))
                {
                    Log.Warn($"Unknown trigger key: {evt.TriggerKey}");
                    continue;
                }

                if (!config.HasAuthor)
                {
                    if (Log.IsDebugEnabled) Log.Debug($"Config {evt.TriggerKey} has no author, skipping social post");
                    continue;
                }

                if (!PersonaRegistry.TryResolve(config.AuthorId, out var persona))
                {
                    Log.Warn($"Unknown author: {config.AuthorId}");
                    continue;
                }

                object[] args = BuildArgs(evt.ContextData);
                string message = SatireRegistry.GetMessageFromConfig(config, args);

                // Channel chosen by the resolved persona handle, exactly as the old
                // NotificationSystem author demux did: official handle → Herald (content-stable
                // NewsPostEvent), citizen handle → CHIPPER (SocialPostEvent). Both bypass the
                // toast sink and its author guessing.
                if (NewsAuthorRegistry.IsOfficial(persona.Handle))
                {
                    NarrativeEmitter.EmitNews(
                        EventBus,
                        persona.Handle,
                        NewsAuthorRegistry.GetDisplayName(persona.Handle),
                        message,
                        string.Empty,
                        config.Mood);
                }
                else
                {
                    EventBus.SafePublish(new SocialPostEvent(persona.Handle, message, config.Mood), "NarrativeNotificationSystem");
                }
                processed++;
            }
            CompactPendingTriggersIfNeeded();
        }

        private static long GetCurrentGameTimeSeconds()
        {
            if (!GameTimeSystem.TryGetGameHours(out var gameHours))
                return 0L;

            double seconds = gameHours * GameRate.SECONDS_PER_HOUR;
            return double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0
                ? 0L
                : (long)Math.Round(seconds);
        }

        private void DrainPendingToasts()
        {
            var sink = m_Sink;
            if (sink == null)
            {
                Log.Warn("NotificationState unavailable during toast drain; retaining queue for retry");
                return;
            }

            int processed = 0;
            while (m_PendingToastHead < m_PendingToasts.Count && processed < MAX_TRIGGERS_PER_TICK)
            {
                var toast = m_PendingToasts[m_PendingToastHead++];

                sink.Push(toast);
                processed++;
            }
            CompactPendingToastsIfNeeded();
        }

        private static string BuildCoalescingKey(NarrativeTriggerEvent evt)
        {
            return evt.ContextData.TryGetValue("idx", out var idx)
                ? string.Concat(evt.TriggerKey, "_", idx)
                : evt.TriggerKey;
        }

        private void CompactPendingTriggersIfNeeded()
        {
            if (m_PendingTriggerHead == 0) return;
            if (m_PendingTriggerHead >= m_PendingTriggers.Count)
            {
                m_PendingTriggers.Clear();
                m_PendingTriggerHead = 0;
                return;
            }
            if (m_PendingTriggerHead < 64 || m_PendingTriggerHead * 2 < m_PendingTriggers.Count) return;
            m_PendingTriggers.RemoveRange(0, m_PendingTriggerHead);
            m_PendingTriggerHead = 0;
        }

        private void CompactPendingToastsIfNeeded()
        {
            if (m_PendingToastHead == 0) return;
            if (m_PendingToastHead >= m_PendingToasts.Count)
            {
                m_PendingToasts.Clear();
                m_PendingToastHead = 0;
                return;
            }
            if (m_PendingToastHead < 64 || m_PendingToastHead * 2 < m_PendingToasts.Count) return;
            m_PendingToasts.RemoveRange(0, m_PendingToastHead);
            m_PendingToastHead = 0;
        }

        /// <summary>
        /// Build arguments array from context data.
        /// PERF M2.7: Uses fixed-size array instead of List to avoid allocation.
        /// </summary>
        private object[] BuildArgs(IReadOnlyDictionary<string, string> contextData)
        {
            if (contextData == null || contextData.Count == 0)
            {
                return Array.Empty<object>();
            }

            // Named keys for message formatting.
            // JournalistName used for SATIRE_INVEST_STOP messages.
            // PERF M2.7: Determine count first, then allocate exact size array
            bool hasArg0 = contextData.TryGetValue("JournalistName", out var journalist)
                        || contextData.TryGetValue("arg0", out journalist);
            bool hasArg1 = contextData.TryGetValue("arg1", out var arg1);

            if (!hasArg0)
            {
                return Array.Empty<object>();
            }

            if (!hasArg1)
            {
                m_OneArgBuffer[0] = journalist ?? string.Empty;
                return m_OneArgBuffer;
            }

            m_TwoArgBuffer[0] = journalist ?? string.Empty;
            m_TwoArgBuffer[1] = arg1 ?? string.Empty;
            return m_TwoArgBuffer;
        }
    }
}
