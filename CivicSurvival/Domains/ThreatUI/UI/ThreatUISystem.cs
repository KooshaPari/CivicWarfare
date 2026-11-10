using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using CivicSurvival.Localization;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Adapters;
using CivicSurvival.Core.Systems.CameraTracking;
using CivicSurvival.Domains.ThreatUI.Systems;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Interfaces.Domain.Cognitive;
using static CivicSurvival.Core.UI.B;
using CivicSurvival.Core.Attributes;
using RuntimeRadarThreat = CivicSurvival.Core.Utils.RadarThreatDto;
using RuntimeRadarTarget = CivicSurvival.Core.Utils.RadarTargetDto;
using RuntimeRadarDefense = CivicSurvival.Core.Utils.RadarDefenseDto;
using RuntimeThreatTarget = CivicSurvival.Core.Utils.ThreatTargetDto;
using WireRadarThreat = CivicSurvival.Core.UI.DomainState.RadarThreatDto;
using WireRadarTarget = CivicSurvival.Core.UI.DomainState.RadarTargetDto;
using WireRadarDefense = CivicSurvival.Core.UI.DomainState.RadarDefenseDto;
using WireThreatTarget = CivicSurvival.Core.UI.DomainState.ThreatTargetDto;
using WireMapBounds = CivicSurvival.Core.UI.DomainState.MapBoundsDto;
using WireRadarInterception = CivicSurvival.Core.UI.DomainState.RadarInterceptionDto;
using RuntimeRadarBroadcast = CivicSurvival.Core.Utils.RadarBroadcastDto;
using WireRadarBroadcast = CivicSurvival.Core.UI.DomainState.RadarBroadcastDto;

namespace CivicSurvival.Domains.ThreatUI.UI
{
    /// <summary>
    /// UI system for threat/wave system status.
    /// Reads from WaveStateSingleton, ThreatStatsSingleton, InterceptStatsSingleton, ThreatOutcomeStatsSingleton.
    ///
    /// Migrated from ThreatUIPanel → CivicUIPanelSystem.
    /// Gains: auto-disposed EntityQueries, proper ECS lifecycle.
    /// </summary>
    [ActIndependent]
#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
    public partial class ThreatUISystem : CivicUIPanelSystem, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // Dependencies
        private EntityQuery m_WaveStateQuery;
        private EntityQuery m_ThreatStatsQuery;
        private EntityQuery m_InterceptStatsQuery;
        private EntityQuery m_ThreatOutcomeStatsQuery;
#if DEBUG
        // Removed direct reference to WaveExecutor
#endif
        private IThreatTargetReader m_TargetReader = null!;
        private ThreatIdentifySystem m_IdentifySystem = null!;
        private IThreatRadarReader m_RadarReader = null!;
        private IBroadcastCoverageReader m_BroadcastReader = null!;
        private IMapContourReader m_MapContourReader = null!;
        // Coastline contour is static per loaded city — publish its JSON to the UI
        // exactly once (when the producer has it), then stop touching the binding.
        private bool m_MapContourPublished;
        private CameraFocusState? m_CameraService;
        private CameraTrackingSystem? m_CameraTracking;
        [NonSerialized] private CivicDependencyWire m_DependencyWire = null!;

        // BUG-T-016 fix: Track frame count to periodically refresh map bounds
        private int m_FramesSinceMapBoundsUpdate;
        // W6-M4: 10 panel ticks × 0.5s = 5s (was UPDATE_INTERVAL_5_SECONDS=300 sim-frames × 0.5s = 150s)
        private const int MAP_BOUNDS_UPDATE_INTERVAL = 10;

        // Debriefing state (captured on WaveEndedEvent)
        private int m_LastDebriefingWave;
        private int m_LastDebriefingIntercepted;
        private int m_LastDebriefingHits;
        private int m_LastDebriefingShotsFired;
        private int m_LastDebriefingCasualties;
        private int m_LastDebriefingDamageCost;
        private long m_LastDebriefingInfraDamageCost;
        private int m_LastDebriefingCrashed;
        // Ephemeral — intentionally NOT serialized. Lost on save/load (S7-07).
        private bool m_ShowDebriefing;
        private bool m_DebriefingVisibleInCoordinator;
        // S7-04: Minimum display duration before auto-dismiss (seconds)
        private const float DEBRIEFING_MIN_DISPLAY = 10f;
        private double m_DebriefingShownTime;

