using System;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Components.Domain.GridWarfare;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Constants;
using CivicSurvival.Domains.Waves.Logic;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Bootstrap;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Waves.Systems
{
    /// <summary>
    /// Spawns threat entities at map edges.
    /// Called by WaveExecutor when Attack phase begins (via SpawnWaveRequestEvent).
    ///
    /// Targets are selected by category distribution:
    /// - Energy (60%): PowerPlants, Transformers
    /// - Critical (15%): Hospitals, WaterPumps
    /// - Service (15%): FireStations, PoliceStations
    /// - Civilian (10%): Residential (terror attacks)
    /// </summary>
    [ActIndependent]
    public partial class ThreatSpawnSystem : CivicSystemBase, IPostLoadValidation, IOutboundStrikeService
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // Outbound counter-strike frontier (developer fork #2): an outbound projectile has no
        // building target on the player's map — it flies toward a notional enemy beachhead past
        // the map edge. The launch point is the map centre; the target is this many metres beyond
        // the nearest edge, in the launch→edge direction, so the projectile visibly leaves the map
        // before it terminalizes (ThreatArrivalSystem's outbound branch). NOT a balance number —
        // it only governs how far past the edge the 3D projectile travels before the 2D target
        // window takes over; lives here as a named constant rather than a magic literal.
        private const float OUTBOUND_FRONTIER_MARGIN = 800f;
        private const float OUTBOUND_LAUNCH_JITTER = 200f;
        // ECB command counter — diagnostic only (PERF log). Read from render thread via ReportEcbCounts.
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => System.Threading.Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => System.Threading.Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => System.Threading.Interlocked.Increment(ref s_EcbCommandCount);

        private const float GROUND_POINT_MAX_MARGIN = 1000f;
        private const float GROUND_POINT_MARGIN_RATIO = 0.25f;

        private static readonly LogContext Log = new("ThreatSpawnSystem");

        // Building target queries live on ThreatTargetCacheSystem;
        // ThreatSpawnSystem reads cached lists via IThreatTargetSource
        // so no Transform-touching query is registered against this system's
        // Dependency chain. Per-tick CompleteDependencyBeforeRO<Transform>
        // wait that used to dominate this system is gone.
        private IThreatTargetSource? m_TargetSource;
        private EntityQuery m_PowerGridQuery;
        // Live drone-launcher emplacements — the outbound-drone launch origins. Read on the main
        // thread in Launch (ModificationEnd) to place a drone counter-strike at a built launcher.
        private EntityQuery m_DroneLauncherQuery;

        private SerializableRandom m_Random;

        // Map bounds (cached, periodically refreshed)
        private float3 m_MapMin;
        private float3 m_MapMax;
        private bool m_BoundsCached;
        private int m_WavesSinceRecache;

        // Delegated components
        private ThreatTargetSelector m_TargetSelector = null!;
        private CivicPrefabInitSystem m_PrefabInit = null!;

        // One-shot per session: the genuinely-missing AttackDrone report is demand-proven
        // (logged at a real spawn, not at the post-load scan that races async mod load).
        // Reset in ResetTransientRuntimeStateAfterLoad on every load.
        [System.NonSerialized] private bool m_ReportedMissingDronePrefab;

        // Terrain system for altitude calculations
        private TerrainSystem m_TerrainSystem = null!;

        // Spawn is now off-barrier: SpawnWave does all target/CEP/position/RNG work in
        // GameSimulation and records the result into the ThreatSpawnIntent buffer; the actual
        // CreateEntity happens in ThreatSpawnApplySystem (Modification4), after a render-completion
        // gate, so the drone-chunk migration cannot land in a chunk DroneRenderWriteJob is still
        // iterating (null chunk pointer → native AV). The host singleton is owned by the consumer.
        private EntityQuery m_IntentHostQuery;

        // Per-SpawnWave-call monotonic batch sequence — stamped on every intent of one call so the
        // consumer groups them and publishes exactly one ThreatsSpawnedEvent per wave. Starts at 1
        // so 0 is reserved for "unset" (the consumer skips a 0 key for idempotency). Process-lifetime
        // counter — NOT save state (the intent buffer is stripped on load). Named "batch", NOT
        // "generation": it is unrelated to the world-generation stamp from m_threatGenerationClock.
        [System.NonSerialized] private uint m_SpawnBatchSeq;

        // PERF: Aggregate altitude stats (avoid per-drone logging) — diagnostic only, not persisted
        [System.NonSerialized] private float m_MinSpawnAltitude;
        [System.NonSerialized] private float m_MaxSpawnAltitude;
        // FIX H27: Cached per SpawnWave call — SpawnShahed/SpawnBallistic use same snapshot
#pragma warning disable CIVIC314 // Ephemeral per-SpawnWave cache, refreshed on every call — not save/load state
        [System.NonSerialized] private RemoteBalanceConfig m_CachedConfig = null!;
#pragma warning restore CIVIC314

        // C-5 threat-generation root fix: stamp every spawned threat with the
        // current loaded-world generation so impact consumers can drop stale
        // pre-load threats without killing legal in-flight act transitions.
        // Process-lifetime service handle (re-resolved in OnStartRunning) — not save state.
        [System.NonSerialized] private ThreatGenerationClock m_threatGenerationClock = null!;

        // Attached nozzle exhaust (see EnsureBallisticExhaustAttached): every live
        // ballistic carries an owner-attached FireMovingMediumVFX record injected via
        // VanillaVfxSystem.TryAttachEffect; EffectTransformSystem then moves the flame
        // with the missile's InterpolatedTransform every frame (DynamicTransform).
        //
        // Re-evaluations must stay rare: every EffectControlSystem pass over the
        // entity (Updated / EffectsUpdated / BatchesUpdated / culling transitions)
        // sees the prefab condition (required=OnFire) as false and Disables the
        // injected record — the controller restores it within ≤6 frames, but each
        // round trip is a visible flicker. So nothing in our code may tag live
        // ballistics with EffectsUpdated/Updated for VFX purposes.
        private EntityQuery m_LiveBallisticQuery;

        private Core.Systems.Effects.EffectCacheSystem m_EffectCache = null!;

        // Attach-controller cadence: covers initial attach after spawn (entities exist
        // one frame after the wave's ECB playback), restore after rare re-evaluations,
        // and restore after load (vanilla PostDeserialize clears m_EnabledData while
        // the IEmptySerializable EnabledEffect buffer survives present-but-empty).
        // RENDER-frame cadence (UnityEngine.Time.frameCount), not sim ticks. Vanilla
        // EffectControlSystem re-evaluates once per render frame; gating on sim ticks scaled the
        // city-wide EnabledData drain with game speed (Sim 84-96/s under a wave). Render gate
        // decouples the drain from game speed (twin of InterceptorExhaustSystem.REATTACH_RENDER_INTERVAL).
        private const int EXHAUST_ATTACH_RENDER_INTERVAL = 3;
#pragma warning disable CIVIC150 // Transient log-arm flag: re-armed by the next wave spawn / load.
        [System.NonSerialized] private bool m_ExhaustAttachLogArmed;
#pragma warning restore CIVIC150
        [System.NonSerialized] private Core.Systems.Effects.VanillaVfxSystem? m_VanillaVfx;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Session-unique seed (GameTimeSystem.Instance is null in OnCreate вЂ” use TickCount)
            int seed = Environment.TickCount + 0x2001;
            m_Random = new SerializableRandom(seed);
            Log.Info($" Created with seed {seed}");

            m_PowerGridQuery = GetEntityQuery(ComponentType.ReadOnly<PowerGridSingleton>());
            m_DroneLauncherQuery = GetEntityQuery(
                ComponentType.ReadOnly<DroneLauncherInstallation>(),
                ComponentType.Exclude<Deleted>());
            m_LiveBallisticQuery = GetEntityQuery(
                ComponentType.ReadOnly<Ballistic>(),
                // Read for the attach seed position (ToComponentDataArray requires query
                // membership). ThreatPosition (not vanilla Game.Objects.Transform) is our
                // mod-only SSOT: reading it completes only our handful of ballistic-movement
                // writers, not the city-wide vanilla Transform job chain (~tens of ms drain
                // on the main thread). Every ballistic carries it via the ballistic render
                // archetype (now built by ThreatSpawnApplySystem, the spawn consumer).
                ComponentType.ReadOnly<ThreatPosition>(),
                ComponentType.Exclude<Deleted>());

            // Restored threats persist across save/load (C1); their render-state and lifecycle
            // tags are reinitialized by ThreatLoadRenderReinitSystem (ModificationEnd, pause-safe,
            // before the first PreCulling), not here.

            // Initialize delegated components. CivicPrefabInitSystem is registered by
            // SystemRegistrar.RegisterCoreSystems before any domain system OnCreate runs;
            // GetExistingSystemManaged keeps the analyzer from flagging this as bypassing
            // a feature gate (CIVIC400).
            m_PrefabInit = World.GetExistingSystemManaged<CivicPrefabInitSystem>();

            m_TargetSelector = new ThreatTargetSelector(m_Random, m_TargetSource);

            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_EffectCache = World.GetOrCreateSystemManaged<Core.Systems.Effects.EffectCacheSystem>();

            // Off-barrier spawn: append intents into the consumer-owned ThreatSpawnIntent host.
            // The archetypes themselves live on ThreatSpawnApplySystem (it owns the CreateEntity).
            m_IntentHostQuery = GetEntityQuery(ComponentType.ReadWrite<ThreatSpawnIntent>());

            // Subscribe to spawn wave requests
            SubscribeRequired<SpawnWaveRequestEvent>(OnSpawnWaveRequest);

            // Outbound counter-strike launch: the GridWarfare effect owner crosses here (Axiom 5)
            // to fire a player projectile, reusing this producer's prefab/bounds/RNG/intent path.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IOutboundStrikeService>(this);

            // FIX EC-SL-008: Apply saved state from Deserialize() if loading a save
            // This must be called AFTER m_Random initialization to override it
            ApplySavedState();

            Log.Info(" Created");
        }

        private void OnSpawnWaveRequest(SpawnWaveRequestEvent evt)
        {
            using (Core.Utils.PerformanceProfiler.Measure("ThreatSpawn.SpawnWave"))
            {
                Log.Info($" Received SpawnWaveRequest: {evt.ThreatCount} threats, wave {evt.WaveNumber}, type {evt.WaveType}, role {evt.WaveRole}, ballisticOverride {evt.BallisticOverride}");
                SpawnWave(evt.ThreatCount, evt.WaveNumber, evt.WaveType, evt.BallisticOverride, evt.WaveRole);
            }
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_TargetSource = ServiceRegistry.Instance.Require<IThreatTargetSource>();
            m_TargetSelector.Source = m_TargetSource;
            // C-5: resolve the threat-generation clock once (process-lifetime; ??=
            // re-resolves on a fresh-world load). Never resolved in OnUpdate.
            m_threatGenerationClock ??= ServiceRegistry.Instance.Require<ThreatGenerationClock>();
            // Same resolve pattern as ThreatTerminalizationSystem: VanillaVfxSystem is
            // registered by Core before domain systems start running.
            m_VanillaVfx ??= World.GetExistingSystemManaged<Core.Systems.Effects.VanillaVfxSystem>();
        }

        public void ValidateAfterLoad()
        {
            ResetTransientRuntimeStateAfterLoad();
            ReResolveRuntimeRefs();

            // Restored ballistics get their exhaust back from the attach controller:
            // vanilla PostDeserialize cleared m_EnabledData, the controller re-attaches
            // within ≤6 sim frames after unpause (GameSimulation does not tick paused).
            // Arm the one-shot log so the re-attach leaves a marker.
            m_ExhaustAttachLogArmed = true;
        }

        private void ResetTransientRuntimeStateAfterLoad()
        {
            // Bounds are process-local terrain cache — recalculated from TerrainSystem
            // on the first post-load wave instead of replaying a stale saved snapshot.
            m_MapMin = default;
            m_MapMax = default;
            m_BoundsCached = false;
            m_WavesSinceRecache = 0;
            ClearTransientState();
        }

        /// <summary>
        /// Single source for the [NonSerialized] transients (batch sequence, altitude
        /// diagnostics, per-wave config cache, one-shot log latches), called from every
        /// reset leg — ResetState, ResetToBootDefaults and the post-load path — so the
        /// legs cannot drift and no previous-session latch survives into the next one.
        /// </summary>
        private void ClearTransientState()
        {
            // Wrap-guard re-bases the sequence to 1 on the next increment; the intent
            // buffer is stripped on load, so 0 ("unset") is always safe here.
            m_SpawnBatchSeq = 0;
            m_MinSpawnAltitude = 0f;
            m_MaxSpawnAltitude = 0f;
            m_CachedConfig = null!;
            m_ExhaustAttachLogArmed = false;
            m_ReportedMissingDronePrefab = false;
        }

        private void ReResolveRuntimeRefs()
        {
            if (!ServiceRegistry.IsInitialized)
                return;

            m_TargetSource = ServiceRegistry.Instance.Require<IThreatTargetSource>();
            m_TargetSelector.Source = m_TargetSource;
            m_threatGenerationClock = ServiceRegistry.Instance.Require<ThreatGenerationClock>();
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                UnsubscribeSafe<SpawnWaveRequestEvent>(OnSpawnWaveRequest);
                ServiceRegistry.Instance.Unregister<IOutboundStrikeService>(this);
            }
            base.OnDestroy();
        }

        protected override void OnUpdateImpl()
        {
            // Re-attach exhaust to ballistics already in flight before any spawn work.
            EnsureBallisticExhaustAttached();

            // Cache map bounds on first update
            if (!m_BoundsCached)
            {
                CacheMapBounds();
            }
        }

        // Attached nozzle exhaust controller. Every EXHAUST_ATTACH_RENDER_INTERVAL
        // render frames, make sure every live ballistic carries a live owner-attached
        // FireMovingMediumVFX record (VanillaVfxSystem.TryAttachEffect validates the existing
        // record and re-injects when it is missing/stale). The engine does the rest:
        // EffectTransformSystem follows the owner's InterpolatedTransform every frame,
        // reading the nozzle offset from the Rocket prefab's Effect element.
        // PERF-LOCK: render-frame early-return — the query/EnabledData drain below must run on a
        // RENDER cadence, never on sim ticks; a sim-tick gate re-multiplies the drain by game speed.
