using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Game;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems.Base;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Services.Arena
{
    /// <summary>
    /// Fetches arena leaderboard from server periodically.
    /// Exposes data for UI binding.
    /// </summary>
    [ActIndependent]
    public partial class ArenaLeaderboardSystem : CivicSystemBase
    {
        private static readonly LogContext Log = new("ArenaLeaderboard");
        private const float FETCH_INTERVAL_SECONDS = 60f;
        private const float CIRCUIT_BREAKER_COOLDOWN_SECONDS = 300f;
        private const double FETCH_JITTER_RANGE = 20.0;

        private TelemetryConfig m_Config = null!;
        private TelemetryAuth m_Auth = null!;
        private ModSettings? m_Settings;
        private RealtimeSeconds m_TimeSinceLastFetch;
        private bool m_Enabled;

        // Cached data
        private string m_CachedLeaderboardJson = "[]";
        private string m_CachedWeeklyJson = "[]";
        private string m_CachedRankTiersJson = "[]";
        private string m_CachedShadowJson = "[]";
        private string m_CachedShadowWeeklyJson = "[]";
        private string m_CachedShadowRankTiersJson = "[]";
        private string m_CachedProsperityJson = "[]";
        private string m_CachedProsperityWeeklyJson = "[]";
        private string m_CachedProsperityRankTiersJson = "[]";
        private string? m_RankTiersConfigVersion;
        private string? m_ShadowRankTiersConfigVersion;
        private string? m_ProsperityRankTiersConfigVersion;
        private int? m_YourPosition;
        private int? m_YourWeeklyPosition;
#pragma warning disable CIVIC150 // Server-backed session state; re-seeded by the first fetch after load
        private int? m_ShadowYourPosition;
        private int? m_ShadowYourWeeklyPosition;
        private int? m_ProsperityYourPosition;
        private int? m_ProsperityYourWeeklyPosition;
#pragma warning restore CIVIC150

        // Ladder progress (Duolingo-style feedback). Confirmed values come from
        // the server's YourStats; the pending delta is accumulated locally from
        // WaveEndedEvent with the SAME formula and clamps the server aggregates
        // with (intercepted x wave number), then shrinks as fetches absorb it.
        // Intentionally NOT persisted: the server is the source of truth — after
        // a load the fetch re-seeds confirmed values, and unconfirmed pending
        // re-confirms itself once the telemetry batch lands server-side.
#pragma warning disable CIVIC150 // Server-backed session state; re-seeded by the first fetch after load (see comment above)
        private long m_ConfirmedScore = -1; // -1 = no server confirmation yet
        private string m_ConfirmedRankTier = string.Empty;
        private long m_PendingScore;
#pragma warning restore CIVIC150
        // Session-start positions: the footer shows movement relative to where
        // the player entered this session ("climbed +3 this session").
        private int? m_AllTimeBaselinePosition;
        private int? m_WeeklyBaselinePosition;

        // Shadow ladder progress (net = income − confiscations; can be negative,
        // so "confirmed" is a separate flag rather than a sentinel value).
#pragma warning disable CIVIC150 // Server-backed session state; re-seeded by the first fetch after load
        private bool m_ShadowConfirmed;
        private long m_ConfirmedNet;
        private string m_ConfirmedShadowRankTier = string.Empty;
        private long m_ShadowPending;
#pragma warning restore CIVIC150

        // Prosperity ladder progress. The pending estimate mirrors the server's
        // per-wave index formula on the same vanilla singletons the wave
        // snapshot samples, so the strip moves the moment a wave starts.
#pragma warning disable CIVIC150 // Server-backed session state; re-seeded by the first fetch after load
        private long m_ConfirmedProsperityScore = -1; // -1 = no server confirmation yet
        private string m_ConfirmedProsperityRankTier = string.Empty;
        private long m_ProsperityPending;
#pragma warning restore CIVIC150
        private readonly object m_Lock = new();

        // Per-wave sanity clamps — mirror CivicServer ArenaRepository so the
        // local estimate never outruns what the server will actually credit.
        private const int MAX_INTERCEPTED_PER_WAVE = 500;
        private const int MAX_WAVE_NUMBER = 200;

        // Ceiling for the locally accumulated shadow estimate: the fetch
        // reconciles it within a minute, so anything beyond this is runaway
        // input, not a real balance (also keeps the long addition bounded).
        private const long MAX_SHADOW_PENDING = 1_000_000_000L;

        // Same runaway ceiling for the prosperity pending index: at most 100
        // per wave, so anything past this is not real play.
        private const long MAX_PROSPERITY_PENDING = 1_000_000L;

        // Rank artwork ships as rank1..rank5; extra config tiers reuse the top icon.
        private const int MAX_RANK_ICON_INDEX = 5;

        // Pre-allocated builders for the board arrays. All are written only
        // under m_Lock; never touched outside that critical section. CIVIC050:
        // avoids per-call StringBuilder allocations in the Build*ArrayJson
        // methods (transitively reachable from OnUpdateImpl via the
        // background-task continuation).
        private readonly StringBuilder m_LeaderboardSb = new StringBuilder(512);
        private readonly StringBuilder m_WeeklySb = new StringBuilder(512);
        private readonly StringBuilder m_ShadowSb = new StringBuilder(512);
        private readonly StringBuilder m_ShadowWeeklySb = new StringBuilder(512);
        private readonly StringBuilder m_ProsperitySb = new StringBuilder(512);
        private readonly StringBuilder m_ProsperityWeeklySb = new StringBuilder(512);

        // Refresh legs — one per fetched board; a manual refresh completes when
        // all legs report (see PumpRefreshCompletions).
        private const int LEG_ALL_TIME = 0;
        private const int LEG_WEEKLY = 1;
        private const int LEG_SHADOW = 2;
        private const int LEG_SHADOW_WEEKLY = 3;
        private const int LEG_PROSPERITY = 4;
        private const int LEG_PROSPERITY_WEEKLY = 5;
        private const int LEG_COUNT = 6;

        private readonly CircuitBreakerState m_LeaderboardBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly CircuitBreakerState m_WeeklyBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly CircuitBreakerState m_ShadowBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly CircuitBreakerState m_ShadowWeeklyBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly CircuitBreakerState m_ProsperityBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly CircuitBreakerState m_ProsperityWeeklyBreaker = new(failureThreshold: 3, cooldownSeconds: CIRCUIT_BREAKER_COOLDOWN_SECONDS);
        private readonly List<ArenaFetchCompletion> m_PendingRefreshCompletions = new();
        private readonly List<ArenaFetchCompletion> m_RefreshCompletionsScratch = new();
        private SchedulerToken m_ActiveRefreshToken;
        private readonly bool[] m_ActiveRefreshLegDone = new bool[LEG_COUNT];
        private readonly bool[] m_ActiveRefreshLegSuccess = new bool[LEG_COUNT];
        private int m_RefreshEpoch;
        private bool m_AcceptRefreshCallbacks;

        public bool IsRefreshInFlight => m_ActiveRefreshToken.IsValid;

        // Vanilla city-level indicators for the local prosperity-index estimate
        // (same singletons the wave snapshot samples server-side).
        private EntityQuery m_CityPopulationQuery;
        private CityStatisticsSystem m_CityStatisticsSystem = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CityPopulationQuery = GetEntityQuery(ComponentType.ReadOnly<Game.City.Population>());
            m_CityStatisticsSystem = World.GetOrCreateSystemManaged<CityStatisticsSystem>();

            // React to the authoritative post-write Online signal so the leaderboard comes
            // up the moment the player enables Online — without needing them to open the
            // arena panel and press refresh. Symmetric with PersonalChronicleSystem /
            // TelemetryService; the Online value is carried in the event (already written by
            // the single writer), so there is no dispatch-order race on the raw command.
            SubscribeRequired<OnlineConnectionStateChangedEvent>(OnOnlineStateChanged);
            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);
            SubscribeRequired<ShadowIncomeAppliedEvent>(OnShadowIncomeApplied);
            SubscribeRequired<WaveStartingEvent>(OnWaveStartingProsperity);
        }

        /// <summary>
        /// Accumulate the local prosperity estimate the moment a wave starts —
        /// the same instant the telemetry snapshot the server scores is taken.
        /// Mirrors the server's per-wave index: happiness × housed share.
        /// </summary>
        private void OnWaveStartingProsperity(WaveStartingEvent evt)
        {
            if (!m_Enabled) return;
            if (!m_CityPopulationQuery.TryGetSingleton<Game.City.Population>(out var cityPop)) return;
            if (cityPop.m_Population <= 0) return;

            int happiness = Math.Min(Math.Max(cityPop.m_AverageHappiness, 0), 100);
            int homeless = 0;
            try
            {
                homeless = Math.Max(m_CityStatisticsSystem.GetStatisticValue(Game.City.StatisticType.HomelessCount), 0);
            }
            catch (Exception ex)
            {
                if (Log.IsDebugEnabled) Log.Debug($" Homeless read failed: {ex.Message}");
            }

            long index = (long)Math.Round(
                happiness * (1.0 - (double)Math.Min(homeless, cityPop.m_Population) / cityPop.m_Population));
            if (index <= 0) return;

            lock (m_Lock)
            {
                m_ProsperityPending = Math.Min(m_ProsperityPending + index, MAX_PROSPERITY_PENDING);
            }
        }

        /// <summary>
        /// Accumulate the local shadow-net estimate as income lands, mirroring
        /// the wave-score pending pattern. Confiscations are not subtracted
        /// locally — the server fetch reconciles the net within a minute.
        /// </summary>
        private void OnShadowIncomeApplied(ShadowIncomeAppliedEvent evt)
        {
            if (!m_Enabled || evt.Amount <= 0) return;

            lock (m_Lock)
            {
                m_ShadowPending = Math.Min(m_ShadowPending + evt.Amount, MAX_SHADOW_PENDING);
            }
        }

        /// <summary>
        /// Accumulate the local score estimate the moment a wave ends, so the
        /// player sees progress immediately instead of a minute later when the
        /// telemetry batch lands and the next fetch confirms it.
        /// </summary>
        private void OnWaveEnded(WaveEndedEvent evt)
        {
            // Offline waves never reach the server ladder — showing pending
            // points that can never be confirmed would be a lie.
            if (!m_Enabled) return;

            long delta = (long)Math.Min(Math.Max(evt.Intercepted, 0), MAX_INTERCEPTED_PER_WAVE)
                * Math.Min(Math.Max(evt.WaveNumber, 1), MAX_WAVE_NUMBER);
            if (delta <= 0) return;

            lock (m_Lock)
            {
                m_PendingScore += delta;
            }
        }

        /// <summary>
        /// Apply the authoritative Online state from the post-write event. Reloads the
        /// durable config fields (server URL, auth) but takes the online gate from the event
        /// (single source, not a settings re-read racing the writer). On enable, kicks an
        /// immediate fetch so the board populates at once.
        /// </summary>
        private void OnOnlineStateChanged(OnlineConnectionStateChangedEvent evt)
        {
            if (!ServiceRegistry.IsInitialized) return;

            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_Config = TelemetryConfig.Load(m_Settings).WithOnlineEnabled(evt.Enabled);
            m_Auth = ServiceRegistry.Instance.Require<TelemetryIdentityService>().GetAuth(m_Config);

            bool wasEnabled = m_Enabled;
            m_Enabled = evt.Enabled;
            if (m_Enabled == wasEnabled) return;

            if (m_Enabled)
            {
                Log.Info(" Online enabled — fetching leaderboard");
                FetchLeaderboard();
                FetchWeekly();
                FetchShadow();
                FetchShadowWeekly();
                FetchProsperity();
                FetchProsperityWeekly();
                m_TimeSinceLastFetch = RealtimeSeconds.Zero;
            }
            else
            {
                Log.Info(" Online disabled — leaderboard polling stopped");
            }
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            Interlocked.Increment(ref m_RefreshEpoch);
            Volatile.Write(ref m_AcceptRefreshCallbacks, true);
            lock (m_Lock)
            {
                m_PendingRefreshCompletions.Clear();
            }

#pragma warning disable CIVIC114 // Scratch list is main-thread-only; OnStartRunning runs on the owner thread.
            m_RefreshCompletionsScratch.Clear();
#pragma warning restore CIVIC114
            ResetActiveRefreshState();
            if (m_Settings != null) return;

            m_Settings = ServiceRegistry.Instance.Require<ModSettings>();
            m_Config = TelemetryConfig.Load(m_Settings);
            m_Auth = ServiceRegistry.Instance.Require<TelemetryIdentityService>().GetAuth(m_Config);
            m_Enabled = m_Config.OnlineEnabled;

            if (!m_Enabled)
            {
                Log.Info(" Disabled (online off)");
                // Initialize the rank-tier ladder STRUCTURE from local balance
                // config (icon + threshold pairs). These are static ladder
                // metadata, NOT leaderboard rows. Live leaderboard rows remain
                // JsonBuilder.EmptyArray until the first successful server
                // fetch — the UI surfaces "live data unavailable" rather than
                // placeholder rows. No fabricated data path exists in this
                // method; it never returns mock leaderboard entries.
                BuildRankTiersJson();
                BuildShadowRankTiersJson();
                BuildProsperityRankTiersJson();
                return;
            }

            Log.Info(" Initialized — fetching from server");
            BuildRankTiersJson();
            BuildShadowRankTiersJson();
            BuildProsperityRankTiersJson();

            // Initial fetch
            FetchLeaderboard();
            FetchWeekly();
            FetchShadow();
            FetchShadowWeekly();
            FetchProsperity();
            FetchProsperityWeekly();
        }

        protected override void OnUpdateImpl()
        {
            PumpRefreshCompletions();
            if (!m_Enabled) return;

            if (RealtimeSeconds.TryCreate(UnityEngine.Time.deltaTime, out var frameDelta, clampNegative: true))
            {
                m_TimeSinceLastFetch = RealtimeSeconds.TryCreate(
                    m_TimeSinceLastFetch.Value + frameDelta.Value,
                    out var elapsed,
                    clampNegative: true)
                        ? elapsed
                        : RealtimeSeconds.Zero;
            }

            if (m_TimeSinceLastFetch.Value >= FETCH_INTERVAL_SECONDS)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                bool attempted = false;
                if (m_LeaderboardBreaker.CanProceed(now))
                {
                    FetchLeaderboard();
                    attempted = true;
                }
                if (m_WeeklyBreaker.CanProceed(now))
                {
                    FetchWeekly();
                    attempted = true;
                }
                if (m_ShadowBreaker.CanProceed(now))
                {
                    FetchShadow();
                    attempted = true;
                }
                if (m_ShadowWeeklyBreaker.CanProceed(now))
                {
                    FetchShadowWeekly();
                    attempted = true;
                }
                if (m_ProsperityBreaker.CanProceed(now))
                {
                    FetchProsperity();
                    attempted = true;
                }
                if (m_ProsperityWeeklyBreaker.CanProceed(now))
                {
                    FetchProsperityWeekly();
                    attempted = true;
                }
                if (attempted)
                    m_TimeSinceLastFetch = RealtimeSeconds.TryCreate(
                        (float)(ThreadSafeRandom.NextDouble() * FETCH_JITTER_RANGE - 10.0),
                        out var jitter,
                        clampNegative: true)
                            ? jitter
                            : RealtimeSeconds.Zero;
            }
        }

        /// <summary>
        /// Force immediate leaderboard refresh on next update cycle.
        /// Called from ArenaUISystem on manual refresh button.
        /// </summary>
        public bool ForceRefresh()
        {
            RefreshTelemetryConfig();
            if (!m_Enabled)
            {
                Log.Info(" Refresh ignored: online disabled");
                return false;
            }
            m_TimeSinceLastFetch = RealtimeSeconds.TryCreate(FETCH_INTERVAL_SECONDS, out var immediateRefreshDelay)
                ? immediateRefreshDelay
                : RealtimeSeconds.Zero;
            return true;
        }

        public bool ForceRefresh(int requestId)
        {
            RefreshTelemetryConfig();
            if (!m_Enabled || requestId <= 0)
            {
                Log.Info(" Refresh ignored: online disabled or invalid request");
                return false;
            }

            var requestToken = new SchedulerToken(requestId);
            if (m_ActiveRefreshToken.IsValid && !m_ActiveRefreshToken.Equals(requestToken))
                return false;

            m_ActiveRefreshToken = requestToken;
            for (int i = 0; i < LEG_COUNT; i++)
            {
                m_ActiveRefreshLegDone[i] = false;
                m_ActiveRefreshLegSuccess[i] = false;
            }
            FetchLeaderboard(requestId);
            FetchWeekly(requestId);
            FetchShadow(requestId);
            FetchShadowWeekly(requestId);
            FetchProsperity(requestId);
            FetchProsperityWeekly(requestId);
            m_TimeSinceLastFetch = RealtimeSeconds.Zero;
            return true;
        }

        public bool CanRefresh
        {
            get
            {
                RefreshTelemetryConfig();
                return m_Enabled;
            }
        }

        private void RefreshTelemetryConfig()
        {
            var nextConfig = TelemetryConfig.Load(m_Settings);
            var nextEnabled = nextConfig.OnlineEnabled;
            var needsAuthReload =
                m_Config == null ||
                !string.Equals(m_Config.ServerUrl, nextConfig.ServerUrl, StringComparison.Ordinal) ||
                m_Config.OnlineEnabled != nextConfig.OnlineEnabled;

            m_Config = nextConfig;
            if (needsAuthReload)
            {
                m_Auth = ServiceRegistry.Instance.Require<TelemetryIdentityService>().GetAuth(m_Config);
            }

            if (m_Enabled != nextEnabled)
            {
                m_Enabled = nextEnabled;
                Log.Info(m_Enabled
                    ? " Runtime online enabled for leaderboard refresh"
                    : " Runtime online disabled for leaderboard refresh");
            }
        }

        #region HTTP Fetch

        /// <summary>
        /// Shared board-fetch core (fire-and-forget by design): epoch guard,
        /// circuit-breaker probe, HTTP GET on a background thread, and refresh
        /// completion for the given leg. <paramref name="parseAndStore"/> parses
        /// the raw response and stores it under m_Lock; it re-checks the passed
        /// keep-epoch callback around and inside the lock and returns false when
        /// the epoch died mid-way (the callback has already cancelled the probe).
        /// </summary>
        private void FetchBoard(
            string relativePath,
            string warnPrefix,
            CircuitBreakerState breaker,
            int leg,
            int refreshRequestId,
            Func<string, Func<bool>, bool> parseAndStore)
        {
            int refreshEpoch = Volatile.Read(ref m_RefreshEpoch);
            if (!IsRefreshEpochCurrent(refreshEpoch))
                return;

            var playerId = GetPlayerId();
            var url = $"{m_Config.ServerUrl}{relativePath}";
            if (!string.IsNullOrEmpty(playerId))
            {
                url += $"?player_id={Uri.EscapeDataString(playerId)}";
            }

            // FIX H79: Cache main-thread time — realtimeSinceStartup is not safe from ThreadPool
            float cachedTime = UnityEngine.Time.realtimeSinceStartup;
            if (!breaker.TryBeginProbe(cachedTime, out var probe))
            {
                QueueRefreshCompletion(refreshRequestId, refreshEpoch, leg, success: false);
                return;
            }
            var terminalDelivered = new int[1];
            bool KeepRefreshEpochOrCancelProbe()
            {
                if (IsRefreshEpochCurrent(refreshEpoch))
                    return true;

                CompleteProbeOnce(terminalDelivered, () => breaker.CancelProbe(probe));
                return false;
            }

            BackgroundTask.Run(() =>
            {
                if (!KeepRefreshEpochOrCancelProbe())
                    return;

                var result = HttpUtils.Get(url);
                if (!KeepRefreshEpochOrCancelProbe())
                    return;

                if (!result.Success)
                {
                    Log.Warn($" {warnPrefix} fetch failed: {result.ErrorMessage}");
                    CompleteProbeOnce(terminalDelivered, () => breaker.RecordFailure(probe));
                    QueueRefreshCompletion(refreshRequestId, refreshEpoch, leg, success: false);
                    return;
                }

                if (!KeepRefreshEpochOrCancelProbe()) return;
                try
                {
                    if (!parseAndStore(result.Response, KeepRefreshEpochOrCancelProbe))
                        return;

                    if (!KeepRefreshEpochOrCancelProbe())
                        return;

                    CompleteProbeOnce(terminalDelivered, () => breaker.RecordSuccess(probe));
                }
                catch (Exception ex)
                {
                    if (!KeepRefreshEpochOrCancelProbe())
                        return;

                    Log.Warn($" {warnPrefix} parse failed: {ex}");
                    CompleteProbeOnce(terminalDelivered, () => breaker.RecordFailure(probe));
                    QueueRefreshCompletion(refreshRequestId, refreshEpoch, leg, success: false);
                    return;
                }

                QueueRefreshCompletion(refreshRequestId, refreshEpoch, leg, success: true);
            }, () => CompleteProbeOnce(terminalDelivered, () => breaker.CancelProbe(probe)));
        }

        private void FetchLeaderboard(int refreshRequestId = 0)
        {
            FetchBoard("/arena/leaderboard", "All-time", m_LeaderboardBreaker, LEG_ALL_TIME, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedLeaderboardJson = BuildLeaderboardArrayJson(response.Leaderboard);
                    m_YourPosition = response.YourPosition;
                    // First position seen this session anchors the "climbed +N" delta.
                    m_AllTimeBaselinePosition ??= response.YourPosition;

                    // Server-confirmed score absorbs the local pending estimate:
                    // whatever growth the server acknowledged is subtracted, the
                    // (clamp-level) remainder keeps showing until the next fetch.
                    var stats = response.YourStats;
                    if (stats != null)
                    {
                        if (m_ConfirmedScore >= 0 && stats.Score > m_ConfirmedScore)
                            m_PendingScore = Math.Max(0, m_PendingScore - (stats.Score - m_ConfirmedScore));
                        m_ConfirmedScore = stats.Score;
                        m_ConfirmedRankTier = stats.RankTier ?? string.Empty;
                    }
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched all-time leaderboard, your pos={m_YourPosition}");
                return true;
            });
        }

        private void FetchWeekly(int refreshRequestId = 0)
        {
            FetchBoard("/arena/leaderboard/weekly", "Weekly", m_WeeklyBreaker, LEG_WEEKLY, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseWeeklyLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedWeeklyJson = BuildWeeklyLeaderboardArrayJson(response.Leaderboard);
                    m_YourWeeklyPosition = response.YourPosition;
                    m_WeeklyBaselinePosition ??= response.YourPosition;
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched weekly leaderboard, your pos={m_YourWeeklyPosition}");
                return true;
            });
        }

        private void FetchShadow(int refreshRequestId = 0)
        {
            FetchBoard("/arena/shadow/leaderboard", "Shadow", m_ShadowBreaker, LEG_SHADOW, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseShadowLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedShadowJson = BuildShadowArrayJson(response.Leaderboard);
                    m_ShadowYourPosition = response.YourPosition;

                    // Same absorb rule as the defense score: confirmed net growth
                    // eats the local pending income estimate.
                    var stats = response.YourStats;
                    if (stats != null)
                    {
                        if (m_ShadowConfirmed && stats.Net > m_ConfirmedNet)
                            m_ShadowPending = Math.Max(0, m_ShadowPending - (stats.Net - m_ConfirmedNet));
                        m_ShadowConfirmed = true;
                        m_ConfirmedNet = stats.Net;
                        m_ConfirmedShadowRankTier = stats.RankTier ?? string.Empty;
                    }
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched shadow leaderboard, your pos={m_ShadowYourPosition}");
                return true;
            });
        }

        private void FetchShadowWeekly(int refreshRequestId = 0)
        {
            FetchBoard("/arena/shadow/leaderboard/weekly", "Shadow weekly", m_ShadowWeeklyBreaker, LEG_SHADOW_WEEKLY, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseWeeklyShadowLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedShadowWeeklyJson = BuildShadowWeeklyArrayJson(response.Leaderboard);
                    m_ShadowYourWeeklyPosition = response.YourPosition;
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched shadow weekly leaderboard, your pos={m_ShadowYourWeeklyPosition}");
                return true;
            });
        }

        private void FetchProsperity(int refreshRequestId = 0)
        {
            FetchBoard("/arena/prosperity/leaderboard", "Prosperity", m_ProsperityBreaker, LEG_PROSPERITY, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseProsperityLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedProsperityJson = BuildProsperityArrayJson(response.Leaderboard);
                    m_ProsperityYourPosition = response.YourPosition;

                    // Same absorb rule as the defense score: confirmed growth
                    // eats the local pending per-wave index estimate.
                    var stats = response.YourStats;
                    if (stats != null)
                    {
                        if (m_ConfirmedProsperityScore >= 0 && stats.Score > m_ConfirmedProsperityScore)
                            m_ProsperityPending = Math.Max(0, m_ProsperityPending - (stats.Score - m_ConfirmedProsperityScore));
                        m_ConfirmedProsperityScore = stats.Score;
                        m_ConfirmedProsperityRankTier = stats.RankTier ?? string.Empty;
                    }
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched prosperity leaderboard, your pos={m_ProsperityYourPosition}");
                return true;
            });
        }

        private void FetchProsperityWeekly(int refreshRequestId = 0)
        {
            FetchBoard("/arena/prosperity/leaderboard/weekly", "Prosperity weekly", m_ProsperityWeeklyBreaker, LEG_PROSPERITY_WEEKLY, refreshRequestId, (raw, keep) =>
            {
                var response = ArenaWireReader.ParseWeeklyProsperityLeaderboardResponse(raw);
                if (!keep()) return false;

                lock (m_Lock)
                {
                    if (!keep()) return false;

                    m_CachedProsperityWeeklyJson = BuildProsperityWeeklyArrayJson(response.Leaderboard);
                    m_ProsperityYourWeeklyPosition = response.YourPosition;
                }

                if (Log.IsDebugEnabled) Log.Debug($" Fetched prosperity weekly leaderboard, your pos={m_ProsperityYourWeeklyPosition}");
                return true;
            });
        }

        private bool IsRefreshEpochCurrent(int refreshEpoch)
        {
            return Volatile.Read(ref m_AcceptRefreshCallbacks) &&
                Volatile.Read(ref m_RefreshEpoch) == refreshEpoch &&
                !Mod.IsUnloading;
        }

        private static void CompleteProbeOnce(int[] terminalDelivered, Action terminal)
        {
            if (Interlocked.Exchange(ref terminalDelivered[0], 1) != 0)
                return;

            terminal();
        }

        private readonly struct ArenaFetchCompletion
        {
            public readonly SchedulerToken RequestToken;
            public readonly int RefreshEpoch;
            public readonly int Leg;
            public readonly bool Success;

            public ArenaFetchCompletion(int requestId, int refreshEpoch, int leg, bool success)
            {
                RequestToken = new SchedulerToken(requestId);
                RefreshEpoch = refreshEpoch;
                Leg = leg;
                Success = success;
            }
        }

        private void QueueRefreshCompletion(int requestId, int refreshEpoch, int leg, bool success)
        {
            if (requestId <= 0 || !IsRefreshEpochCurrent(refreshEpoch))
                return;

            lock (m_Lock)
            {
                if (!IsRefreshEpochCurrent(refreshEpoch))
                    return;

                m_PendingRefreshCompletions.Add(new ArenaFetchCompletion(requestId, refreshEpoch, leg, success));
            }
        }

        internal void PumpRefreshCompletions()
        {
            int refreshEpoch = Volatile.Read(ref m_RefreshEpoch);
#pragma warning disable CIVIC114 // Scratch list is main-thread-only; lock protects only the producer queue.
            m_RefreshCompletionsScratch.Clear();
            lock (m_Lock)
            {
                if (m_PendingRefreshCompletions.Count > 0)
                {
                    m_RefreshCompletionsScratch.AddRange(m_PendingRefreshCompletions);
                    m_PendingRefreshCompletions.Clear();
                }
            }
#pragma warning restore CIVIC114

            foreach (var completion in m_RefreshCompletionsScratch)
            {
                if (completion.RefreshEpoch != refreshEpoch)
                    continue;

                if (!completion.RequestToken.Equals(m_ActiveRefreshToken))
                    continue;

                if (completion.Leg < 0 || completion.Leg >= LEG_COUNT)
                    continue;

                m_ActiveRefreshLegDone[completion.Leg] = true;
                m_ActiveRefreshLegSuccess[completion.Leg] = completion.Success;
            }

            if (!m_ActiveRefreshToken.IsValid || !IsRefreshEpochCurrent(refreshEpoch))
                return;

            bool allDone = true;
            bool success = true;
            for (int i = 0; i < LEG_COUNT; i++)
            {
                allDone &= m_ActiveRefreshLegDone[i];
                success &= m_ActiveRefreshLegSuccess[i];
            }
            if (!allDone)
                return;
            RequestResultBridge.PublishTerminalForBegun(
                RequestResultBridge.ArenaRefresh,
                m_ActiveRefreshToken.Value,
                success ? RequestStatus.Success : RequestStatus.Failed,
                success ? ReasonId.None : ReasonIds.ArenaPartialRefresh);

            ResetActiveRefreshState();
        }

        private void ResetActiveRefreshState()
        {
            m_ActiveRefreshToken = default;
            for (int i = 0; i < LEG_COUNT; i++)
            {
                m_ActiveRefreshLegDone[i] = false;
                m_ActiveRefreshLegSuccess[i] = false;
            }
        }

        private string? GetPlayerId()
        {
            var playerId = m_Auth?.PlayerId;
            return string.IsNullOrWhiteSpace(playerId) ? null : playerId;
        }

        #endregion

        #region Rank Tiers (from balance config)

        /// <summary>
        /// Rebuild the tier list from BalanceConfig (Arena.RankThresholds — the
        /// same contract source the server labels ranks with). Icons rank1..rank5
        /// are assigned by ascending threshold; the emitted array is highest tier
        /// first, matching the UI's display order. Main-thread only.
        /// </summary>
        private void BuildRankTiersJson()
        {
            var config = Core.Config.BalanceConfig.Current;
            var thresholds = config.Arena.RankThresholds;
            if (thresholds == null || thresholds.Count == 0)
            {
                m_CachedRankTiersJson = JsonBuilder.EmptyArray;
                return;
            }

            m_CachedRankTiersJson = BuildTiersJson(thresholds);
            m_RankTiersConfigVersion = config.Version;
        }

        /// <summary>
        /// Rebuild the shadow-ladder tier list from BalanceConfig
        /// (Arena.ShadowRankThresholds). Main-thread only.
        /// </summary>
        private void BuildShadowRankTiersJson()
        {
            var config = Core.Config.BalanceConfig.Current;
            var thresholds = config.Arena.ShadowRankThresholds;
            if (thresholds == null || thresholds.Count == 0)
            {
                m_CachedShadowRankTiersJson = JsonBuilder.EmptyArray;
                return;
            }

            m_CachedShadowRankTiersJson = BuildTiersJson(thresholds);
            m_ShadowRankTiersConfigVersion = config.Version;
        }

        /// <summary>
        /// Rebuild the prosperity-ladder tier list from BalanceConfig
        /// (Arena.ProsperityRankThresholds). Main-thread only.
        /// </summary>
        private void BuildProsperityRankTiersJson()
        {
            var config = Core.Config.BalanceConfig.Current;
            var thresholds = config.Arena.ProsperityRankThresholds;
            if (thresholds == null || thresholds.Count == 0)
            {
                m_CachedProsperityRankTiersJson = JsonBuilder.EmptyArray;
                return;
            }

            m_CachedProsperityRankTiersJson = BuildTiersJson(thresholds);
            m_ProsperityRankTiersConfigVersion = config.Version;
        }

        private static string BuildTiersJson(Dictionary<string, int> thresholds)
        {
            var ascending = new List<KeyValuePair<string, int>>(thresholds);
            ascending.Sort((a, b) => a.Value.CompareTo(b.Value));

            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = ascending.Count - 1; i >= 0; i--)
            {
                if (i < ascending.Count - 1) sb.Append(',');
                var dto = new RankTierDto
                {
                    Name = ascending[i].Key,
                    MinScore = ascending[i].Value,
                    Icon = $"rank{Math.Min(i + 1, MAX_RANK_ICON_INDEX)}",
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_LeaderboardSb (CIVIC050).
        private string BuildLeaderboardArrayJson(List<LeaderboardEntry> entries)
        {
            // Intercepted/SuccessRate stay wire-only — the panel does not show them.
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_LeaderboardSb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new LeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    WavesSurvived = e.WavesSurvived,
                    Score = e.Score,
                    RankTier = e.RankTier ?? string.Empty,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_ShadowSb (CIVIC050).
        private string BuildShadowArrayJson(List<ShadowLeaderboardEntry> entries)
        {
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_ShadowSb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new ShadowLeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    Earned = e.Earned,
                    Confiscated = e.Confiscated,
                    Net = e.Net,
                    RankTier = e.RankTier ?? string.Empty,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_ShadowWeeklySb (CIVIC050).
        private string BuildShadowWeeklyArrayJson(List<WeeklyShadowLeaderboardEntry> entries)
        {
            // WeekStart is intentionally not emitted — UI does not consume it.
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_ShadowWeeklySb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new WeeklyShadowLeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    Net = e.Net,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_ProsperitySb (CIVIC050).
        private string BuildProsperityArrayJson(List<ProsperityLeaderboardEntry> entries)
        {
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_ProsperitySb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new ProsperityLeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    Waves = e.Waves,
                    AvgIndex = e.AvgIndex,
                    Score = e.Score,
                    RankTier = e.RankTier ?? string.Empty,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_ProsperityWeeklySb (CIVIC050).
        private string BuildProsperityWeeklyArrayJson(List<WeeklyProsperityLeaderboardEntry> entries)
        {
            // WeekStart is intentionally not emitted — UI does not consume it.
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_ProsperityWeeklySb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new WeeklyProsperityLeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    Score = e.Score,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Caller holds m_Lock; reuses m_WeeklySb (CIVIC050).
        private string BuildWeeklyLeaderboardArrayJson(List<WeeklyLeaderboardEntry> entries)
        {
            // WeekStart is intentionally not emitted — UI does not consume it.
            if (entries == null || entries.Count == 0) return JsonBuilder.EmptyArray;
            var sb = m_WeeklySb;
            sb.Clear();
            sb.Append('[');
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                var dto = new WeeklyLeaderboardEntryDto
                {
                    Position = e.Position,
                    Nickname = e.Nickname ?? string.Empty,
                    WavesSurvived = e.WavesSurvived,
                    Score = e.Score,
                };
                dto.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        #endregion


        #region Public API

        /// <summary>
        /// Get all-time leaderboard as JSON array.
        /// </summary>
        public string GetLeaderboardJson()
        {
            lock (m_Lock)
            {
                return m_CachedLeaderboardJson;
            }
        }

        /// <summary>
        /// Get weekly leaderboard as JSON array.
        /// </summary>
        public string GetWeeklyJson()
        {
            lock (m_Lock)
            {
                return m_CachedWeeklyJson;
            }
        }

        /// <summary>
        /// Get rank tiers as JSON array. Rebuilds when the balance config
        /// version changes (remote config can land after startup). Called from
        /// the UI system on the main thread only.
        /// </summary>
        public string GetRankTiersJson()
        {
            if (!string.Equals(m_RankTiersConfigVersion, Core.Config.BalanceConfig.Current.Version, StringComparison.Ordinal))
                BuildRankTiersJson();
            return m_CachedRankTiersJson;
        }

        /// <summary>
        /// Get shadow-ladder rank tiers as JSON array. Main-thread only.
        /// </summary>
        public string GetShadowRankTiersJson()
        {
            if (!string.Equals(m_ShadowRankTiersConfigVersion, Core.Config.BalanceConfig.Current.Version, StringComparison.Ordinal))
                BuildShadowRankTiersJson();
            return m_CachedShadowRankTiersJson;
        }

        /// <summary>
        /// Get the all-time shadow ladder as JSON array.
        /// </summary>
        public string GetShadowJson()
        {
            lock (m_Lock)
            {
                return m_CachedShadowJson;
            }
        }

        /// <summary>
        /// Get the weekly shadow ladder as JSON array.
        /// </summary>
        public string GetShadowWeeklyJson()
        {
            lock (m_Lock)
            {
                return m_CachedShadowWeeklyJson;
            }
        }

        /// <summary>
        /// Your position on the all-time shadow ladder.
        /// </summary>
        public int? ShadowYourPosition
        {
            get { lock (m_Lock) { return m_ShadowYourPosition; } }
        }

        /// <summary>
        /// Your position on the weekly shadow ladder.
        /// </summary>
        public int? ShadowYourWeeklyPosition
        {
            get { lock (m_Lock) { return m_ShadowYourWeeklyPosition; } }
        }

        /// <summary>
        /// True once the server confirmed shadow YourStats at least once this
        /// session (net can legitimately be negative, so no value sentinel).
        /// </summary>
        public bool ShadowConfirmed
        {
            get { lock (m_Lock) { return m_ShadowConfirmed; } }
        }

        /// <summary>
        /// Server-confirmed net shadow capital (income − confiscations).
        /// </summary>
        public long ShadowNet
        {
            get { lock (m_Lock) { return m_ConfirmedNet; } }
        }

        /// <summary>
        /// Shadow income accumulated locally since the last confirmation.
        /// </summary>
        public long ShadowPending
        {
            get { lock (m_Lock) { return m_ShadowPending; } }
        }

        /// <summary>
        /// Server-confirmed shadow rank title; empty until the first fetch.
        /// </summary>
        public string ShadowRankTier
        {
            get { lock (m_Lock) { return m_ConfirmedShadowRankTier; } }
        }

        /// <summary>
        /// Get prosperity-ladder rank tiers as JSON array. Main-thread only.
        /// </summary>
        public string GetProsperityRankTiersJson()
        {
            if (!string.Equals(m_ProsperityRankTiersConfigVersion, Core.Config.BalanceConfig.Current.Version, StringComparison.Ordinal))
                BuildProsperityRankTiersJson();
            return m_CachedProsperityRankTiersJson;
        }

        /// <summary>
        /// Get the all-time prosperity ladder as JSON array.
        /// </summary>
        public string GetProsperityJson()
        {
            lock (m_Lock)
            {
                return m_CachedProsperityJson;
            }
        }

        /// <summary>
        /// Get the weekly prosperity ladder as JSON array.
        /// </summary>
        public string GetProsperityWeeklyJson()
        {
            lock (m_Lock)
            {
                return m_CachedProsperityWeeklyJson;
            }
        }

        /// <summary>
        /// Your position on the all-time prosperity ladder.
        /// </summary>
        public int? ProsperityYourPosition
        {
            get { lock (m_Lock) { return m_ProsperityYourPosition; } }
        }

        /// <summary>
        /// Your position on the weekly prosperity ladder.
        /// </summary>
        public int? ProsperityYourWeeklyPosition
        {
            get { lock (m_Lock) { return m_ProsperityYourWeeklyPosition; } }
        }

        /// <summary>
        /// Server-confirmed prosperity score; -1 until the first fetch that
        /// carries YourStats (player ranked and identified).
        /// </summary>
        public long ProsperityConfirmedScore
        {
            get { lock (m_Lock) { return m_ConfirmedProsperityScore; } }
        }

        /// <summary>
        /// Locally accumulated prosperity index since the last server
        /// confirmation (per-wave index at wave start, server formula mirrored).
        /// </summary>
        public long ProsperityPending
        {
            get { lock (m_Lock) { return m_ProsperityPending; } }
        }

        /// <summary>
        /// Server-confirmed prosperity rank title; empty until the first fetch.
        /// </summary>
        public string ProsperityRankTier
        {
            get { lock (m_Lock) { return m_ConfirmedProsperityRankTier; } }
        }

        /// <summary>
        /// Your position in all-time leaderboard.
        /// </summary>
        public int? YourPosition
        {
            get { lock (m_Lock) { return m_YourPosition; } }
        }

        /// <summary>
        /// Your position in weekly leaderboard.
        /// </summary>
        public int? YourWeeklyPosition
        {
            get { lock (m_Lock) { return m_YourWeeklyPosition; } }
        }

        /// <summary>
        /// Server-confirmed ladder score; -1 until the first fetch that carries
        /// YourStats (player ranked and identified).
        /// </summary>
        public long YourConfirmedScore
        {
            get { lock (m_Lock) { return m_ConfirmedScore; } }
        }

        /// <summary>
        /// Locally accumulated score since the last server confirmation
        /// (intercepted x wave number per ended wave, server clamps mirrored).
        /// </summary>
        public long PendingScore
        {
            get { lock (m_Lock) { return m_PendingScore; } }
        }

        /// <summary>
        /// Server-confirmed rank title; empty until the first YourStats fetch.
        /// </summary>
        public string YourRankTier
        {
            get { lock (m_Lock) { return m_ConfirmedRankTier; } }
        }

        /// <summary>
        /// Positions climbed on the all-time board since the session's first
        /// fetch (positive = up). 0 while unknown.
        /// </summary>
        public int AllTimePositionDelta
        {
            get
            {
                lock (m_Lock)
                {
                    return m_AllTimeBaselinePosition.HasValue && m_YourPosition.HasValue
                        ? m_AllTimeBaselinePosition.Value - m_YourPosition.Value
                        : 0;
                }
            }
        }

        /// <summary>
        /// Positions climbed on the weekly board since the session's first
        /// fetch (positive = up). 0 while unknown.
        /// </summary>
        public int WeeklyPositionDelta
        {
            get
            {
                lock (m_Lock)
                {
                    return m_WeeklyBaselinePosition.HasValue && m_YourWeeklyPosition.HasValue
                        ? m_WeeklyBaselinePosition.Value - m_YourWeeklyPosition.Value
                        : 0;
                }
            }
        }

        #endregion

        protected override void OnDestroy()
        {
            UnsubscribeSafe<OnlineConnectionStateChangedEvent>(OnOnlineStateChanged);
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            UnsubscribeSafe<ShadowIncomeAppliedEvent>(OnShadowIncomeApplied);
            UnsubscribeSafe<WaveStartingEvent>(OnWaveStartingProsperity);
            Volatile.Write(ref m_AcceptRefreshCallbacks, false);
            Interlocked.Increment(ref m_RefreshEpoch);
            lock (m_Lock)
            {
                m_PendingRefreshCompletions.Clear();
            }

#pragma warning disable CIVIC114 // Scratch list is main-thread-only; OnDestroy runs on the owner thread.
            m_RefreshCompletionsScratch.Clear();
#pragma warning restore CIVIC114
            ResetActiveRefreshState();
            base.OnDestroy();
        }
    }
}