        // PERF: Cached JSON
        private string m_CachedThreatTargetsJson = JsonBuilder.EmptyArray;
        private string m_CachedRadarThreatsJson = JsonBuilder.EmptyArray;
        private string m_CachedRadarTargetsJson = JsonBuilder.EmptyArray;
        private string m_CachedRadarDefensesJson = JsonBuilder.EmptyArray;
        private string m_CachedRadarBroadcastsJson = JsonBuilder.EmptyArray;
        private string m_CachedRadarInterceptionsJson = JsonBuilder.EmptyArray;

        // Interception flash markers ring buffer (max 20, auto-expire after 10s)
        private const int MAX_INTERCEPTIONS = 20;
        private const float INTERCEPTION_LIFETIME = 10f;
        private readonly List<InterceptionMarker> m_InterceptionMarkers = new(MAX_INTERCEPTIONS);

        private struct InterceptionMarker
        {
            public float X;
            public float Z;
            public double Timestamp; // ElapsedTime when interception occurred
            public bool Success;    // true = intercepted, false reserved for future miss-marker
        }

        private int m_RadarObserverCursor;
        private int m_TargetObserverCursor;
        private double m_LastSeenElapsedTime;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_WaveStateQuery = GetEntityQuery(
                ComponentType.ReadOnly<WaveStateSingleton>());
            m_ThreatStatsQuery = GetEntityQuery(
                ComponentType.ReadOnly<ThreatStatsSingleton>());
            m_InterceptStatsQuery = GetEntityQuery(
                ComponentType.ReadOnly<InterceptStatsSingleton>());
            m_ThreatOutcomeStatsQuery = GetEntityQuery(
                ComponentType.ReadOnly<ThreatOutcomeStatsSingleton>());

#if DEBUG
            // No longer using WaveExecutor directly
#endif

            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);
            SubscribeRequired<GameOverEvent>(OnGameOver);
            SubscribeRequired<ThreatInterceptEvent>(OnThreatIntercept);
            m_DependencyWire = new CivicDependencyWire(nameof(ThreatUISystem));

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_IdentifySystem ??= m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<ThreatIdentifySystem>());
            // Route NullObject-backed service lookups through the wire so they
            // also re-resolve on demand (CIVIC434). Always returns non-null
            // (Null object fallback) — wire just provides the consistent
            // dependency-resolution surface for the whole class.
            m_TargetReader = m_DependencyWire.RequireWired(() =>
                ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullThreatTargetReader.Instance));
            m_RadarReader = m_DependencyWire.RequireWired(() =>
                ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullThreatRadarReader.Instance));
            m_BroadcastReader = m_DependencyWire.RequireWired(() =>
                ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullBroadcastCoverageReader.Instance));
            m_CameraService = m_DependencyWire.RequireWired(() => ServiceRegistry.Instance.Require<CameraFocusState>());
            // Wired (not a bare GetExistingSystemManaged) because it's now read in
            // OnPanelUpdate for the radar camera marker, not only in click triggers.
            m_CameraTracking = m_DependencyWire.RequireWired(() => World.GetExistingSystemManaged<CameraTrackingSystem>());
            m_MapContourReader = m_DependencyWire.RequireWired(() => ServiceRegistry.Instance.Require<IMapContourReader>());
            // Republish the contour for the freshly started city; the producer recomputes
            // it on load, so re-arm our one-shot publish gate.
            m_MapContourPublished = false;
            m_FramesSinceMapBoundsUpdate = 0;
        }

        protected override void ConfigureBindings()
        {
            Bindings.Add<string>(ThreatState, "{}");
            // Separate one-shot binding: the static coastline contour, kept out of the
            // per-frame ThreatState payload (re-sent every tick during a wave otherwise).
            Bindings.Add<string>(MapContour, "[]");
        }

        protected override void ConfigureTriggers()
        {
#if DEBUG
            Triggers.Add(DebugSkipPhase, FeatureIds.ThreatUI, OnDebugSkipPhase);
#endif
            // Camera/navigation triggers are UI-side commands, not gameplay requests.
            // Keep them synchronous so focus/tracking responds while simulation is paused.
            Triggers.Add<EntityRef>(FocusThreat, FeatureIds.ThreatUI, OnFocusThreat);
            Triggers.Add<EntityRef>(FocusRadarThreat, FeatureIds.ThreatUI, OnFocusRadarThreat);
            Triggers.Add<int, int>(FocusRadarDefense, FeatureIds.ThreatUI, OnFocusRadarDefense);
            Triggers.Add(DismissDebriefing, FeatureIds.ThreatUI, OnDismissDebriefing);
        }

        // Cached map bounds JSON
        private string m_CachedMapBoundsJson = "{\"MinX\":-7168,\"MaxX\":7168,\"MinZ\":-7168,\"MaxZ\":7168}";

        protected override void OnPanelUpdate()
        {
            PublishMapContourOnce();

            // NO_MIGRATE: unavailable DTO intentionally reflects actual WaveState absence.
            if (!m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var waveState))
            {
                var unavailableDto = new ThreatDto
                {
                    WavePhase = "calm",
                    ProducerReady = false,
                    WaveDataStatus = "unavailable",
                    EarlyWarningMessage = "",
                    IntelReportLabel = LocalizationManager.T("THREAT_INTEL_REPORT"),
                    NoActiveThreatsLabel = LocalizationManager.T("THREAT_NO_ACTIVE"),
                    ThreatTargetsJson = JsonBuilder.EmptyArray,
                    RadarThreatsJson = JsonBuilder.EmptyArray,
                    RadarTargetsJson = JsonBuilder.EmptyArray,
                    RadarDefensesJson = JsonBuilder.EmptyArray,
                    RadarBroadcastsJson = JsonBuilder.EmptyArray,
                    MapBoundsJson = m_CachedMapBoundsJson,
                    RadarInterceptionsJson = JsonBuilder.EmptyArray,
                    IdentifyTrackedEntity = -1,
                    CameraX = ThreatDto.CameraMarkerSentinel,
                    CameraZ = ThreatDto.CameraMarkerSentinel
                };
                PublishWhenComplete(ThreatState, NoSourceChecks, () => unavailableDto);
                return;
            }

            string phaseStr = waveState.CurrentPhase switch
            {
                GamePhase.Calm => "calm",
                GamePhase.Alert => "alert",
                GamePhase.Attack => "attack",
                // PsyAttack is its own top-indicator state ("INFO-WAR", purple) — the
                // info-war raid beat reads distinctly from the kinetic attack.
                GamePhase.PsyAttack => "psyattack",
                GamePhase.Recovery => "recovery",
                _ => "calm"
            };

            // Cross-feature soft read: ThreatUIDomain declares no hard dependency on
            // Waves/ThreatsAirDefense/ThreatDamage (Dependencies = { "Effects" }), and the
            // owner singleton can be transiently absent in the post-load window before its
            // producer's EnsureExists/PostLoadValidation runs. Degrade like the WaveState
            // path above (:153) — never the throwing GetSingletonOrDefault, which aborts
            // load with [CRITICAL] when the feature is available but the singleton is not
            // yet restored.
            var threatStats = m_ThreatStatsQuery.TryGetSingleton<ThreatStatsSingleton>(out var tStats)
                ? tStats : ThreatStatsSingleton.Default;
            int threatsRemaining = threatStats.TotalActiveCount;

            var interceptStats = m_InterceptStatsQuery.TryGetSingleton<InterceptStatsSingleton>(out var iStats)
                ? iStats : InterceptStatsSingleton.Default;
            int threatsIntercepted = interceptStats.InterceptedCount;
            var outcomeStats = m_ThreatOutcomeStatsQuery.TryGetSingleton<ThreatOutcomeStatsSingleton>(out var oStats)
                ? oStats : ThreatOutcomeStatsSingleton.Default;
            int threatsHit = outcomeStats.HitsCount;
            int threatsCrashed = outcomeStats.CrashedCount;

            // Detect save/load time wraparound — clear stale caches
            double elapsedTime = SystemAPI.Time.ElapsedTime;
            if (elapsedTime < m_LastSeenElapsedTime)
            {
                m_CachedThreatTargetsJson = JsonBuilder.EmptyArray;
                m_CachedRadarThreatsJson = JsonBuilder.EmptyArray;
                m_CachedRadarTargetsJson = JsonBuilder.EmptyArray;
                m_CachedRadarDefensesJson = JsonBuilder.EmptyArray;
                m_CachedRadarInterceptionsJson = JsonBuilder.EmptyArray;
                m_RadarObserverCursor = 0;
                m_TargetObserverCursor = 0;
                m_InterceptionMarkers.Clear();
                m_FramesSinceMapBoundsUpdate = MAP_BOUNDS_UPDATE_INTERVAL;
            }
            m_LastSeenElapsedTime = elapsedTime;

            UpdateThreatTargetsJson();
            UpdateRadarDataJson();
            UpdateInterceptionsJson();

            // M4 FIX: Detect time wraparound after save/load (ElapsedTime resets to ~0,
            // m_DebriefingShownTime retains old value → auto-dismiss never fires).
            // UI systems inherit UISystemBase which lacks OnGameLoaded.
            if (m_ShowDebriefing && m_DebriefingShownTime > SystemAPI.Time.ElapsedTime)
            {
                m_ShowDebriefing = false;
                m_DebriefingVisibleInCoordinator = false;
                ModalCoordinator.Instance.Dismiss("Debriefing");
                m_InterceptionMarkers.Clear();
                // M5 FIX: Zero stale debriefing counters on save/load wraparound
                m_LastDebriefingIntercepted = 0;
                m_LastDebriefingHits = 0;
                m_LastDebriefingShotsFired = 0;
                m_LastDebriefingCasualties = 0;
                m_LastDebriefingDamageCost = 0;
                m_LastDebriefingInfraDamageCost = 0;
                m_LastDebriefingCrashed = 0;
            }

            if (m_ShowDebriefing && m_DebriefingVisibleInCoordinator && ModalCoordinator.Instance.ActiveId != "Debriefing")
                m_DebriefingVisibleInCoordinator = false;

            if (m_ShowDebriefing && !m_DebriefingVisibleInCoordinator && ModalCoordinator.Instance.TryShow("Debriefing"))
            {
                m_DebriefingVisibleInCoordinator = true;
#pragma warning disable CIVIC034 // Transient UI timer — compared with ElapsedTime, not persisted
                m_DebriefingShownTime = SystemAPI.Time.ElapsedTime;
#pragma warning restore CIVIC034
            }

            // Debriefing — auto-dismiss after minimum display duration
            // S04-C1 FIX: removed GamePhase.Alert gate — debriefing shows during Recovery,
            // Alert only comes with next wave, so phase check prevented dismiss entirely
            if (m_ShowDebriefing
                && m_DebriefingVisibleInCoordinator
                && SystemAPI.Time.ElapsedTime - m_DebriefingShownTime >= DEBRIEFING_MIN_DISPLAY)
            {
                m_ShowDebriefing = false;
                m_DebriefingVisibleInCoordinator = false;
                ModalCoordinator.Instance.Dismiss("Debriefing");
            }

            // M1 FIX: Use snapshot crashed count (not live singleton which resets on new wave)
            int totalThreats = m_LastDebriefingIntercepted + m_LastDebriefingHits + m_LastDebriefingCrashed;
            float efficiency = m_LastDebriefingShotsFired > 0
                ? (float)m_LastDebriefingIntercepted / m_LastDebriefingShotsFired * 100f
                : 0f;

            // Camera "you are here" marker — read the live ground-target pivot.
            // Degrade to the sentinel when the camera host is unavailable so the UI
            // simply omits the marker (no crash, no false dot at world origin).
            float cameraX = ThreatDto.CameraMarkerSentinel;
            float cameraZ = ThreatDto.CameraMarkerSentinel;
            if (m_CameraTracking != null && m_CameraTracking.TryGetCameraGroundPosition(out var camX, out var camZ))
            {
                cameraX = camX;
                cameraZ = camZ;
            }

            // Identify pipeline state
