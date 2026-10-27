using System;
using System.Collections.Generic;
using System.Threading;
using Game;
using Newtonsoft.Json.Linq;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Domains.Network.Data;
using CivicSurvival.Domains.Network.Events;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Network.Systems
{
    /// <summary>
    /// Polls server for global news and online stats.
    /// Publishes events to EventBus for UI consumption.
    ///
    /// Features:
    /// - Polls /api/news/latest every 60s
    /// - Polls /api/stats/online every 60s
    /// - Publishes official server news to NewsFeedService
    /// - Graceful degradation on network errors
    ///
    /// Shared poll-loop scaffolding (throttle/jitter, circuit-breaker gate, epochs,
    /// pending-lock, mood/JSON helpers, destroy bookkeeping) lives in
    /// <see cref="NewsPollingSystemBase"/>. This system supplies the global-news
    /// specifics: the network-toggle gate, the news primary stream, the online-stats
    /// second stream, nickname registration, and connection-state events.
    /// </summary>
    [ActIndependent]
    public partial class GlobalNewsSystem : NewsPollingSystemBase
    {
        private static readonly LogContext s_Log = new("GlobalNewsSystem");
        protected override LogContext Log => s_Log;

        private const float POLL_INTERVAL_SECONDS = 60f;
        private const float ONLINE_STATS_INTERVAL_SECONDS = 60f;
        private const int MAX_NEWS_ITEMS_TOTAL = 10;
        private const int MAX_SEEN_NEWS_IDS = 512;

        protected override float PollIntervalSeconds => POLL_INTERVAL_SECONDS;

        private long m_LastFetchTimeTicks;  // TS-002 FIX: Use long ticks for atomic read/write
        private volatile bool m_IsConnected;  // TS-001 FIX: volatile for ThreadPool writes
        private float m_TimeSinceLastStatsPoll;

        // Main-thread handoff lock: background completions queue under it; the pump
        // and post-load/destroy resets take it too. Private — locks only this
        // system's own queues (MA0064: never a publicly-reachable instance).
        private readonly object m_PendingLock = new();

        // m_CachedNews removed — CIVIC336 dead field (GetCachedNews removed, consumers use fresh items via events)
        // m_CachedStats removed — CIVIC222 dead field (GetOnlineStats removed in CIVIC243)

        // FIX HIGH: Queue for main-thread processing (TriggerUpdate not safe from ThreadPool)
        // GC-FIX H2.4: Non-readonly to enable swap-buffer pattern (avoids allocation)
        private List<GlobalNewsItem> m_PendingNewsFeedItems = new();

        // GC-FIX H2.4: Swap-buffer to avoid allocation when processing pending items
        private List<GlobalNewsItem> m_ProcessingBuffer = new();

        // FIX P0-3: Queue ALL events for main-thread dispatch (EventBus is not thread-safe)
        // Separate typed lists because EventBus.Publish<T> routes by typeof(T)
        private readonly List<GlobalConnectionChangedEvent> m_PendingConnectionEvents = new();
        private readonly List<OnlineStatsUpdatedEvent> m_PendingStatsEvents = new();
        private readonly List<NicknameCompletion> m_PendingNicknameCompletions = new();
        private readonly List<NicknameBudgetUpdatedEvent> m_PendingNicknameBudgets = new();
        private readonly List<NewsFetchCompletion> m_PendingNewsFetchCompletions = new();
        private readonly List<StatsFetchCompletion> m_PendingStatsFetchCompletions = new();

        // CIVIC050: Pre-allocated scratch lists for ProcessPendingEvents (main-thread dispatch)
        private readonly List<GlobalConnectionChangedEvent> m_ConnEventsScratch = new();
        private readonly List<OnlineStatsUpdatedEvent> m_StatsEventsScratch = new();
        private readonly List<NicknameCompletion> m_NicknameCompletionsScratch = new();
        private readonly List<NicknameBudgetUpdatedEvent> m_NicknameBudgetsScratch = new();
        private readonly List<NewsFetchCompletion> m_NewsFetchCompletionsScratch = new();
        private readonly List<StatsFetchCompletion> m_StatsFetchCompletionsScratch = new();
        private readonly BoundedNewsIdDeduper m_SeenNewsIds = new(MAX_SEEN_NEWS_IDS);
        private int m_LatestNicknameRequestId;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Subscribe to toggle BEFORE LoadConfig (called in OnStartRunning) —
            // otherwise the system can never be enabled at runtime.
            SubscribeRequired<ToggleGlobalConnectionCommand>(OnToggleConnection);
            SubscribeRequired<SetNicknameCommand>(OnSetNickname);
            SubscribeRequired<GameLoadedEvent>(OnGameLoaded);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            if (m_Settings != null) return;

            LoadConfig();

            if (!m_Enabled)
            {
                Log.Info("Disabled (global news connection opt-in required)");
                return;
            }

            Log.Info($"Enabled — polling {m_ServerUrl}");

            // Initial fetch
            FetchNews();
            FetchOnlineStats();
            FetchNicknameBudget();
        }

        /// <summary>The on/off gate: the network-connection toggle.</summary>
        protected override bool ShouldPoll() => m_Enabled;

        /// <summary>Primary stream: news.</summary>
        protected override void Fetch() => FetchNews();

        /// <summary>Second stream: online stats, on the same 60s cadence.</summary>
        protected override void OnAdditionalPoll(float deltaTime, float currentTime)
        {
            // Online stats polling — FIX #254: jitter ±10s
#pragma warning disable CIVIC056 // Timer resets every 60s — never accumulates unbounded
            m_TimeSinceLastStatsPoll += deltaTime;
#pragma warning restore CIVIC056
            if (m_TimeSinceLastStatsPoll >= ONLINE_STATS_INTERVAL_SECONDS)
            {
                FetchOnlineStats();
                m_TimeSinceLastStatsPoll = NextJitter();
            }
        }

        /// <summary>
        /// Drains async completions that must reach UIUpdate even while simulation is paused.
        /// </summary>
        protected override void OnPumpCompletions()
        {
            DrainNicknameCompletions();
            DrainNicknameBudgets();

            if (!m_Enabled) return;

            ProcessPendingFetchCompletions();

            // FIX HIGH: Process pending items on main thread
            // EventBus and NewsFeedService are not safe from ThreadPool
            ProcessPendingEvents();
            ProcessPendingNewsFeedItems();
        }

        private void ProcessPendingFetchCompletions()
        {
#pragma warning disable CIVIC114 // Scratch lists are main-thread-only; lock protects m_Pending* not scratch
            m_NewsFetchCompletionsScratch.Clear();
            m_StatsFetchCompletionsScratch.Clear();
#pragma warning restore CIVIC114

            lock (m_PendingLock)
            {
                if (m_PendingNewsFetchCompletions.Count > 0)
                {
                    m_NewsFetchCompletionsScratch.AddRange(m_PendingNewsFetchCompletions);
                    m_PendingNewsFetchCompletions.Clear();
                }
                if (m_PendingStatsFetchCompletions.Count > 0)
                {
                    m_StatsFetchCompletionsScratch.AddRange(m_PendingStatsFetchCompletions);
                    m_PendingStatsFetchCompletions.Clear();
                }
            }

            foreach (var completion in m_NewsFetchCompletionsScratch)
                ApplyNewsFetchCompletion(completion);

            foreach (var completion in m_StatsFetchCompletionsScratch)
                ApplyStatsFetchCompletion(completion);
        }

        /// <summary>
        /// Dispatch queued events on main thread (P0-3: EventBus is not thread-safe).
        /// </summary>
        private void ProcessPendingEvents()
        {
#pragma warning disable CIVIC114 // Scratch lists are main-thread-only; lock protects m_Pending* not scratch
            m_ConnEventsScratch.Clear();
            m_StatsEventsScratch.Clear();
#pragma warning restore CIVIC114

            lock (m_PendingLock)
            {
                if (m_PendingConnectionEvents.Count > 0)
                {
                    m_ConnEventsScratch.AddRange(m_PendingConnectionEvents);
                    m_PendingConnectionEvents.Clear();
                }
                if (m_PendingStatsEvents.Count > 0)
                {
                    m_StatsEventsScratch.AddRange(m_PendingStatsEvents);
                    m_PendingStatsEvents.Clear();
                }
            }

            foreach (var evt in m_ConnEventsScratch)
                EventBus?.SafePublish(evt, "GlobalNewsSystem");

            foreach (var evt in m_StatsEventsScratch)
                EventBus?.SafePublish(evt, "GlobalNewsSystem");
        }

        private void DrainNicknameCompletions()
        {
#pragma warning disable CIVIC114 // Scratch list is main-thread-only; lock protects m_PendingNicknameCompletions
            m_NicknameCompletionsScratch.Clear();
#pragma warning restore CIVIC114

            lock (m_PendingLock)
            {
                if (m_PendingNicknameCompletions.Count > 0)
                {
                    m_NicknameCompletionsScratch.AddRange(m_PendingNicknameCompletions);
                    m_PendingNicknameCompletions.Clear();
                }
            }

            foreach (var completion in m_NicknameCompletionsScratch)
                ApplyNicknameCompletion(completion);
        }

        private void DrainNicknameBudgets()
        {
#pragma warning disable CIVIC114 // Scratch list is main-thread-only; lock protects m_PendingNicknameBudgets
            m_NicknameBudgetsScratch.Clear();
#pragma warning restore CIVIC114

            lock (m_PendingLock)
            {
                if (m_PendingNicknameBudgets.Count > 0)
                {
                    m_NicknameBudgetsScratch.AddRange(m_PendingNicknameBudgets);
                    m_PendingNicknameBudgets.Clear();
                }
            }

            foreach (var budget in m_NicknameBudgetsScratch)
            {
                // The server is the source of truth for the online nickname. Mirror the locally
                // persisted copy onto what the server reports so the field is never blank while
                // the server holds a real nickname (e.g. after a fresh install, cleared ModData,
                // or a save moved between machines — local copy empty, server still has the name).
                //
                // Defense in depth: the client NEVER writes a synthetic display fallback into
                // the local field. The current server contract reports Initialized=false for
                // never-set players, and TelemetryAuth.NicknameRegistrationResult already drops
                // any "Mayor_XXXX" pattern defensively before it reaches us. The IsSyntheticFallback
                // re-check here is the last-mile guard against any older server build that
                // still emits the synthetic display name. The UI surfaces "nickname not set"
                // (an honest "not signed in" state) for a never-set player — no fabricated
                // identity is ever persisted or rendered.
                if (m_Settings != null
                    && budget.Initialized
                    && !TelemetryAuth.NicknameRegistrationResult.IsSyntheticFallback(budget.Nickname)
                    && budget.Nickname != m_Settings.PlayerNickname)
                {
                    m_Settings.ApplyPatch(ModSettingsPatch.SetPlayerNickname(budget.Nickname));
                }

                EventBus?.SafePublish(budget, "GlobalNewsSystem");
            }
        }

        /// <summary>
        /// Read-only fetch of the player's nickname change budget from the server.
        /// Enqueues the result for main-thread dispatch (callback runs on ThreadPool).
        /// </summary>
        private void FetchNicknameBudget()
        {
            int asyncEpoch = Volatile.Read(ref m_AsyncEpoch);
            m_Auth.FetchNicknameStatusAsync(result =>
            {
                if (!IsAsyncEpochCurrent(asyncEpoch)) return;
                if (!result.Success
                    || result.ChangesRemaining == TelemetryAuth.NicknameRegistrationResult.ChangesRemainingUnknown)
                    return;

                lock (m_PendingLock)
                {
#pragma warning disable CIVIC230 // Background callback hands off immutable data to main-thread queue.
                    m_PendingNicknameBudgets.Add(
                        new NicknameBudgetUpdatedEvent(result.ChangesRemaining, result.Initialized, result.Nickname));
#pragma warning restore CIVIC230
                }
            });
        }

        private void ProcessPendingNewsFeedItems()
        {
            // GC-FIX H2.4: Swap-buffer pattern instead of copying list
            lock (m_PendingLock)
            {
                if (m_PendingNewsFeedItems.Count > 0)
                {
                    // Swap buffers instead of copying (avoids allocation)
                    (m_PendingNewsFeedItems, m_ProcessingBuffer) = (m_ProcessingBuffer, m_PendingNewsFeedItems);
                }
            }

            if (m_ProcessingBuffer.Count == 0) return;

            foreach (var item in m_ProcessingBuffer)
            {
                var mood = MapMoodToSocialMood(item.Mood);
                string body = ComposeBody(item);
                var post = NewsFeedPost.FromGlobalNews(item, mood, body);
                EventBus?.SafePublish(new OfficialNewsReceivedEvent(post), "GlobalNewsSystem");
            }

            // GC-FIX H2.4: Clear processing buffer for reuse
            m_ProcessingBuffer.Clear();
        }

        private void ClearNetworkCompletionQueues()
        {
            lock (m_PendingLock)
            {
                m_PendingConnectionEvents.Clear();
                m_PendingStatsEvents.Clear();
                m_PendingNewsFetchCompletions.Clear();
                m_PendingStatsFetchCompletions.Clear();
                m_PendingNewsFeedItems.Clear();
                m_PendingNicknameBudgets.Clear();
            }

            m_ProcessingBuffer.Clear();
        }

        protected override void OnDestroy()
        {
            // Deliver any pending terminal nickname results BEFORE MarkDestroyed
            // bumps the epoch (ApplyNicknameCompletion gates on IsAsyncEpochCurrent,
            // which goes false once destroyed). The unified UIUpdate pump now early-
            // returns when m_Destroyed, so this is the last chance to flush a
            // terminal registration result to RequestResultBridge instead of
            // silently dropping it on teardown.
            DrainNicknameCompletions();

            MarkDestroyed();
            System.Threading.Interlocked.Exchange(ref m_LastFetchTimeTicks, 0L);
            m_IsConnected = false;
            lock (m_PendingLock)
            {
                m_PendingNicknameCompletions.Clear();
            }
            ClearNetworkCompletionQueues();
            m_SeenNewsIds.Clear();
            UnsubscribeSafe<ToggleGlobalConnectionCommand>(OnToggleConnection);
            UnsubscribeSafe<SetNicknameCommand>(OnSetNickname);
            UnsubscribeSafe<GameLoadedEvent>(OnGameLoaded);
            base.OnDestroy();
        }

        private void LoadConfig()
        {
            LoadSharedConfig();
            m_Enabled = m_Settings!.NetworkConnectionEnabled;
        }

        private void OnToggleConnection(ToggleGlobalConnectionCommand cmd)
        {
            Interlocked.Increment(ref m_NetworkAsyncEpoch);
            m_Enabled = cmd.Enable;
            if (m_Settings != null)
                m_Settings.ApplyPatch(ModSettingsPatch.SetNetworkConnectionEnabled(cmd.Enable));
            // Online connection is a global (save-independent) user preference, like the
            // telemetry opt-in. Persist it to the global store so it survives across
            // saves and restarts; the in-save value is only a fallback.
            ConsentStore.Write(ConsentKey.OnlineConnection, cmd.Enable);

            // This system is the SINGLE writer of the Online state. Now that the new value
            // is committed to settings + ConsentStore, broadcast the authoritative
            // post-write signal carrying the final value. Functional/identity consumers
            // (TelemetryService, PersonalChronicleSystem, ArenaLeaderboardSystem) react to
            // THIS event and read Enabled from it — they no longer re-read the setting that
            // this handler just wrote, so the dispatch order against the raw command no
            // longer matters (HIGH#1 race removed by construction).
            EventBus?.SafePublish(new OnlineConnectionStateChangedEvent(cmd.Enable), "GlobalNewsSystem");

            if (m_Enabled)
            {
                Log.Info("Connection enabled by user");
                DrainNicknameCompletions();

                // M-38 FIX: Clear stale non-terminal pending queues from previous session.
                // Nickname completions are terminal results; they must drain through ApplyNicknameCompletion.
                ClearNetworkCompletionQueues();
                ResetPollTimer();
                m_TimeSinceLastStatsPoll = 0f;
                m_CircuitBreaker.Reset();
                SetConnectionState(false, "Connecting to Global Grid", forceEvent: true);
                FetchNews();
                FetchOnlineStats();
                FetchNicknameBudget();
            }
            else
            {
                Log.Info("Connection disabled by user");
                m_IsConnected = false;
                ClearNetworkCompletionQueues();
#pragma warning disable CIVIC244 // By design: immediate notification to dependent systems on user toggle
                EventBus?.SafePublish(new GlobalConnectionChangedEvent(false, "Disconnected by user"), "GlobalNewsSystem");
#pragma warning restore CIVIC244
            }
        }

        private void OnSetNickname(SetNicknameCommand cmd)
        {
            if (cmd.RequestId > 0)
            {
                if (cmd.RequestId > m_LatestNicknameRequestId)
                    m_LatestNicknameRequestId = cmd.RequestId;

                string nickname = cmd.Nickname ?? string.Empty;
                int requestId = cmd.RequestId;
                if (nickname.Length > 0)
                {
                    var validation = NameFilter.Validate(nickname);
                    if (!validation.IsValid)
                    {
                        var reason = validation.Error.Contains("length", StringComparison.OrdinalIgnoreCase)
                            || validation.Error.Contains("exceed", StringComparison.OrdinalIgnoreCase)
                            ? ReasonIds.NicknameInvalidLength
                            : ReasonIds.NicknameInvalidChars;
                        RequestResultBridge.PublishTerminalForBegun(
                            RequestResultBridge.Nickname,
                            requestId,
                            RequestStatus.Failed,
                            reason);
                        return;
                    }
                }

                // Nickname registration is an online-only operation. If the player has
                // turned the global connection off (m_Enabled == false) no network call
                // may go out — mirror the gating on the poll loop (ShouldPoll) and the
                // initial fetches. Publish a terminal Failed so the UI request resolves
                // instead of hanging on a request that will never reach the server.
                if (!m_Enabled)
                {
                    RequestResultBridge.PublishTerminalForBegun(
                        RequestResultBridge.Nickname,
                        requestId,
                        RequestStatus.Failed,
                        ReasonIds.NicknameServerUnavailable);
                    return;
                }

                int asyncEpoch = Volatile.Read(ref m_AsyncEpoch);
                m_Auth.RegisterNicknameAsync(nickname, result =>
                {
                    if (!IsAsyncEpochCurrent(asyncEpoch))
                        return;

                    lock (m_PendingLock)
                    {
#pragma warning disable CIVIC230 // Background callback hands off immutable completion data to main-thread queue.
                        m_PendingNicknameCompletions.Add(new NicknameCompletion(
                            asyncEpoch,
                            nickname,
                            requestId,
                            result.Success,
                            result.ReasonId,
                            result.ChangesRemaining,
                            result.Initialized));
#pragma warning restore CIVIC230
                    }
                });
                return;
            }

            string localNickname = cmd.Nickname ?? string.Empty;
            if (localNickname.Length > 0 && !NameFilter.IsValid(localNickname))
                return;

            if (m_Settings != null)
                m_Settings.ApplyPatch(ModSettingsPatch.SetPlayerNickname(localNickname));
        }

        private void OnGameLoaded(GameLoadedEvent evt)
        {
            ResetNewsProducerAfterLoad();

            if (!m_Enabled)
                return;

            ResetPollTimer();
            m_CircuitBreaker.Reset();
            FetchNews();
        }

        private void ResetNewsProducerAfterLoad()
        {
            Interlocked.Increment(ref m_NetworkAsyncEpoch);
            Interlocked.Exchange(ref m_LastFetchTimeTicks, 0L);

            lock (m_PendingLock)
            {
                m_PendingNewsFetchCompletions.Clear();
                m_PendingNewsFeedItems.Clear();
                // Guarded by m_PendingLock: PublishToNewsFeed mutates the deduper from
                // background completion threads, so the reset must hold the same lock to
                // avoid racing an in-flight MarkSeen (epoch bump doesn't stop one mid-call).
                m_SeenNewsIds.Clear();
            }

            m_ProcessingBuffer.Clear();
            m_UnknownMoodWarnings.Clear();
        }

        private void ApplyNicknameCompletion(NicknameCompletion completion)
        {
            if (!IsAsyncEpochCurrent(completion.AsyncEpoch))
                return;

            if (completion.RequestId < m_LatestNicknameRequestId)
            {
                RequestResultBridge.PublishTerminalForBegun(
                    RequestResultBridge.Nickname,
                    completion.RequestId,
                    RequestStatus.Failed,
                    ReasonIds.RequestSuperseded);
                return;
            }

            if (!completion.Success)
            {
                RequestResultBridge.PublishTerminalForBegun(
                    RequestResultBridge.Nickname,
                    completion.RequestId,
                    RequestStatus.Failed,
                    string.IsNullOrEmpty(completion.ReasonId)
                        ? ReasonIds.NicknameServerUnavailable
                        : completion.ReasonId);
                return;
            }

            if (m_Settings != null)
                m_Settings.ApplyPatch(ModSettingsPatch.SetPlayerNickname(completion.Nickname));

            PublishNicknameBudget(completion.ChangesRemaining, completion.Initialized, completion.Nickname);

            RequestResultBridge.PublishTerminalForBegun(
                RequestResultBridge.Nickname,
                completion.RequestId,
                RequestStatus.Success,
                canonicalEcho: completion.Nickname);
        }

        /// <summary>
        /// Surface the server-reported nickname change budget to the UI system via EventBus.
        /// Skips when the budget is unknown (server did not report one).
        /// </summary>
        private void PublishNicknameBudget(int changesRemaining, bool initialized, string nickname)
        {
            if (changesRemaining == TelemetryAuth.NicknameRegistrationResult.ChangesRemainingUnknown)
                return;

            EventBus?.SafePublish(
                new NicknameBudgetUpdatedEvent(changesRemaining, initialized, nickname),
                "GlobalNewsSystem");
        }

        private readonly struct NicknameCompletion
        {
            public readonly int AsyncEpoch;
            public readonly string Nickname;
            public readonly int RequestId;
            public readonly bool Success;
            public readonly string ReasonId;
            public readonly int ChangesRemaining;
            public readonly bool Initialized;

            public NicknameCompletion(
                int asyncEpoch,
                string nickname,
                int requestId,
                bool success,
                string reasonId,
                int changesRemaining,
                bool initialized)
            {
                AsyncEpoch = asyncEpoch;
                Nickname = nickname ?? string.Empty;
                RequestId = requestId;
                Success = success;
                ReasonId = reasonId ?? string.Empty;
                ChangesRemaining = changesRemaining;
                Initialized = initialized;
            }
        }

        private enum FetchCompletionStatus
        {
            None = 0,
            Success = 1,
            Failure = 2,
            Cancel = 3
        }

        private readonly struct NewsFetchCompletion
        {
            public readonly int AsyncEpoch;
            public readonly int NetworkEpoch;
            public readonly CircuitBreakerState.BreakerProbe Probe;
            public readonly FetchCompletionStatus Status;
            public readonly IReadOnlyList<GlobalNewsItem> Items;
            public readonly long LatestTicks;
            public readonly string Message;
            public readonly string LogMessage;
            public readonly bool LogAsError;

            private NewsFetchCompletion(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                FetchCompletionStatus status,
                IReadOnlyList<GlobalNewsItem> items,
                long latestTicks,
                string message,
                string logMessage,
                bool logAsError)
            {
                AsyncEpoch = asyncEpoch;
                NetworkEpoch = networkEpoch;
                Probe = probe;
                Status = status;
                Items = items;
                LatestTicks = latestTicks;
                Message = message ?? string.Empty;
                LogMessage = logMessage ?? string.Empty;
                LogAsError = logAsError;
            }

            public static NewsFetchCompletion Success(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                IReadOnlyList<GlobalNewsItem> items,
                long latestTicks)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Success, items, latestTicks, string.Empty, string.Empty, false);

            public static NewsFetchCompletion Failure(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                string message,
                string logMessage,
                bool logAsError = false)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Failure, Array.Empty<GlobalNewsItem>(), 0L, message, logMessage, logAsError);

            public static NewsFetchCompletion Cancel(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Cancel, Array.Empty<GlobalNewsItem>(), 0L, string.Empty, string.Empty, false);
        }

        private readonly struct StatsFetchCompletion
        {
            public readonly int AsyncEpoch;
            public readonly int NetworkEpoch;
            public readonly CircuitBreakerState.BreakerProbe Probe;
            public readonly FetchCompletionStatus Status;
            public readonly OnlineStats Stats;
            public readonly string Message;
            public readonly string LogMessage;

            private StatsFetchCompletion(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                FetchCompletionStatus status,
                OnlineStats stats,
                string message,
                string logMessage)
            {
                AsyncEpoch = asyncEpoch;
                NetworkEpoch = networkEpoch;
                Probe = probe;
                Status = status;
                Stats = stats;
                Message = message ?? string.Empty;
                LogMessage = logMessage ?? string.Empty;
            }

            public static StatsFetchCompletion Success(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                OnlineStats stats)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Success, stats, string.Empty, string.Empty);

            public static StatsFetchCompletion Failure(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe,
                string message,
                string logMessage)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Failure, default, message, logMessage);

            public static StatsFetchCompletion Cancel(
                int asyncEpoch,
                int networkEpoch,
                CircuitBreakerState.BreakerProbe probe)
                => new(asyncEpoch, networkEpoch, probe, FetchCompletionStatus.Cancel, default, string.Empty, string.Empty);
        }

        /// <summary>
        /// Fetch news on background thread (fire-and-forget by design).
        /// </summary>
        private void FetchNews()
        {
            float requestTime = UnityEngine.Time.realtimeSinceStartup;
            if (!m_CircuitBreaker.TryBeginProbe(requestTime, out var probe))
                return;

            int asyncEpoch = Volatile.Read(ref m_AsyncEpoch);
            int networkEpoch = Volatile.Read(ref m_NetworkAsyncEpoch);
            var terminalDelivered = new int[1];
            BackgroundTask.Run(() =>
            {
                if (!IsNetworkAsyncEpochCurrent(asyncEpoch, networkEpoch))
                {
                    EnqueueNewsFetchCompletionOnce(
                        terminalDelivered,
                        NewsFetchCompletion.Cancel(asyncEpoch, networkEpoch, probe));
                    return;
                }
                try
                {
                    var url = $"{m_ServerUrl}/news/latest?limit={MAX_NEWS_ITEMS_TOTAL}";
                    // TS-002 FIX: Read ticks atomically, convert to DateTime for URL
                    long ticks = Interlocked.Read(ref m_LastFetchTimeTicks);
                    if (ticks != 0)
                    {
                        var lastFetch = new DateTime(ticks, DateTimeKind.Utc);
                        url += $"&since={lastFetch:yyyy-MM-ddTHH:mm:ssZ}";
                    }

                    var result = HttpUtils.Get(url);
                    if (!result.Success)
                    {
                        EnqueueNewsFetchCompletionOnce(
                            terminalDelivered,
                            NewsFetchCompletion.Failure(
                                asyncEpoch,
                                networkEpoch,
                                probe,
                                "Connection lost",
                                $"News fetch failed: {result.ErrorMessage}"));
                        return;
                    }

                    var items = ParseNewsResponse(result.Response);
                    long latestTicks = ticks;

                    if (items.Count > 0)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (items[i].Timestamp.Ticks > latestTicks)
                                latestTicks = items[i].Timestamp.Ticks;
                        }
                    }

                    EnqueueNewsFetchCompletionOnce(
                        terminalDelivered,
                        NewsFetchCompletion.Success(asyncEpoch, networkEpoch, probe, items, latestTicks));
                }
                catch (Exception ex)
                {
                    Log.Error($"News fetch/parse failed: {ex}");
                    EnqueueNewsFetchCompletionOnce(
                        terminalDelivered,
                        NewsFetchCompletion.Failure(
                            asyncEpoch,
                            networkEpoch,
                            probe,
                            "Connection lost",
                            $"News parse error: {ex}",
                            logAsError: true));
                }
            }, () => EnqueueNewsFetchCompletionOnce(
                terminalDelivered,
                NewsFetchCompletion.Cancel(asyncEpoch, networkEpoch, probe)));
        }

        /// <summary>
        /// Fetch online stats on background thread (fire-and-forget by design).
        /// </summary>
        private void FetchOnlineStats()
        {
            float requestTime = UnityEngine.Time.realtimeSinceStartup;
            if (!m_CircuitBreaker.TryBeginProbe(requestTime, out var probe))
                return;

            int asyncEpoch = Volatile.Read(ref m_AsyncEpoch);
            int networkEpoch = Volatile.Read(ref m_NetworkAsyncEpoch);
            var terminalDelivered = new int[1];
            BackgroundTask.Run(() =>
            {
                if (!IsNetworkAsyncEpochCurrent(asyncEpoch, networkEpoch))
                {
                    EnqueueStatsFetchCompletionOnce(
                        terminalDelivered,
                        StatsFetchCompletion.Cancel(asyncEpoch, networkEpoch, probe));
                    return;
                }
                try
                {
                    var url = $"{m_ServerUrl}/stats/online";

                    var result = HttpUtils.Get(url);
                    if (!result.Success)
                    {
                        EnqueueStatsFetchCompletionOnce(
                            terminalDelivered,
                            StatsFetchCompletion.Failure(
                                asyncEpoch,
                                networkEpoch,
                                probe,
                                "Connection lost",
                                $"Stats fetch failed: {result.ErrorMessage}"));
                        return;
                    }

                    var stats = ParseOnlineStats(result.Response);
                    EnqueueStatsFetchCompletionOnce(
                        terminalDelivered,
                        StatsFetchCompletion.Success(asyncEpoch, networkEpoch, probe, stats));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Stats fetch/parse failed: {ex}");
                    EnqueueStatsFetchCompletionOnce(
                        terminalDelivered,
                        StatsFetchCompletion.Failure(
                            asyncEpoch,
                            networkEpoch,
                            probe,
                            "Connection lost",
                            $"Stats fetch/parse failed: {ex}"));
                }
            }, () => EnqueueStatsFetchCompletionOnce(
                terminalDelivered,
                StatsFetchCompletion.Cancel(asyncEpoch, networkEpoch, probe)));
        }

        private void EnqueueNewsFetchCompletionOnce(int[] terminalDelivered, NewsFetchCompletion completion)
        {
            if (Interlocked.Exchange(ref terminalDelivered[0], 1) != 0)
                return;

            if (!IsNetworkAsyncEpochCurrent(completion.AsyncEpoch, completion.NetworkEpoch))
            {
                m_CircuitBreaker.CancelProbe(completion.Probe);
                return;
            }

            lock (m_PendingLock)
            {
                m_PendingNewsFetchCompletions.Add(completion);
            }
        }

        private void EnqueueStatsFetchCompletionOnce(int[] terminalDelivered, StatsFetchCompletion completion)
        {
            if (Interlocked.Exchange(ref terminalDelivered[0], 1) != 0)
                return;

            if (!IsNetworkAsyncEpochCurrent(completion.AsyncEpoch, completion.NetworkEpoch))
            {
                m_CircuitBreaker.CancelProbe(completion.Probe);
                return;
            }

            lock (m_PendingLock)
            {
                m_PendingStatsFetchCompletions.Add(completion);
            }
        }

        private void ApplyNewsFetchCompletion(NewsFetchCompletion completion)
        {
            if (!IsNetworkAsyncEpochCurrent(completion.AsyncEpoch, completion.NetworkEpoch))
                return;

            switch (completion.Status)
            {
                case FetchCompletionStatus.Success:
                    m_CircuitBreaker.RecordSuccess(completion.Probe);
                    if (completion.Items.Count > 0)
                    {
                        Interlocked.Exchange(ref m_LastFetchTimeTicks, completion.LatestTicks);
                        PublishToNewsFeed(completion.Items);

                        if (Log.IsDebugEnabled) Log.Debug($"Received {completion.Items.Count} news items");
                    }

                    // FIX H96: Check m_Enabled before setting connected — user may have disabled
                    // between fetch start and callback. Without this, background thread overwrites
                    // the m_IsConnected=false set by OnToggleConnection.
                    SetConnectionState(true, "Connected to Global Grid");
                    break;

                case FetchCompletionStatus.Failure:
                    LogFetchFailure(completion.LogMessage, completion.LogAsError);
                    m_CircuitBreaker.RecordFailure(completion.Probe);
                    SetConnectionState(false, completion.Message);
                    break;

                case FetchCompletionStatus.Cancel:
                    m_CircuitBreaker.CancelProbe(completion.Probe);
                    break;

                default:
                    Log.Warn($"Unknown news fetch completion status: {completion.Status}");
                    m_CircuitBreaker.CancelProbe(completion.Probe);
                    break;
            }
        }

        private void ApplyStatsFetchCompletion(StatsFetchCompletion completion)
        {
            if (!IsNetworkAsyncEpochCurrent(completion.AsyncEpoch, completion.NetworkEpoch))
                return;

            switch (completion.Status)
            {
                case FetchCompletionStatus.Success:
                    m_CircuitBreaker.RecordSuccess(completion.Probe);
                    SetConnectionState(true, "Connected to Global Grid");

                    lock (m_PendingLock) { m_PendingStatsEvents.Add(new OnlineStatsUpdatedEvent(completion.Stats)); }

                    if (Log.IsDebugEnabled)
                        Log.Debug($"Online stats: {completion.Stats.OnlineNow} now, {completion.Stats.TotalPlayers} total");
                    break;

                case FetchCompletionStatus.Failure:
                    LogFetchFailure(completion.LogMessage, logAsError: false);
                    m_CircuitBreaker.RecordFailure(completion.Probe);
                    SetConnectionState(false, completion.Message);
                    break;

                case FetchCompletionStatus.Cancel:
                    m_CircuitBreaker.CancelProbe(completion.Probe);
                    break;

                default:
                    Log.Warn($"Unknown stats fetch completion status: {completion.Status}");
                    m_CircuitBreaker.CancelProbe(completion.Probe);
                    break;
            }
        }

        private void LogFetchFailure(string logMessage, bool logAsError)
        {
            if (string.IsNullOrEmpty(logMessage))
                return;

            if (logAsError)
                Log.Error(logMessage);
            else
                Log.Warn(logMessage);
        }

        private void SetConnectionState(bool isConnected, string message, bool forceEvent = false)
        {
            if (isConnected && !m_Enabled)
                return;
            if (!forceEvent && m_IsConnected == isConnected)
                return;

            m_IsConnected = isConnected;
            lock (m_PendingLock)
            {
                m_PendingConnectionEvents.Add(new GlobalConnectionChangedEvent(isConnected, message));
            }
        }

        private List<GlobalNewsItem> ParseNewsResponse(string json)
        {
#pragma warning disable CIVIC050 // returned/stored, not per-frame
            var items = new List<GlobalNewsItem>();
#pragma warning restore CIVIC050

            // Parse combined response: { "chronicle": [...], "breaking": [...] }
            // No try/catch here — let exceptions propagate to caller so CircuitBreaker
            // sees parse failures (RecordSuccess is AFTER this call, outer catch handles errors)
            var root = JObject.Parse(json);

            var chronicleItems = ParseChronicleArray(root);
            items.AddRange(chronicleItems);

            var breakingItems = ParseBreakingArray(root);
            items.AddRange(breakingItems);

            // Sort by timestamp descending
            items.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            return items;
        }

        private List<GlobalNewsItem> ParseChronicleArray(JObject root)
        {
#pragma warning disable CIVIC050 // returned/stored, not per-frame
            var items = new List<GlobalNewsItem>();
#pragma warning restore CIVIC050
            if (root["chronicle"] is not JArray chronicle) return items;

            foreach (var token in chronicle)
            {
                if (token is not JObject itemObj) continue;
                var item = new GlobalNewsItem(
                    id: ReadString(itemObj, "id"),
                    headline: ReadString(itemObj, "headline"),
                    category: "chronicle",
                    nickname: null,
                    timestamp: ReadDateTime(itemObj, "created_at"),
                    isChronicle: true,
                    body: ReadString(itemObj, "body"),
                    breakingFlash: ReadString(itemObj, "breaking_flash"),
                    mood: ReadString(itemObj, "mood")
                );
                if (!string.IsNullOrEmpty(item.Id))
                    items.Add(item);
            }
            return items;
        }

        private List<GlobalNewsItem> ParseBreakingArray(JObject root)
        {
#pragma warning disable CIVIC050 // returned/stored, not per-frame
            var items = new List<GlobalNewsItem>();
#pragma warning restore CIVIC050
            if (root["breaking"] is not JArray breaking) return items;

            foreach (var token in breaking)
            {
                if (token is not JObject itemObj) continue;
                var item = new GlobalNewsItem(
                    id: ReadString(itemObj, "id"),
                    headline: ReadString(itemObj, "headline"),
                    category: ReadString(itemObj, "category") ?? "breaking",
                    nickname: ReadString(itemObj, "nickname"),
                    timestamp: ReadDateTime(itemObj, "created_at"),
                    isChronicle: false,
                    body: ReadString(itemObj, "body"),
                    breakingFlash: ReadString(itemObj, "breaking_flash"),
                    mood: ReadString(itemObj, "mood")
                );
                if (!string.IsNullOrEmpty(item.Id))
                    items.Add(item);
            }
            return items;
        }

        private OnlineStats ParseOnlineStats(string json)
        {
            var obj = JObject.Parse(json);
            return new OnlineStats(
                onlineNow: ReadInt(obj, "online_now"),
                onlineHour: ReadInt(obj, "online_hour"),
                onlineToday: ReadInt(obj, "online_today"),
                totalPlayers: ReadInt(obj, "total_players")
            );
        }

        private void PublishToNewsFeed(IReadOnlyList<GlobalNewsItem> items)
        {
            // FIX HIGH: Queue items for main-thread processing instead of calling EventBus directly
            // EventBus publish is not thread-safe; main-thread completions call this before dispatch.
            lock (m_PendingLock)
            {
                foreach (var item in items)
                {
                    if (m_SeenNewsIds.MarkSeen(item.Id))
                        m_PendingNewsFeedItems.Add(item);
                }
            }
        }

        /// <summary>
        /// Check if connected to global grid.
        /// </summary>
        public bool IsConnected => m_IsConnected;

        /// <summary>
        /// Check if global news is enabled.
        /// </summary>
        public bool IsEnabled => m_Enabled;
    }
}