#pragma warning disable CIVIC218 // Throttled to 1/N render frames, cosmetic controller, ≤dozens of ballistic entities — same class as the diagnostic reads above
        private void EnsureBallisticExhaustAttached()
        {
            if (UnityEngine.Time.frameCount % EXHAUST_ATTACH_RENDER_INTERVAL != 0)
                return;

            // VFX system not initialized yet (EffectCacheSystem still warming up after
            // load) — silently skip this pass; the exhaust attaches on a later tick.
            if (m_VanillaVfx == null || !m_VanillaVfx.IsReady)
                return;
            if (m_LiveBallisticQuery.IsEmptyIgnoreFilter)
                return;
            if (!m_EffectCache.TryGetEffect(EffectNames.FIRE_MOVING_MEDIUM_VFX, out Entity effectPrefab))
                return;

            var entities = m_LiveBallisticQuery.ToEntityArray(Allocator.Temp);
            // Seed position only — EffectTransformSystem overwrites it from the owner's
            // InterpolatedTransform (DynamicTransform record) before anything renders it
            // for long; the seed just avoids one frame at the world origin. Read from our
            // mod-only ThreatPosition, not vanilla Transform, to avoid draining the whole
            // city's Transform job chain on the main thread (see query setup in OnCreate).
            var positions = m_LiveBallisticQuery.ToComponentDataArray<ThreatPosition>(Allocator.Temp);

            // Element 0 of the Rocket prefab's Effect buffer (bound by
            // CivicPrefabInitSystem.TryBindRocketExhaust). AttachEffectBatch (called later by the
            // late-phase drain) skips ballistics that fell back to the drone model — no Effect
            // element. Enqueue for the deferred CompleteRendering drain (VanillaVfxLateAttachSystem)
            // instead of draining here in GameSimulation: the EnabledData deps.Complete() would
            // otherwise wait on this frame's in-flight city effect graph (~tens of ms under a wave).
            // Ballistic twin of the interceptor exhaust late-phase fix.
            var requests = new NativeList<Core.Systems.Effects.VfxAttachRequest>(entities.Length, Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                requests.Add(new Core.Systems.Effects.VfxAttachRequest(entities[i], effectPrefab, 0, positions[i].Position));

            m_VanillaVfx.EnqueueOwnerAttach(requests);

            int enqueued = entities.Length;
            requests.Dispose();
            entities.Dispose();
            positions.Dispose();

            // The actual attach now happens in CompleteRendering; mark the one-shot log on the
            // enqueue (a non-empty request set = a re-attach pass) since the per-batch attached
            // count is no longer returned synchronously.
            if (m_ExhaustAttachLogArmed && enqueued > 0)
            {
                m_ExhaustAttachLogArmed = false;
                Log.Info($"[BallisticVFX] Exhaust re-attach enqueued → {enqueued} ballistic(s)");
            }
        }
#pragma warning restore CIVIC218

        private void CacheMapBounds()
        {
            // Use TerrainSystem playable area - real map bounds without backdrop
            var offset = m_TerrainSystem.playableOffset;
            var area = m_TerrainSystem.playableArea;

            m_MapMin = new float3(offset.x, 0, offset.y);
            m_MapMax = new float3(offset.x + area.x, 0, offset.y + area.y);

            m_BoundsCached = true;
            Log.Info($" Map bounds from TerrainSystem: {m_MapMin} to {m_MapMax} (area: {area.x:F0}x{area.y:F0}m)");
        }

        /// <summary>
        /// Spawn a wave of threats.
        /// Called via SpawnWaveRequestEvent at start of Attack phase.
        /// </summary>
#pragma warning disable CIVIC231 // Called via SpawnWaveRequestEvent — WaveExecutor validates act
        public void SpawnWave(int threatCount, int waveNumber, WaveType waveType = WaveType.Harassment, int ballisticOverride = -1, WaveRole waveRole = WaveRole.Regular)
        {
#pragma warning restore CIVIC231
            ApplySavedState();

            // PERF: Cache config locally (avoid 4-level indirection per access)
            // FIX H27: Single read of BalanceConfig.Current per SpawnWave call —
            // prevents torn config if background swap occurs between method calls.
            // Cached as field so SpawnShahed/SpawnBallistic use same snapshot.
            m_CachedConfig = BalanceConfig.Current;
            var wavesCfg = m_CachedConfig.Waves;
            int citySizeMW = GetCitySizeMW();
            int ballisticCount = ballisticOverride >= 0
                ? ballisticOverride
                : ThreatMath.CalculateBallisticCount(citySizeMW, waveNumber, wavesCfg);

            // PERF-LOCK: cap simultaneous in-flight ballistics. The vanilla render pipeline
            // reprocesses TransformFrame/Moving for every live ballistic each frame, so render
            // cost is linear in this count; overlapping waves (DoubleTapChance) would otherwise
            // stack live ballistics well past BallisticMaxPerWave. Difficulty scales via leak
            // chance / speed / frequency, NOT simultaneous count — do not raise this silently to
            // make waves harder (Axiom 15). CalculateEntityCount reads archetype counts only
            // (no component read), so it adds no sync point.
            int liveBallistics = m_LiveBallisticQuery.CalculateEntityCount();
            int ballisticHeadroom = math.max(0, wavesCfg.BallisticMaxConcurrentInFlight - liveBallistics);
            ballisticCount = math.min(ballisticCount, ballisticHeadroom);

            // Ballistics are a SUBSET of the wave's total threat count, not an addition on top of it.
            // threatCount is what the scaling formula and the UI ("expecting N threats") promise, so
            // the drones spawned must be threatCount minus the ballistics, keeping spawned == promised.
            // Floored at 0: if ballisticCount ever exceeds threatCount (large production, small clamped
            // wave), the ballistics stand as the wave's minimum and no negative drone loop runs.
            int droneCount = math.max(0, threatCount - ballisticCount);

            string waveTypeName = waveType == WaveType.MassiveStrike ? "MASSIVE STRIKE" : "Harassment";
            Log.Info($" Spawning wave #{waveNumber} ({waveTypeName}): {threatCount} threats ({droneCount} drones + {ballisticCount} ballistic)");

            // Reading AttackDroneEntity re-resolves on demand: the getter re-fetches the entity
            // from the stable PrefabBase ref whenever its cache was cleared (every load) or the
            // entity went stale (asset-editor hot-reload), so a prefab present in m_Prefabs is
            // never reported missing just because a reload zeroed the cache mid-session. If it is
            // STILL Null at an actual spawn, the prefab is not resolved AT THIS WAVE — skip the
            // wave and do NOT publish a ThreatsSpawnedEvent(0,0,…) (WaveExecutor would treat that as
            // a normal zero spawn). This is NOT necessarily permanent: CivicPrefabInitSystem now
            // re-resolves on vanilla onContentAvailabilityChanged, so a .cok that vanilla rejected
            // via the IsAvailable gate or removed mid-session can reappear and a later wave spawns
            // normally. Report once per session to avoid spam, not because the absence is final.
            Entity droneEntity = m_PrefabInit.AttackDroneEntity;
            if (droneEntity == Entity.Null)
            {
                if (!m_ReportedMissingDronePrefab)
                {
                    m_ReportedMissingDronePrefab = true;
                    // Error (reaches telemetry): the intro siren gate holds the first strike until
                    // CivicPrefabInitSystem reports the prefabs settled, so reaching this null past
                    // the gate means the AttackDrone .cok is unresolved at gameplay time — either a
                    // load/registration failure (vanilla dropped it before AddPrefab) or a content-
                    // availability gate that has not flipped back yet. Worth surfacing in Grafana;
                    // the init system keeps re-resolving on availability events, so it may recover.
                    Log.Error("AttackDrone prefab unresolved at wave spawn (expected Assets/Models/AttackDrone.cok in PrefabSystem.m_Prefabs) — threat spawning held until it resolves; see CivicPrefabInitSystem for the load/availability verdict.");
                }
                return;
            }

            // Ballistics render with the dedicated Rocket model; if the asset is missing,
            // fall back to the drone model so the wave still functions (non-fatal, no
            // separate report — RocketEntity also re-resolves lazily on read).
            Entity rocketEntity = m_PrefabInit.RocketEntity;
            if (rocketEntity == Entity.Null)
                rocketEntity = droneEntity;

            // Periodic map bounds recache
            m_WavesSinceRecache++;
            if (m_WavesSinceRecache >= wavesCfg.MapBoundsRecacheWaves)
            {
                m_BoundsCached = false;
                m_WavesSinceRecache = 0;
                Log.Info($" Forcing map bounds recache");
            }

            // FIX: Ensure bounds are cached before spawning (may be called before OnUpdate)
            if (!m_BoundsCached)
            {
                CacheMapBounds();
            }

            // Calculate variance for this wave
            float variance = m_Random.NextFloat(-wavesCfg.TargetingVariance, wavesCfg.TargetingVariance);

            // Concentration: the first FocusFraction of the wave's drones pile onto a
            // handful of buildings (filling each to MaxThreatsPerTarget) so something is
            // actually demolished; the remainder spread into isolated fires. Clamped so a
            // bad config value can't push every drone into either extreme accidentally.
            float focusFraction = math.saturate(wavesCfg.FocusFraction);
            int focusDrones = (int)math.round(droneCount * focusFraction);

            // Track target saturation
            int targetHitCapacity = math.max(
                Engine.Threats.TARGET_MAP_CAPACITY,
                math.max(1, droneCount) + math.max(0, ballisticCount));
            var targetHitCount = new NativeHashMap<Entity, int>(targetHitCapacity, Allocator.Temp);

            // Track category distribution for logging
            int energyCount = 0, criticalCount = 0, serviceCount = 0, civilianCount = 0, groundCount = 0;

            // W6-H3a: Track actual spawned (SpawnShahed/SpawnBallistic return false if prefab missing)
            int actualShahedSpawned = 0;
            int actualBallisticSpawned = 0;

            // PERF: Reset altitude stats for aggregate logging
            m_MinSpawnAltitude = float.MaxValue;
            m_MaxSpawnAltitude = float.MinValue;

            if (m_TargetSource == null)
                m_TargetSource = ServiceRegistry.Instance.Require<IThreatTargetSource>();
            // Force a fresh rebuild: the cache's throttled refresh is gated off in peacetime
            // (Calm), so without this the wave would target a snapshot that predates anything
            // built during the lull. ForceRefreshForWave is unconditional and also marks ready.
            m_TargetSource.ForceRefreshForWave();
            m_TargetSelector.Source = m_TargetSource;
            if (!m_TargetSource.IsReady)
                Log.Warn("Target cache not ready — wave will spawn against empty target lists");

            // Prefab init (UpdateFrameData, OGD.MinLod) is applied once at OnInitialize
            // by CivicPrefabInitSystem — see Core/Systems/Bootstrap/CivicPrefabInitSystem.cs.

            // Off-barrier spawn: append intents into the consumer-owned host buffer. The host is
            // created by ThreatSpawnApplySystem in OnCreate/OnStartRunning/OnLoadRestore (the
            // consumer owns it). Appending to an existing buffer is NOT a structural change, so it
            // is safe to do here in GameSimulation. Do not create the host from this producer —
            // that would be an EntityManager structural change mid-update; if it is somehow
            // missing, skip loudly rather than recover here (the consumer recreates it next start).
            if (!m_IntentHostQuery.TryGetSingletonBuffer<ThreatSpawnIntent>(out var intentBuffer, isReadOnly: false))
            {
                Log.Error($"SpawnWave skipped: ThreatSpawnIntent host missing (wave {waveNumber})");
                return;
            }

            // One key per SpawnWave call — the consumer groups by it and publishes one
            // ThreatsSpawnedEvent per group. Starts at 1 (0 = unset/idempotency-skip sentinel).
            uint waveBatchKey = ++m_SpawnBatchSeq;
            if (waveBatchKey == 0) waveBatchKey = ++m_SpawnBatchSeq; // wrap guard

            try
            {
                // Spawn Shaheds (total threats minus the ballistic subset)
                for (int i = 0; i < droneCount; i++)
                {
                    float categoryRoll = m_Random.NextFloat(0f, 1f);
                    TargetCategory category = ThreatMath.SelectTargetCategory(waveType, variance, categoryRoll, wavesCfg, intro: waveRole == WaveRole.Intro);

                    m_TargetSelector.SetRandom(m_Random); // Sync random state before selector usage
                    bool concentrate = i < focusDrones;
                    var (targetEntity, targetPos, actualCategory) = m_TargetSelector.FindTargetWithFallback(category, targetHitCount, wavesCfg.MaxThreatsPerTarget, concentrate);
                    m_Random = m_TargetSelector.GetRandom(); // Sync random state after selector usage

                    float3 spawnPos = GetRandomSpawnPoint();

                    if (targetEntity != Entity.Null)
                    {
                        // CEP: circular error probable — random offset to simulate guidance inaccuracy.
                        // Focus-cluster drones use a tighter CEP so consecutive hits land on (or
                        // adjacent to) the same building anchor instead of scattering onto neighbours —
                        // otherwise the concentrate selector saturates a target's hit-count while the
                        // CEP-displaced impacts hit different buildings and nothing reaches the destroy
                        // threshold. Some spread remains so a cluster is not pinpoint-perfect.
                        float cep = concentrate
                            ? m_CachedConfig.Threats.FocusStrikeCEP
                            : m_CachedConfig.Threats.ShahedCEP;
                        if (cep > 0f)
                        {
                            float angle = m_Random.NextFloat(0f, 2f * math.PI);
                            float radius = math.sqrt(m_Random.NextFloat(0f, 1f)) * cep;
                            targetPos.x += radius * math.cos(angle);
                            targetPos.z += radius * math.sin(angle);
                            if (Log.IsDebugEnabled) Log.Debug($"[SPAWN] CEP offset: radius={radius:F1}m angle={math.degrees(angle):F0}° target={targetEntity.Index}");
                        }

                        if (SpawnShahed(ref intentBuffer, spawnPos, targetPos, actualCategory, targetEntity.Index, targetEntity.Version, droneEntity, concentrate, waveNumber, waveBatchKey))
                            actualShahedSpawned++;
                        UpdateHitCount(targetHitCount, targetEntity);
                    }
                    else
                    {
                        float3 groundTarget = GetRandomGroundPoint();
                        if (SpawnShahed(ref intentBuffer, spawnPos, groundTarget, TargetCategory.Civilian, -1, 0, droneEntity, false, waveNumber, waveBatchKey))
                            actualShahedSpawned++;
                        groundCount++;
                        civilianCount++;
                        continue;
                    }

                    switch (actualCategory)
                    {
                        case TargetCategory.Energy: energyCount++; break;
                        case TargetCategory.Critical: criticalCount++; break;
                        case TargetCategory.Service: serviceCount++; break;
                        case TargetCategory.Civilian: civilianCount++; break;
                        default:
                            Log.Warn($" Unhandled TargetCategory: {actualCategory}");
                            break;
                    }
                }

                // Spawn Ballistics (MW-based scaling, or override from debug)
                if (ballisticCount > 0)
                {
                    var strategicTargets = m_TargetSelector.GetStrategicTargets();

                    try
                    {
                        for (int i = 0; i < ballisticCount; i++)
                        {
                            m_TargetSelector.SetRandom(m_Random); // Sync random state before selector usage
                            var target = m_TargetSelector.SelectUnsaturatedTarget(strategicTargets, targetHitCount, wavesCfg.MaxThreatsPerTarget);
                            m_Random = m_TargetSelector.GetRandom(); // Sync random state after selector usage
                            if (target.Entity != Entity.Null)
                            {
                                if (SpawnBallistic(ref intentBuffer, target.Position, target.Entity.Index, target.Entity.Version, rocketEntity, waveNumber, waveBatchKey))
                                    actualBallisticSpawned++;
                                UpdateHitCount(targetHitCount, target.Entity);
                            }
                            else
                            {
                                float3 groundTarget = GetRandomGroundPoint();
                                // W6-M13: Use -1 for ground targets (consistent with Shahed, avoids Entity.Index=0 collision)
                                if (SpawnBallistic(ref intentBuffer, groundTarget, -1, 0, rocketEntity, waveNumber, waveBatchKey))
                                    actualBallisticSpawned++;
                            }
                        }
                    }
                    finally
                    {
                        if (strategicTargets.IsCreated) strategicTargets.Dispose();
                    }
                }

                Log.Info($" Recorded {actualShahedSpawned}/{droneCount} Shahed intents: " +
                    $"Energy={energyCount}, Critical={criticalCount}, Service={serviceCount}, " +
                    $"Civilian={civilianCount}, Ground={groundCount} | focus={focusDrones}/{droneCount} (FocusFraction={focusFraction:F2}) | Alt={m_MinSpawnAltitude:F0}-{m_MaxSpawnAltitude:F0}m");

                if (ballisticCount > 0)
                {
                    Log.Info($" + {actualBallisticSpawned}/{ballisticCount} Ballistic intents ({citySizeMW}MW city)");
                }

                // The attach controller picks the new ballistics up on its next pass (the entity
                // exists one GameSimulation pass after the consumer's Modification4 CreateEntity).
                // Arm the one-shot Info line for the first successful attach of this wave.
                if (actualBallisticSpawned > 0)
                    m_ExhaustAttachLogArmed = true;

                // ThreatsSpawnedEvent is NOT published here: the consumer (ThreatSpawnApplySystem)
                // publishes it AFTER the real CreateEntity, so a save in the producer→consumer gap
                // never records a wave as spawned while its entities do not yet exist.
            }
            finally
            {
                if (targetHitCount.IsCreated) targetHitCount.Dispose();
            }
        }

        private void UpdateHitCount(NativeHashMap<Entity, int> hitCount, Entity entity)
        {
            hitCount.TryGetValue(entity, out int count);
            hitCount[entity] = count + 1;
        }

        private int GetCitySizeMW()
        {
            // City SIZE from built nameplate (snapshot) — NOT live production. Ballistic count scales
            // with city size like the rest of the wave; a struck city keeps its size, the readiness
            // gate owns the recovery delay. Falls back to live production until the snapshot is ready.
            // TOCTOU FIX: Use TryGetSingleton instead of IsEmpty + GetSingleton
            if (!m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var grid))
                return 0;
            return WaveContextGatherer.ResolveCitySizeMW(grid.Production);
        }

        private float3 GetRandomGroundPoint()
        {
            // WARN-T-004 fix: bounds check for small maps (margin can't exceed half map size)
            float marginX = math.min(GROUND_POINT_MAX_MARGIN, (m_MapMax.x - m_MapMin.x) * GROUND_POINT_MARGIN_RATIO);
            float marginZ = math.min(GROUND_POINT_MAX_MARGIN, (m_MapMax.z - m_MapMin.z) * GROUND_POINT_MARGIN_RATIO);

            return new float3(
                m_Random.NextFloat(m_MapMin.x + marginX, m_MapMax.x - marginX),
                0f,
                m_Random.NextFloat(m_MapMin.z + marginZ, m_MapMax.z - marginZ)
            );
        }

        private float3 GetRandomSpawnPoint()
        {
            int edge = m_Random.Next(0, 4);
            float t = m_Random.NextFloat(0f, 1f);
            float flyHeight = Engine.Threats.SHAHED_SPAWN_ALTITUDE;

#pragma warning disable CIVIC073 // t = NextFloat(0,1) — guaranteed [0,1]
            float3 pos = edge switch
            {
                0 => new float3(math.lerp(m_MapMin.x, m_MapMax.x, t), 0f, m_MapMax.z),
                1 => new float3(math.lerp(m_MapMin.x, m_MapMax.x, t), 0f, m_MapMin.z),
                2 => new float3(m_MapMin.x, 0f, math.lerp(m_MapMin.z, m_MapMax.z, t)),
                _ => new float3(m_MapMax.x, 0f, math.lerp(m_MapMin.z, m_MapMax.z, t)),
            };
#pragma warning restore CIVIC073

            pos.y = GetTerrainAdjustedAltitude(pos, flyHeight);
            return pos;
        }

        private float GetTerrainAdjustedAltitude(float3 position, float flyHeight)
        {
            var terrainData = m_TerrainSystem.GetHeightData();
            float terrainHeight = TerrainUtils.SampleHeight(ref terrainData, position);

            // Ensure terrain is valid (not negative/underground)
            terrainHeight = math.max(terrainHeight, 0f);

            // Fly at fixed height above terrain
            float totalHeight = terrainHeight + flyHeight;

            // PERF: Track for aggregate logging (no per-drone log)
            m_MinSpawnAltitude = math.min(m_MinSpawnAltitude, totalHeight);
            m_MaxSpawnAltitude = math.max(m_MaxSpawnAltitude, totalHeight);

            return totalHeight;
        }

        private bool SpawnShahed(ref DynamicBuffer<ThreatSpawnIntent> intents, float3 spawnPos, float3 targetPos, TargetCategory category, int targetBuildingIndex, int targetBuildingVersion, Entity droneEntity, bool isFocusStrike, int waveNumber, uint waveBatchKey,
            byte faction = ThreatSpawnIntent.FactionEnemyInbound, AttackCategory outboundAxis = AttackCategory.Kinetic, float outboundDamage = 0f, uint outboundSeed = 0u, ushort outboundTargetId = 0)
        {
            float distance = math.distance(spawnPos, targetPos);
            float3 direction = math.normalizesafe(targetPos - spawnPos);
            quaternion rotation = quaternion.LookRotationSafe(direction, math.up());
            // FIX H27: Use cached config from SpawnWave — prevents torn read on background swap
            float speed = m_CachedConfig.Threats.ShahedSpeed;
            float3 velocity = direction * speed;

            // Off-barrier: record the fully-resolved spawn data. ALL RNG (seed) is drawn here in
            // the producer (GameSimulation); the consumer never touches m_Random. SubMeshCount is
            // computed here so the consumer does no prefab buffer lookup. Render-archetype
            // CreateEntity itself happens in ThreatSpawnApplySystem (Modification4).
            intents.Add(new ThreatSpawnIntent
            {
                Kind = 0,
                Faction = faction,
                OutboundAxis = outboundAxis,
                OutboundDamage = outboundDamage,
                OutboundSeed = outboundSeed,
                OutboundTargetId = outboundTargetId,
                SpawnPos = spawnPos,
                Rotation = rotation,
                Velocity = velocity,
                Speed = speed,
                TotalDistance = distance,
                TargetPos = targetPos,
                TargetBuildingIndex = targetBuildingIndex,
                TargetBuildingVersion = targetBuildingVersion,
                Category = category,
                IsFocusStrike = isFocusStrike,
                PseudoSeed = (ushort)m_Random.Next(1, ushort.MaxValue),
                ThreatGeneration = m_threatGenerationClock?.Current ?? ThreatGenerationClock.Unstamped,
                SubMeshCount = (byte)math.min(byte.MaxValue, GetPrefabSubMeshCount(droneEntity)),
                PrefabIndex = droneEntity.Index,
                PrefabVersion = droneEntity.Version,
                SpawnElapsedTime = SystemAPI.Time.ElapsedTime,
                WaveNumber = waveNumber,
                WaveBatchKey = waveBatchKey
            });
            IncrementEcbCount();

            if (Log.IsDebugEnabled) Log.Debug($"[SPAWN:INTENT] Shahed prefabEntity={droneEntity.Index} pos=({spawnPos.x:F0},{spawnPos.y:F0},{spawnPos.z:F0})");

            return true;
        }

        private bool SpawnBallistic(ref DynamicBuffer<ThreatSpawnIntent> intents, float3 targetPos, int targetBuildingIndex, int targetBuildingVersion, Entity rocketEntity, int waveNumber, uint waveBatchKey,
            byte faction = ThreatSpawnIntent.FactionEnemyInbound, AttackCategory outboundAxis = AttackCategory.Kinetic, float outboundDamage = 0f, uint outboundSeed = 0u, ushort outboundTargetId = 0, float3? spawnPosOverride = null)
        {
            // FIX H27: Use cached config from SpawnWave
            var cfg = m_CachedConfig.Threats;

            // Ballistic missiles launch from the map edge (like drones) and fly a lofted arc
            // to the target — BallisticMovementJobEntity climbs, cruises high, then dives
            // near-vertically. Spawn low at the perimeter; the arc handles the altitude.
            // Outbound counter-strikes invert this: they launch from the player's map
            // (spawnPosOverride) toward a frontier past the edge, so the launch point is
            // supplied rather than drawn from the perimeter.
            float3 spawnPos = spawnPosOverride ?? GetRandomSpawnPoint();
            spawnPos.y = GetTerrainAdjustedAltitude(spawnPos, Engine.Threats.BALLISTIC_LAUNCH_ALTITUDE);

            // Initial direction points along the first arc step (mostly the climb); the job
            // recomputes heading every tick from the arc, so this is just the spawn frame.
            float3 direction = math.normalizesafe(targetPos - spawnPos);
            quaternion rotation = quaternion.LookRotationSafe(direction, math.up());
            float3 velocity = direction * cfg.BallisticSpeed;

            // Off-barrier: record intent. No TransformFrame preseed for ballistic (dead code —
            // UpdateGroupSystem rewrites the buffer before anything reads it); the consumer skips
            // it for Kind=1. RNG (seed) drawn here in the producer.
            intents.Add(new ThreatSpawnIntent
            {
                Kind = 1,
                Faction = faction,
                OutboundAxis = outboundAxis,
                OutboundDamage = outboundDamage,
                OutboundSeed = outboundSeed,
                OutboundTargetId = outboundTargetId,
                SpawnPos = spawnPos,
                Rotation = rotation,
                Velocity = velocity,
                Speed = cfg.BallisticSpeed,
                TotalDistance = 0f,
                TargetPos = targetPos,
                TargetBuildingIndex = targetBuildingIndex,
                TargetBuildingVersion = targetBuildingVersion,
                Category = default,
                IsFocusStrike = false,
                ImpactRadius = cfg.BallisticImpactRadius,
                DamageSeverity = ThreatConstants.BALLISTIC_IMPACT_SEVERITY,
                PseudoSeed = (ushort)m_Random.Next(1, ushort.MaxValue),
                ThreatGeneration = m_threatGenerationClock?.Current ?? ThreatGenerationClock.Unstamped,
                SubMeshCount = (byte)math.min(byte.MaxValue, GetPrefabSubMeshCount(rocketEntity)),
                PrefabIndex = rocketEntity.Index,
                PrefabVersion = rocketEntity.Version,
                SpawnElapsedTime = SystemAPI.Time.ElapsedTime,
                WaveNumber = waveNumber,
                WaveBatchKey = waveBatchKey
            });
            IncrementEcbCount();

            return true;
        }

        // ============================================================================
        // IOutboundStrikeService — player outbound counter-strike launch (Axiom 5 boundary)
        // ============================================================================

        /// <summary>
        /// True once the AttackDrone prefab is resolved — the minimum needed to launch an
        /// outbound projectile (ballistic falls back to the drone prefab when its own is absent,
        /// mirroring SpawnWave). Reads the demand-resolving CivicPrefabInitSystem accessor.
        /// </summary>
        public bool CanLaunch => m_PrefabInit != null && m_PrefabInit.AttackDroneEntity != Entity.Null;

        /// <summary>
        /// Record an off-barrier outbound <see cref="ThreatSpawnIntent"/> (Faction=PlayerOutbound)
        /// for a player counter-strike of <paramref name="kind"/>, carrying the
        /// (<paramref name="axis"/>, <paramref name="damage"/>) payload resolved at arrival.
        /// Reuses this producer's prefab/bounds/RNG and the same intent-append path as a wave; the
        /// render-archetype CreateEntity happens in ThreatSpawnApplySystem (Modification4),
        /// render-safe. Called synchronously on the main thread from the GridWarfare effect owner
        /// (ModificationEnd) — appending to the existing host buffer is NOT a structural change, so
        /// it is pause-safe there. Returns false (records nothing) when prefabs are unresolved or
        /// the intent host is missing.
        /// </summary>
        public bool Launch(ArsenalKind kind, AttackCategory axis, float damage, uint seed, ushort targetId)
        {
            Entity droneEntity = m_PrefabInit.AttackDroneEntity;
            if (droneEntity == Entity.Null)
                return false; // prefabs not resolved yet — caller leaves the operation uncommitted

            // Outbound uses the same per-call config snapshot discipline as SpawnWave (H27): one
            // read of BalanceConfig.Current. SpawnShahed/SpawnBallistic read m_CachedConfig.
            m_CachedConfig = BalanceConfig.Current;

            if (!m_BoundsCached)
                CacheMapBounds();

            if (!m_IntentHostQuery.TryGetSingletonBuffer<ThreatSpawnIntent>(out var intentBuffer, isReadOnly: false))
            {
                Log.Error("Outbound launch skipped: ThreatSpawnIntent host missing");
                return false;
            }

            // Launch origin is per-kind (small jitter on top so repeated launches don't stack):
            // a drone flies from a built drone launcher, a ballistic still leaves the map centre.
            // No launcher for a drone strike → nothing to launch from; the caller rolls the
            // operation back exactly like a "producer unavailable" failure.
            if (!TryGetLaunchPosition(kind, out float3 basePos))
            {
                Log.Warn($"Launch blocked: no drone launcher built (kind={kind})");
                return false;
            }
            float3 spawnPos = new float3(
                basePos.x + m_Random.NextFloat(-OUTBOUND_LAUNCH_JITTER, OUTBOUND_LAUNCH_JITTER),
                0f,
                basePos.z + m_Random.NextFloat(-OUTBOUND_LAUNCH_JITTER, OUTBOUND_LAUNCH_JITTER));
            float3 targetPos = ComputeOutboundFrontier(spawnPos);

            // A new batch key per launch so the consumer publishes its own ThreatsSpawnedEvent
            // group; waveNumber 0 (no wave). Starts at 1 (0 reserved).
            uint batchKey = ++m_SpawnBatchSeq;
            if (batchKey == 0) batchKey = ++m_SpawnBatchSeq;

            bool recorded;
            if (kind == ArsenalKind.Ballistic)
            {
                Entity rocketEntity = m_PrefabInit.RocketEntity;
                if (rocketEntity == Entity.Null)
                    rocketEntity = droneEntity; // mirror SpawnWave's ballistic→drone fallback
                recorded = SpawnBallistic(ref intentBuffer, targetPos, -1, 0, rocketEntity, waveNumber: 0, batchKey,
                    faction: ThreatSpawnIntent.FactionPlayerOutbound, outboundAxis: axis, outboundDamage: damage, outboundSeed: seed, outboundTargetId: targetId, spawnPosOverride: spawnPos);
            }
            else
            {
                spawnPos.y = GetTerrainAdjustedAltitude(spawnPos, Engine.Threats.SHAHED_SPAWN_ALTITUDE);
                recorded = SpawnShahed(ref intentBuffer, spawnPos, targetPos, TargetCategory.Civilian, -1, 0, droneEntity, isFocusStrike: false, waveNumber: 0, batchKey,
                    faction: ThreatSpawnIntent.FactionPlayerOutbound, outboundAxis: axis, outboundDamage: damage, outboundSeed: seed, outboundTargetId: targetId);
            }

            if (recorded)
                Log.Info($"[SPAWN:INTENT] Outbound {kind} faction=PlayerOutbound axis={axis} dmg={damage:F1} launcher=({basePos.x:F0},{basePos.z:F0}) from=({spawnPos.x:F0},{spawnPos.z:F0}) to=({targetPos.x:F0},{targetPos.z:F0})");
            return recorded;
        }

        /// <summary>
        /// Frontier target past the nearest map edge for an outbound launch: pushes the launch
        /// point out to whichever edge it is closest to, plus <see cref="OUTBOUND_FRONTIER_MARGIN"/>,
        /// so the projectile crosses the boundary in the shortest direction (developer fork #2 —
        /// the exact beachhead is the 2D target window's concern, not this 3D flight leg).
        /// </summary>
        private float3 ComputeOutboundFrontier(float3 from)
        {
            float distLeft = from.x - m_MapMin.x;
            float distRight = m_MapMax.x - from.x;
            float distBottom = from.z - m_MapMin.z;
            float distTop = m_MapMax.z - from.z;

            // Pick the nearest map edge without float-equality on a computed min
            // (CIVIC072): <= over the four edge distances, ties resolve
            // left → right → bottom → top.
            float3 target = from;
            if (distLeft <= distRight && distLeft <= distBottom && distLeft <= distTop)
                target.x = m_MapMin.x - OUTBOUND_FRONTIER_MARGIN;
            else if (distRight <= distBottom && distRight <= distTop)
                target.x = m_MapMax.x + OUTBOUND_FRONTIER_MARGIN;
            else if (distBottom <= distTop)
                target.z = m_MapMin.z - OUTBOUND_FRONTIER_MARGIN;
            else
                target.z = m_MapMax.z + OUTBOUND_FRONTIER_MARGIN;
            target.y = 0f;
            return target;
        }

        /// <summary>
        /// Base launch origin for an outbound strike of <paramref name="kind"/> (jitter is added by
        /// the caller). A drone launches from the built <see cref="DroneLauncherInstallation"/>
        /// nearest the enemy edge (the map perimeter the projectile crosses); no launcher built →
        /// false, and the caller rolls the operation back. A ballistic keeps the map-centre origin
        /// until its own launcher building ships — add an enum+table row here when it does.
        /// </summary>
        [CompletesDependency("TryGetLaunchPosition: main-thread Transform read of built drone launchers at outbound-drone launch — one search per launch (a player click), mirroring the Center placement snap lookup. Zero cost for ballistic (map-centre origin, no ECS read).")]
        private bool TryGetLaunchPosition(ArsenalKind kind, out float3 pos)
        {
            if (kind != ArsenalKind.Drone)
            {
                // Ballistic launcher building lands later; until then the ballistic origin is the
                // map centre exactly as before.
                pos = (m_MapMin + m_MapMax) * 0.5f;
                pos.y = 0f;
                return true;
            }

            pos = default;
            if (m_DroneLauncherQuery.IsEmpty)
                return false;

            var installs = m_DroneLauncherQuery.ToComponentDataArray<DroneLauncherInstallation>(Allocator.Temp);
            // Building Transform is a render component: the read goes through the render-write barrier
            // ticket (BuildingTransform has zero out-of-band producers today, so the Consume is free —
            // the ticket is the routing contract, not a wait).
            var ticket = ServiceRegistry.Instance.Require<IRenderWriteBarrier>()
                .Consume(typeof(ThreatSpawnSystem), RenderWriteComponentMask.BuildingTransform);
            bool found = false;
            float bestEdgeDist = float.MaxValue;
            for (int i = 0; i < installs.Length; i++)
            {
                // A demolished launcher's installation can survive a tick before ModEntityCleanupSystem
                // deletes it, so trust only a launcher whose building still exists and has a transform.
                Entity building = installs[i].GetBuildingEntity();
                if (building == Entity.Null || !EntityManager.Exists(building))
                    continue;
                if (!BuildingDamageHelper.TryGetBuildingTransform(EntityManager, building, ticket, out var transform))
                    continue;

                float edgeDist = DistanceToNearestEdge(transform.m_Position);
                if (edgeDist < bestEdgeDist)
                {
                    bestEdgeDist = edgeDist;
                    pos = transform.m_Position;
                    pos.y = 0f;
                    found = true;
                }
            }
            installs.Dispose();
            return found;
        }

        // Metres from a point to whichever map edge is nearest — the same four-edge distance the
        // outbound frontier uses to pick the crossing edge. Smallest value = most forward toward the
        // enemy line, which is how the launch launcher is chosen.
        private float DistanceToNearestEdge(float3 p)
        {
            float distLeft = p.x - m_MapMin.x;
            float distRight = m_MapMax.x - p.x;
            float distBottom = p.z - m_MapMin.z;
            float distTop = m_MapMax.z - p.z;
            return math.min(math.min(distLeft, distRight), math.min(distBottom, distTop));
        }

        private int GetPrefabSubMeshCount(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null || !EntityManager.HasBuffer<SubMesh>(prefabEntity))
                return 1;
#pragma warning disable CIVIC051 // Spawn-time prefab buffer lookup; one per spawned threat, not a per-entity query loop.
            return math.max(1, EntityManager.GetBuffer<SubMesh>(prefabEntity, true).Length);
#pragma warning restore CIVIC051
        }
    }
}