#pragma warning disable CIVIC108 // Intentional: system is always enabled when ThreatUI runs
            int identifyTrackedEntity = m_IdentifySystem.TrackedEntityIndex;
            float identifyProgress = m_IdentifySystem.IdentifyProgress;
            bool identifyConfirmed = m_IdentifySystem.IsIdentified;
            bool identifyFocusActive = m_IdentifySystem.IsFocusActive;
#pragma warning restore CIVIC108

            var dto = new ThreatDto
            {
                WavePhase = phaseStr,
                WaveNumber = waveState.WaveNumber,
                ThreatsExpected = waveState.ThreatsExpected,
                ThreatsSpawned = waveState.ThreatsSpawned,
                ThreatsRemaining = threatsRemaining,
                ThreatsIntercepted = threatsIntercepted,
                ThreatsHit = threatsHit,
                ThreatsCrashed = threatsCrashed,
                TimeInPhase = waveState.TimeInPhase,
                PhaseEndTime = waveState.PhaseEndTime,
                ScenarioStarted = waveState.ScenarioStarted,
                ProducerReady = true,
                WaveDataStatus = GetWaveDataStatus(phaseStr, waveState, threatsRemaining, threatsIntercepted, threatsHit, threatsCrashed),
                WaitingForLaunchWindow = waveState.WaitingForLaunchWindow,
                EarlyWarningMessage = LocalizationManager.T("THREAT_EARLY_WARNING", waveState.ThreatsExpected),
                IntelReportLabel = LocalizationManager.T("THREAT_INTEL_REPORT"),
                NoActiveThreatsLabel = LocalizationManager.T("THREAT_NO_ACTIVE"),
                ThreatTargetsJson = m_CachedThreatTargetsJson,
                RadarThreatsJson = m_CachedRadarThreatsJson,
                RadarTargetsJson = m_CachedRadarTargetsJson,
                RadarDefensesJson = m_CachedRadarDefensesJson,
                RadarBroadcastsJson = m_CachedRadarBroadcastsJson,
                MapBoundsJson = m_CachedMapBoundsJson,
                IdentifyTrackedEntity = identifyTrackedEntity,
                IdentifyProgress = identifyProgress,
                IdentifyConfirmed = identifyConfirmed,
                IdentifyFocusActive = identifyFocusActive,
                ShowDebriefing = m_ShowDebriefing,
                DebriefingWave = m_LastDebriefingWave,
                DebriefingIntercepted = m_LastDebriefingIntercepted,
                DebriefingHits = m_LastDebriefingHits,
                DebriefingShotsFired = m_LastDebriefingShotsFired,
                DebriefingCasualties = m_LastDebriefingCasualties,
                DebriefingDamageCost = m_LastDebriefingDamageCost,
                DebriefingInfraDamageCost = m_LastDebriefingInfraDamageCost,
                DebriefingCrashed = m_LastDebriefingCrashed,
                DebriefingTotalThreats = totalThreats,
                DebriefingEfficiency = efficiency,
                RadarInterceptionsJson = m_CachedRadarInterceptionsJson,
                CameraX = cameraX,
                CameraZ = cameraZ
            };

            PublishWhenComplete(ThreatState, NoSourceChecks, () => dto);
        }

        /// <summary>
        /// Publishes the static coastline contour to the UI exactly once, as soon as the
        /// producer (VanillaMapContourAdapter) has computed it. The contour does not change
        /// during play, so after a successful publish this becomes a cheap bool check.
        /// </summary>
        private void PublishMapContourOnce()
        {
            if (m_MapContourPublished)
                return;

            if (m_MapContourReader.TryGetContourJson(out var contourJson))
            {
                Bindings.Update(MapContour, contourJson);
                m_MapContourPublished = true;
                // The contour is in world coords; the UI normalizes it with MapBounds.
                // Force a bounds refresh so the real (non-fallback) bounds reach the UI
                // right away and the outline aligns with markers without a stale window.
                m_FramesSinceMapBoundsUpdate = MAP_BOUNDS_UPDATE_INTERVAL;
                if (Log.IsDebugEnabled) Log.Debug($"Published map contour ({contourJson.Length} bytes)");
            }
        }

        /// <summary>
        /// Re-arm the one-shot contour publish on load. Load-over-running-game reuses this
        /// system instance and does NOT call OnStartRunning, so the producer recomputes the
        /// new city's coastline (VanillaMapContourAdapter.ValidateAfterLoad) but the consumer
        /// latch would stay set and keep the previous city's outline on the radar. PLVS calls
        /// this on every load; re-arming the latch republishes the fresh contour.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_MapContourPublished = false;
            m_FramesSinceMapBoundsUpdate = MAP_BOUNDS_UPDATE_INTERVAL;
        }

        private static string GetWaveDataStatus(
            string phase,
            WaveStateSingleton waveState,
            int remaining,
            int intercepted,
            int hits,
            int crashed)
        {
            int observed = waveState.ThreatsSpawned + remaining + intercepted + hits + crashed;
            if (!waveState.ScenarioStarted && observed == 0)
                return "noWave";

            if (observed == 0)
                return "preStart";

            return phase == "calm" || phase == "recovery" ? "completed" : "active";
        }

        private void UpdateThreatTargetsJson()
        {
            var targetView = m_TargetReader.TargetsView;
            if (targetView == null)
            {
                if (m_TargetObserverCursor != 0)
                {
                    m_TargetObserverCursor = 0;
                    m_CachedThreatTargetsJson = JsonBuilder.EmptyArray;
                }

                return;
            }

            var observed = targetView.Observe(ref m_TargetObserverCursor);
            if (!observed.Changed)
                return;

            var targets = observed.Value.Targets;
            if (targets == null || targets.Count == 0)
            {
                m_CachedThreatTargetsJson = JsonBuilder.EmptyArray;
            }
            else
            {
                var sb = new StringBuilder(1024);
                sb.Append('[');
                bool first = true;
                foreach (var t in targets)
                {
                    var entry = WireThreatTarget.FromRuntime(t);
                    if (!first) sb.Append(',');
                    first = false;
                    entry.WriteTo(sb);
                }
                sb.Append(']');
                m_CachedThreatTargetsJson = sb.ToString();
            }
        }

        private void UpdateRadarDataJson()
        {
            var radarView = m_RadarReader.RadarView;
            if (radarView == null)
            {
                if (m_RadarObserverCursor != 0)
                {
                    m_RadarObserverCursor = 0;
                    m_CachedRadarThreatsJson = JsonBuilder.EmptyArray;
                    m_CachedRadarTargetsJson = JsonBuilder.EmptyArray;
                    m_CachedRadarDefensesJson = JsonBuilder.EmptyArray;
                }
            }
            else
            {
                var observed = radarView.Observe(ref m_RadarObserverCursor);
                if (observed.Changed)
                {
                    var threats = observed.Value.Threats;

                    int threatCount = threats != null ? threats.Count : 0;
                    m_CachedRadarThreatsJson = SerializeRadarThreats(threats);

                    int jsonLen = m_CachedRadarThreatsJson.Length;
                    if (threatCount > 0)
                    {
                        if (Log.IsDebugEnabled) Log.Debug($"UpdateRadarData: threats={threatCount}, jsonLen={jsonLen}");
                    }

                    m_CachedRadarTargetsJson = SerializeRadarTargets(observed.Value.Targets);
                    m_CachedRadarDefensesJson = SerializeRadarDefenses(observed.Value.Defenses);
                }
            }

            // Telecom broadcast umbrellas — independent of the threat/target/defense radarView
            // (a separate vanilla telecom reader), so serialize every panel tick from its cache.
            m_CachedRadarBroadcastsJson = m_BroadcastReader != null
                ? SerializeRadarBroadcasts(m_BroadcastReader.GetCoverage())
                : JsonBuilder.EmptyArray;

            m_FramesSinceMapBoundsUpdate++;
            if (m_FramesSinceMapBoundsUpdate >= MAP_BOUNDS_UPDATE_INTERVAL)
            {
                var (min, max) = m_RadarReader.GetMapBounds();
                var bounds = new WireMapBounds
                {
                    MinX = min.x,
                    MaxX = max.x,
                    MinZ = min.z,
                    MaxZ = max.z,
                };
                var sb = new StringBuilder(64);
                bounds.WriteTo(sb);
                m_CachedMapBoundsJson = sb.ToString();
                m_FramesSinceMapBoundsUpdate = 0;
            }
        }

        private static string SerializeRadarThreats(IReadOnlyList<RuntimeRadarThreat>? items)
        {
            if (items == null || items.Count == 0) return JsonBuilder.EmptyArray;

            var sb = new StringBuilder(1024);
            sb.Append('[');
            bool first = true;
            foreach (var t in items)
            {
                if (t.Type == null)
                {
                    Mod.Log.Warn($"ThreatUISystem: SKIP null type: entity={t.Entity.Index}v{t.Entity.Version}");
                    continue;
                }
                if (t.EvasionStatus == null)
                {
                    Mod.Log.Warn($"ThreatUISystem: SKIP null evasionStatus: entity={t.Entity.Index}v{t.Entity.Version}");
                    continue;
                }
                if (float.IsNaN(t.X) || float.IsInfinity(t.X) ||
                    float.IsNaN(t.Z) || float.IsInfinity(t.Z) ||
                    float.IsNaN(t.Vx) || float.IsInfinity(t.Vx) ||
                    float.IsNaN(t.Vz) || float.IsInfinity(t.Vz) ||
                    float.IsNaN(t.Eta) || float.IsInfinity(t.Eta))
                {
                    Mod.Log.Warn($"ThreatUISystem: SKIP NaN/Inf: entity={t.Entity.Index}v{t.Entity.Version} x={t.X} z={t.Z} vx={t.Vx} vz={t.Vz} eta={t.Eta}");
                    continue;
                }

                var entry = WireRadarThreat.FromRuntime(t);
                if (!first) sb.Append(',');
                first = false;
                entry.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string SerializeRadarTargets(IReadOnlyList<RuntimeRadarTarget> items)
        {
            if (items == null || items.Count == 0) return JsonBuilder.EmptyArray;

            var sb = new StringBuilder(512);
            sb.Append('[');
            bool first = true;
            foreach (var t in items)
            {
                var entry = WireRadarTarget.FromRuntime(t);
                if (!first) sb.Append(',');
                first = false;
                entry.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string SerializeRadarBroadcasts(IReadOnlyList<BroadcastCoverage> items)
        {
            if (items == null || items.Count == 0) return JsonBuilder.EmptyArray;

            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            foreach (var c in items)
            {
                if (float.IsNaN(c.X) || float.IsInfinity(c.X) ||
                    float.IsNaN(c.Z) || float.IsInfinity(c.Z) ||
                    float.IsNaN(c.Range) || float.IsInfinity(c.Range) ||
                    c.Range <= 0f)
                {
                    continue;
                }

                var entry = WireRadarBroadcast.FromRuntime(
                    new RuntimeRadarBroadcast { X = c.X, Z = c.Z, Range = c.Range });
                if (!first) sb.Append(',');
                first = false;
                entry.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string SerializeRadarDefenses(IReadOnlyList<RuntimeRadarDefense> items)
        {
            if (items == null || items.Count == 0) return JsonBuilder.EmptyArray;

            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            foreach (var d in items)
            {
                if (float.IsNaN(d.X) || float.IsInfinity(d.X) ||
                    float.IsNaN(d.Z) || float.IsInfinity(d.Z) ||
                    float.IsNaN(d.Range) || float.IsInfinity(d.Range) ||
                    d.Range <= 0f)
                {
                    continue;
                }

                var entry = WireRadarDefense.FromRuntime(d);
                if (!first) sb.Append(',');
                first = false;
                entry.WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void UpdateInterceptionsJson()
        {
            double now = SystemAPI.Time.ElapsedTime;

            // Expire old markers (also remove stale markers from before save/load where ElapsedTime reset)
            m_InterceptionMarkers.RemoveAll(m => (now - m.Timestamp) > INTERCEPTION_LIFETIME || now < m.Timestamp);

            if (m_InterceptionMarkers.Count == 0)
            {
                m_CachedRadarInterceptionsJson = JsonBuilder.EmptyArray;
                return;
            }

            var sb = new StringBuilder(512);
            sb.Append('[');
            bool first = true;
            foreach (var m in m_InterceptionMarkers)
            {
                var entry = new WireRadarInterception
                {
                    X = m.X,
                    Z = m.Z,
                    TimeAgo = (float)(now - m.Timestamp),
                    Lifetime = INTERCEPTION_LIFETIME,
                    Success = m.Success,
                };
                if (!first) sb.Append(',');
                first = false;
                entry.WriteTo(sb);
            }
            sb.Append(']');
            m_CachedRadarInterceptionsJson = sb.ToString();
        }

        private void OnWaveEnded(WaveEndedEvent evt)
        {
            m_LastDebriefingWave = evt.WaveNumber;
            m_LastDebriefingIntercepted = evt.Intercepted;
            m_LastDebriefingHits = evt.Hits;
            m_LastDebriefingShotsFired = evt.ShotsFired;
            m_LastDebriefingCasualties = evt.Casualties;
            m_LastDebriefingDamageCost = evt.DamageCost;
            m_LastDebriefingInfraDamageCost = evt.InfrastructureDamageCost;
            m_LastDebriefingCrashed = evt.Crashed;
            // L17 FIX: Clear stale interception markers from previous wave
            m_InterceptionMarkers.Clear();
            m_ShowDebriefing = true;
            m_DebriefingVisibleInCoordinator = false;
        }

        private void OnDismissDebriefing()
        {
            m_ShowDebriefing = false;
            m_DebriefingVisibleInCoordinator = false;
            ModalCoordinator.Instance.Dismiss("Debriefing");
        }

        private void OnThreatIntercept(ThreatInterceptEvent evt)
        {
            // Ring buffer: drop oldest if full
            if (m_InterceptionMarkers.Count >= MAX_INTERCEPTIONS)
                m_InterceptionMarkers.RemoveAt(0);

#pragma warning disable CIVIC230 // Pre-allocated List, Add is O(1) amortized
            m_InterceptionMarkers.Add(new InterceptionMarker
            {
                X = evt.Position.x,
                Z = evt.Position.z,
                Timestamp = SystemAPI.Time.ElapsedTime,
                Success = true
            });
#pragma warning restore CIVIC230
        }

        // FIX S7-03: Auto-dismiss debriefing when game over fires (prevents overlap with DefeatModal)
        private void OnGameOver(GameOverEvent _)
        {
            m_ShowDebriefing = false;
            m_DebriefingVisibleInCoordinator = false;
            ModalCoordinator.Instance.Dismiss("Debriefing");
        }

#if DEBUG
        private void OnDebugSkipPhase()
        {
            EventBus?.SafePublish(new DebugSkipPhaseCommand(), "ThreatUISystem");
        }
#endif

        /// <summary>
        /// One-shot camera pan from a UI click. Do not convert this to an ECS
        /// request: camera navigation must react immediately, including while
        /// GameSimulation is paused.
        /// </summary>
        private void OnFocusThreat(EntityRef targetEntity)
        {
            if (m_CameraService == null) return;
            var targets = GetCurrentThreatTargets();

            foreach (var target in targets)
            {
                if (target.EntityIndex == targetEntity.Index && target.EntityVersion == targetEntity.Version)
                {
                    m_CameraService.FocusOnPosition(target.Position);
                    Log.Info($"Focused on target: {target.Name}");
                    return;
                }
            }
        }

        private IReadOnlyList<RuntimeThreatTarget> GetCurrentThreatTargets()
        {
            var targetView = m_TargetReader.TargetsView;
            if (targetView == null)
                return Array.Empty<RuntimeThreatTarget>();

            int observerCursor = m_TargetObserverCursor;
            return targetView.Observe(ref observerCursor).Value.Targets
                ?? Array.Empty<RuntimeThreatTarget>();
        }

        /// <summary>
        /// Starts camera tracking for a radar threat from a UI click. This is a
        /// pause-safe UI navigation command, not a gameplay request to be drained
        /// by a later simulation update.
        /// </summary>
        /// <summary>
        /// One-shot camera pan to a static air-defense installation clicked on the
        /// radar. Installations don't move, so this is a plain <see cref="CameraFocusState.FocusOnPosition(float3)"/>
        /// pan, not follow-cam — pause-safe UI navigation, not an ECS request.
        ///
        /// Payload is the installation's world X/Z in meters (the UI hit-tests the
        /// marker and sends its position). We pass coordinates rather than a list
        /// index on purpose: the radar defense list is rebuilt every tick from an
        /// ECS query with no stable-order contract, so an index could resolve to a
        /// different battery between the click and its dispatch. Metre precision is
        /// ample for centering the camera. If AA ever becomes mobile, switch this
        /// to CameraTracking by entity like the threat path.
        /// </summary>
        private void OnFocusRadarDefense(int worldX, int worldZ)
        {
            if (m_CameraService == null) return;
            m_CameraService.FocusOnPosition(new float3(worldX, 100f, worldZ));
        }

        private void OnFocusRadarThreat(EntityRef entity)
        {
            // Engage identify pipeline: follow-cam (not one-time pan)
            if (m_CameraTracking != null)
            {
                m_CameraTracking.SetTrackedEntity(entity);
                if (Log.IsDebugEnabled) Log.Debug($"Camera tracking radar threat entity={entity.Index}v{entity.Version}");
            }
            else
            {
                // Fallback: one-time pan if camera tracking unavailable
                if (m_CameraService == null) return;
                var threats = m_RadarReader.GetRadarThreats();
                foreach (var threat in threats)
                {
                    if (threat.Entity.Index == entity.Index && threat.Entity.Version == entity.Version)
                    {
                        var position = new float3(threat.X, 100f, threat.Z);
                        m_CameraService.FocusOnPosition(position);
                        if (Log.IsDebugEnabled) Log.Debug($"Fallback: focused on radar threat at ({threat.X:F0}, {threat.Z:F0})");
                        return;
                    }
                }
            }
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            UnsubscribeSafe<GameOverEvent>(OnGameOver);
            UnsubscribeSafe<ThreatInterceptEvent>(OnThreatIntercept);
            base.OnDestroy();
        }
    }
#pragma warning restore CIVIC098
}
