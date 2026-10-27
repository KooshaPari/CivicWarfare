using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.AirDefense;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.AirDefense.Jobs;
using CivicSurvival.Domains.AirDefense.Logic;
using System.Threading;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Air Defense Orchestrator — pure phase coordinator.
    ///
    /// Owns ECS queries, lookups, and lifecycle wiring. Delegates:
    /// - Targeting pipeline (Match → Residential → Scoring) → <see cref="TargetingPipelineDriver"/>
    /// - Fire control + static tree caching → <see cref="FireControlCoordinator"/>
    /// - Cross-system shot counters → <see cref="AirDefenseShotCounter"/>
    ///
    /// Async pipeline:
    /// - Job chain: Match → Residential → Scoring (NO Complete() in hot path)
    /// - Main thread fire control runs on N-1 frame results (parallel with N jobs)
    /// - 1-frame latency trade-off for zero blocking
    ///
    /// Flow per frame:
    /// 1. Tick frame timers (fire-control tree refresh)
    /// 2. No-AA early exit
    /// 3. Update lookups
    /// 4. Process N-1 targeting results: fire control on main thread
    /// 5. Schedule N targeting jobs (async)
    ///
    /// S16a-2 ACCEPTED: Credits singleton dual-writer resolved — AirDefenseStateSystem is the single writer for credits.
    /// S16a-4 ACCEPTED: Pipeline state is ephemeral (reconstructed on first frame after load); only random state serialized.
    /// S16a-5 ACCEPTED: Mid-wave AA with zero crew — design limitation; player can assign crew via panel.
    /// S16a-6 ACCEPTED: Cooldown is a passive persisted game-time timestamp, no per-tick job needed.
    /// S16a-9 ACCEPTED: ShotsFired is main-thread only. BDS increments via AirDefenseShotCounter — flush system flushes total to singleton.
    /// S16b-1 ACCEPTED: Ammo decremented before intercept chance calc — low-ammo penalty triggers 1 shot earlier; negligible.
    ///
    /// CROSS-DOMAIN STALENESS — ACCEPTED (H11/H12/H13):
    /// H11: No ordering vs TMS (ThreatFlight domain). ADO reads threat positions up to 1 targeting-throttle
    ///      cycle stale (~15m at 150m/s, 10Hz). Engagement range 300-600m — positional error is noise.
    /// H12: No ordering vs SpotterAggregateSystem (Spotters domain). SpotterPenaltyState changes on wave
    ///      events (~1/min). 1-frame stale = 16ms on a minute-scale event — irrelevant.
    /// H13: No ordering vs BudgetResolutionSystem (Services). BuildingPlacementCommitSystem checks
    ///      BudgetResolved flag and skips unresolved intents. 500ms re-check = by-design throttle.
    /// W2-H8: No ordering vs ThreatIdentifySystem (ThreatUI domain). IdentifiedTarget accuracy bonus
    ///      1-frame stale on identification frame — same magnitude as H11 positional staleness.
    /// Cross-domain RegisterAfter] would introduce coupling worse than the 1-frame latency it solves.
    /// </summary>
#pragma warning disable CA1001 // m_TargetingDriver disposed in OnDestroy (ECS lifecycle, not IDisposable pattern)
    [ActIndependent]
    [HotPathSystem]
    public partial class AirDefenseOrchestrator : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // ECB command counter — read by PerfReportSections for telemetry
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);

        private const int TARGETING_THROTTLE_FRAMES = 6;

        private static readonly LogContext Log = new("AirDefenseOrchestrator");

        // ============================================================================
        // QUERIES
        // ============================================================================
        private EntityQuery m_ThreatQuery;
        private EntityQuery m_AAQuery;
        // Cross-domain singletons (read each tick via FireControlCoordinator)
        private EntityQuery m_TelemarathonQuery;
        private EntityQuery m_SpotterPenaltyQuery;

        // ============================================================================
        // COMPONENT LOOKUPS — owned here, snapshotted into FireControlEcsLookups each frame
        // ============================================================================
        private ComponentLookup<Shahed> m_ShahedLookup;
        // CROSS-SYSTEM DEPENDENCY INVARIANT (do NOT remove m_CombatStateLookup / m_PriorityTargetLookup
        // as "unused"): they register ShahedCombatState / PriorityTarget in this system's reader/writer
        // lists. CollectThreatJob (scheduled on Dependency) reads both component types; their writers
        // (the TMS movement graph) only reach the job's incoming Dependency through these registrations.
        // Drop a lookup and its writer falls out of Dependency → the collect job races it = data race /
        // native AV. m_CombatStateLookup is also used by ExecuteFireControl; m_PriorityTargetLookup is
        // also passed into CollectThreatJob directly.
#pragma warning disable CIVIC269 // FireControlExecutor owns direct miss/intercept merges on combat state.
        private ComponentLookup<ShahedCombatState> m_CombatStateLookup;
#pragma warning restore CIVIC269
        private ComponentLookup<ActiveThreat> m_ActiveThreatLookup;
        private ComponentLookup<PendingDestruction> m_PendingDestructionLookup;
        private ComponentLookup<PlayerOutboundThreat> m_PlayerOutboundLookup;
        private ComponentLookup<Building> m_BuildingLookup;
#pragma warning disable CIVIC269 // RW lookup passed to FireControlExecutor for direct writes (H02 fix)
        private ComponentLookup<AirDefenseInstallation> m_AALookup;
#pragma warning restore CIVIC269
#pragma warning disable CIVIC269 // RW lookup passed to FireControlExecutor for direct cooldown timestamp writes on fire.
        private ComponentLookup<AirDefenseCooldown> m_CooldownLookup;
#pragma warning restore CIVIC269
        private ComponentLookup<PriorityTarget> m_PriorityTargetLookup;
        private ComponentLookup<IdentifiedTarget> m_IdentifiedTargetLookup;
        private ComponentLookup<Simulate> m_SimulateLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;

        // ============================================================================
        // TYPE HANDLES — for the async CollectThreatJob (chunk iteration on a worker)
        // ============================================================================
        private ComponentTypeHandle<Shahed> m_ShahedTypeHandle;
        private ComponentTypeHandle<ShahedCombatState> m_CombatStateTypeHandle;
        private ComponentTypeHandle<ThreatPosition> m_ThreatPositionTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;

        // ============================================================================
        // HELPERS (composed)
        // ============================================================================
        [System.NonSerialized] private readonly FireControlCoordinator m_FireControl = new();
        [System.NonSerialized] private TargetingPipelineDriver m_TargetingDriver = null!;

        // ============================================================================
        // VANILLA / CIVIC SYSTEM REFS
        // ============================================================================
        private SearchSystem m_SearchSystem = null!;
        private GameSimulationEndBarrier m_ECBSystem = null!;
        private InterceptBarrier m_InterceptBarrier = null!;
        // Shared live-AA snapshot (order-version-gated rebuild). Registered before ADO so the snapshot
        // is fresh this frame. CollectAAData reads static AA fields (position/range/type) from here and
        // re-reads ammo/crew/cooldown LIVE per entity — the per-frame Simulate+Transform drain moves to
        // the cache's rare rebuild.
        private LiveAACacheSystem m_liveAACache = null!;
        private IRenderWriteBarrier m_RenderWriteBarrier = null!;
        private ResidentialCacheSystem m_ResidentialCache = null!;
        private AirDefensePolicySystem m_PolicySystem = null!;
        // Global player toggle: does Patriot engage drones? Read synchronously on the main
        // thread in CollectAAData. Fail-closed null-object (false) when AirDefense owner is
        // unavailable — Patriot stays reserved for ballistics.
        private IPatriotDroneInterceptReader m_PatriotDroneReader = null!;
        [System.NonSerialized] private CivicDependencyWire m_DependencyWire = null!;
        [System.NonSerialized] private bool m_DriverWired;

        // ============================================================================
        // PERSISTED STATE
        // ============================================================================
        private SerializableRandom m_Random;

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            InitializeQueries();
            InitializeLookups();
            ResolveVanillaSystems();
            InitializeRuntimeState();
            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            ResolveCivicSystems();
            WireDriverIfNeeded();
        }

        private void InitializeQueries()
        {
            // R11-S03: Exclude Deleted threats to avoid wasted targeting pipeline execution
            // R12-F04: Exclude PendingDestruction to avoid phantom shots during wave transition cleanup
#pragma warning disable CIVIC340 // Targeting wants absent-or-disabled PendingDestruction; disabled tag means live threat.
            // PERF-LOCK: Exclude<PlayerOutboundThreat> keeps the player's own outbound counter-strikes
            // out of the AA candidate set at the query level — CollectThreatJob never iterates them, so
            // own-AA never scores/scans/fires on own projectiles on the hot targeting path. Filtering by
            // a byte read inside the scoring loop instead would re-introduce the per-candidate hot-loop
            // cost this query-level exclude avoids. Inbound waves carry the bit disabled (set only by the
            // outbound producer in ThreatSpawnApplySystem), so the enableable exclude leaves them in.
            // Require ShahedCombatState: CollectThreatJob reads combats[i].IsIntercepted for
            // every entity in the chunk. A chunk whose archetype lacks ShahedCombatState yields
            // an empty component array, so combats[i] becomes an out-of-bounds read — a process-
            // killing access violation in a Burst worker (bounds checks stripped). The drone
            // archetype always co-carries it today, but the read must be backed by a query
            // requirement, not an implicit spawn invariant — symmetric with ThreatMovementSystem's
            // query, which already requires it. Drones are not excluded (every Shahed has it);
            // ballistics carry no Shahed and were never matched.
            m_ThreatQuery = GetEntityQuery(
                ComponentType.ReadOnly<Shahed>(),
                ComponentType.ReadOnly<ShahedCombatState>(),
                ComponentType.ReadOnly<ThreatPosition>(),
                ComponentType.ReadOnly<ActiveThreat>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<PendingDestruction>(),
                ComponentType.Exclude<PlayerOutboundThreat>()
            );
#pragma warning restore CIVIC340

            // NOTE: AirDefenseInstallation is on a separate entity (not on the placed AA object,
            // which is a StaticObjectPrefab prop). Transform lives on that AA object — resolve
            // it via the stored Index/Version lookup.
            m_AAQuery = GetEntityQuery(
                ComponentType.ReadWrite<AirDefenseInstallation>(),
                ComponentType.ReadOnly<Simulate>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Destroyed>()
            );

            // Cross-domain singletons read per-tick via FireControlCoordinator
            m_TelemarathonQuery = GetEntityQuery(ComponentType.ReadOnly<TelemarathonRuntimeState>());
            m_SpotterPenaltyQuery = GetEntityQuery(ComponentType.ReadOnly<SpotterPenaltyState>());
        }

        private void InitializeLookups()
        {
            m_ShahedLookup = GetComponentLookup<Shahed>(true);
            m_CombatStateLookup = GetComponentLookup<ShahedCombatState>(false);
            m_ActiveThreatLookup = GetComponentLookup<ActiveThreat>(true);
            m_PendingDestructionLookup = GetComponentLookup<PendingDestruction>(true);
            m_PlayerOutboundLookup = GetComponentLookup<PlayerOutboundThreat>(true);
            m_BuildingLookup = GetComponentLookup<Building>(true);
            m_AALookup = GetComponentLookup<AirDefenseInstallation>(false);
            m_CooldownLookup = GetComponentLookup<AirDefenseCooldown>(false);
            m_PriorityTargetLookup = GetComponentLookup<PriorityTarget>(true);
            m_IdentifiedTargetLookup = GetComponentLookup<IdentifiedTarget>(true);
            m_SimulateLookup = GetComponentLookup<Simulate>(true);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();

            // Type handles for the async CollectThreatJob (Axiom 8: created in OnCreate).
            m_ShahedTypeHandle = GetComponentTypeHandle<Shahed>(true);
            m_CombatStateTypeHandle = GetComponentTypeHandle<ShahedCombatState>(true);
            m_ThreatPositionTypeHandle = GetComponentTypeHandle<ThreatPosition>(true);
            m_EntityTypeHandle = GetEntityTypeHandle();
        }

        private void ResolveVanillaSystems()
        {
            m_SearchSystem = World.GetOrCreateSystemManaged<SearchSystem>();
            m_ECBSystem = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_DependencyWire = new CivicDependencyWire(nameof(AirDefenseOrchestrator));
            // InterceptBarrier is a CoreKernel scheduling anchor — vanilla-style resolve.
            m_InterceptBarrier = World.GetOrCreateSystemManaged<InterceptBarrier>();
        }

        private void ResolveCivicSystems()
        {
            m_RenderWriteBarrier ??= ServiceRegistry.Instance.Require<IRenderWriteBarrier>();
            // Cross-system caches resolved in OnStartRunning — independent of RegisterAt order
            // and feature priority. Both fields live in the same AirDefense feature.
            m_ResidentialCache ??= m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<ResidentialCacheSystem>());
            m_PolicySystem ??= m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<AirDefensePolicySystem>());
            // Live-AA snapshot owner — feature-gated system, resolve via FeatureRegistry (CIVIC400/403);
            // same AirDefense feature, registration orders it before ADO.
            m_liveAACache ??= m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<LiveAACacheSystem>());
            // Same AirDefense feature owns the toggle flag; resolve as fail-closed null-object.
            m_PatriotDroneReader = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullPatriotDroneInterceptReader.Instance);
        }

        private void InitializeRuntimeState()
        {
            AirDefenseShotCounter.Reset();

            var timeProvider = GameTimeSystem.Instance;
            float gameHours;
            if (timeProvider != null)
                gameHours = timeProvider.Current.TotalGameHours;
            else
            {
                Mod.Log.Warn("AirDefenseOrchestrator.OnCreate: GameTimeSystem unavailable — using TickCount-only seed");
                gameHours = 0f;
            }
            // Mix TickCount so seed is unique per session even when TotalGameHours=0
            int seed = unchecked((int)(gameHours * GameRate.SECONDS_PER_HOUR) ^ System.Environment.TickCount);
            m_Random = new SerializableRandom(seed);

            // Helpers
            m_TargetingDriver = new TargetingPipelineDriver(TARGETING_THROTTLE_FRAMES);
            m_FireControl.Initialize(m_SearchSystem, m_InterceptBarrier, m_TelemarathonQuery, m_SpotterPenaltyQuery);
        }

        private void WireDriverIfNeeded()
        {
            if (m_DriverWired) return;
            m_TargetingDriver.Wire(m_ResidentialCache, m_PolicySystem, m_RenderWriteBarrier, m_ThreatQuery);
            m_DriverWired = true;
        }

        protected override void OnDestroy()
        {
            m_TargetingDriver?.Dispose();

            base.OnDestroy();
            Log.Info("Destroyed");
        }

        // ============================================================================
        // MAIN UPDATE
        // ============================================================================

        protected override void OnUpdateImpl()
        {
            // After reset/load-restore m_DriverWired is cleared but OnStartRunning may
            // not re-fire; re-wire here as a safety net (cheap idempotent assignment
            // when already wired).
            WireDriverIfNeeded();

            if (TryHandleNoAAEarlyExit())
                return;

            if (!GameTimeSystem.TryGetTotalGameSeconds(out var now))
                return;

            UpdateFrameLookups();
            ProcessPreviousFrameTargetingResults(now);
            TryScheduleCurrentFrameTargetingJobs(now);
        }

        private bool TryHandleNoAAEarlyExit()
        {
            // Early exit if no AA installations — avoid targeting queries/lookups on vanilla entities.
            // This prevents sync points with vanilla Household/SubElement systems.
            if (!m_AAQuery.IsEmpty)
                return false;

            m_TargetingDriver.ForceComplete();
            m_TargetingDriver.ClearGenerations();
            return true;
        }

        private void UpdateFrameLookups()
        {
            m_ShahedLookup.Update(this);
            m_CombatStateLookup.Update(this);
            m_ActiveThreatLookup.Update(this);
            m_PendingDestructionLookup.Update(this);
            m_PlayerOutboundLookup.Update(this);
            m_BuildingLookup.Update(this);
            m_PriorityTargetLookup.Update(this);
            m_IdentifiedTargetLookup.Update(this);
            m_AALookup.Update(this);
            m_CooldownLookup.Update(this);
            m_SimulateLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
            m_StorageInfoLookup.Update(this);

            // Type handles for the async CollectThreatJob (Axiom 8: .Update in OnUpdate).
            m_ShahedTypeHandle.Update(this);
            m_CombatStateTypeHandle.Update(this);
            m_ThreatPositionTypeHandle.Update(this);
            m_EntityTypeHandle.Update(this);
        }

        private void ProcessPreviousFrameTargetingResults(double nowGameSeconds)
        {
            if (!m_TargetingDriver.HasPendingJob)
                return;

            using (PerformanceProfiler.Measure("AirDefenseOrchestrator.ProcessResults"))
            {
                bool hasSnap = m_TargetingDriver.TryConsumePreviousSnapshot(out var snap, out var completed);
                if (!completed)
                    return;
                if (hasSnap)
                {
                    MarkCandidateBits(in snap);
                    ExecuteFireControl(in snap, nowGameSeconds);
                    m_ECBSystem.AddJobHandleForProducer(Dependency);
                }
                m_TargetingDriver.PromoteAfterProcess();
            }
        }

        /// <summary>
        /// Leak-attribution funnel, coverage bit: stamp <see cref="ShahedCombatState.WasCandidate"/>
        /// on every drone the N-1 match produced a (threat, ready-AA) pair for. Runs on the MAIN
        /// thread over the consumed snapshot — deliberately not a job: ShahedCombatState must keep
        /// zero Burst-job writers (PERF-LOCK in InterceptProcessingSystem — RO main-thread readers
        /// there and in ThreatTerminalizationSystem complete nothing only while that holds). The
        /// snapshot is one frame stale; harmless for classification because a shot resolved this
        /// frame implies candidacy, and ClassifyLeak checks WasEngaged before WasCandidate.
        /// </summary>
        private void MarkCandidateBits(in TargetingSnapshot snap)
        {
            var candidates = snap.ScoredCandidates;
            int count = System.Math.Min(snap.CandidateCount, candidates.Length);
            for (int i = 0; i < count; i++)
            {
                var entity = new Entity
                {
                    Index = candidates[i].ThreatEntityIndex,
                    Version = candidates[i].ThreatEntityVersion
                };
                if (!m_CombatStateLookup.TryGetComponent(entity, out var state) || state.WasCandidate)
                    continue;
                state.WasCandidate = true;
#pragma warning disable CIVIC035 // TryGetComponent above validated the live component this frame.
                m_CombatStateLookup[entity] = state;
#pragma warning restore CIVIC035
            }
        }

        private void TryScheduleCurrentFrameTargetingJobs(double nowGameSeconds)
        {
            if (m_TargetingDriver.HasPendingJob) return;

            // Refresh residential cache in the safe window — after N-1 jobs completed, before new scheduling.
            m_TargetingDriver.RefreshResidentialIfPending();

            if (!m_TargetingDriver.ShouldScheduleThisFrame()) return;
            // Guard — ResidentialCacheSystem populates on first frame; skip until ready
            if (!m_TargetingDriver.IsResidentialReady) return;

            RenderWriteTicket renderTicket = m_TargetingDriver.BeginFill(GetType());

            CollectAAData(renderTicket, nowGameSeconds);
            if (m_TargetingDriver.AAWriteCount == 0)
            {
                m_TargetingDriver.AbortFillAndInvalidate();
                return;
            }

            // No-sync upper bound on the threat count (chunk metadata, not CalculateEntityCount).
            // The async CollectThreatJob fills the actual <= upperBound threats on a worker.
            int upperBound = m_TargetingDriver.UpperBoundThreatCount;

            // Size candidate / collect / residential buffers to the upper bound before scheduling
            // (collect + match use AddNoResize — capacity must cover worst case up front).
            bool prepared = m_TargetingDriver.PrepareCandidates(upperBound, m_TargetingDriver.AAWriteCount);
            if (!prepared)
            {
                // Candidate buffer could not be sized to cover worst-case upperBound*A matches —
                // scheduling the match job would overrun AddNoResize. Abort safely; the
                // store logs the cause (throttled). Pipeline retries next throttle tick.
                m_TargetingDriver.AbortFillAndInvalidate();
                return;
            }

            var collectHandles = new CollectThreatJobHandles(
                m_ShahedTypeHandle,
                m_CombatStateTypeHandle,
                m_ThreatPositionTypeHandle,
                m_EntityTypeHandle,
                m_PriorityTargetLookup);

            var finalHandle = m_TargetingDriver.ScheduleJobChain(Dependency, in collectHandles);
            Dependency = JobHandle.CombineDependencies(Dependency, finalHandle);
        }

        // ============================================================================
        // DATA COLLECTION (AA collection on the main thread; threats collected async via CollectThreatJob)
        // ============================================================================

        private void CollectAAData(RenderWriteTicket renderTicket, double nowGameSeconds)
        {
            // ClearWriteFrame() already called by driver.BeginFill — just add.
            // Source of truth for the AA SET is LiveAACacheSystem's order-version-gated snapshot:
            //   static fields (Position/RangeSq/InterceptChance/Type/Building) come from the cache, so
            //   CollectAAData no longer runs the per-frame .WithAll<Simulate>() query (no Simulate drain)
            //   and no longer reads Game.Objects.Transform here (no Transform drain — Position is cached).
            // The per-AA gameplay GATES (Simulate liveness, ammo, crew, cooldown) are re-read LIVE per
            // entity via the existing lookups — never taken from the snapshot, which can be stale between
            // rebuilds.
            var snapshot = m_liveAACache.GetLiveAASnapshot();

            for (int i = 0; i < snapshot.Length; i++)
            {
                var aaData = snapshot[i];
                var entity = aaData.GetEntity();

                // LIVE re-read of the installation by entity: validates Simulate-enabled liveness,
                // !Deleted/!Destroyed, live linked building, and returns the current component (ammo/crew).
                if (!AirDefenseLifecycle.TryGetActiveInstallation(
                        entity,
                        m_AALookup,
                        m_StorageInfoLookup,
                        m_SimulateLookup,
                        m_DeletedLookup,
                        m_DestroyedLookup,
                        out var activeAA))
                    continue;

                // LIVE gates (not from snapshot) — cooldown/ammo/crew change per shot.
                if (m_CooldownLookup.TryGetComponent(entity, out var cd) && nowGameSeconds < cd.ReadyAtGameSeconds) continue;
                if (activeAA.CurrentAmmo <= 0) continue;
                if (activeAA.CrewAssigned <= 0) continue;

                // Patriot is an anti-ballistic SAM; it engages drones only when the player
                // explicitly opts in (global toggle, default OFF). Gating at the targeting-set
                // entry is the root point — at OFF the Patriot never enters the drone pipeline,
                // so no in-flight scoring/fire spends its expensive missiles on a Shahed.
                // Ballistic interception (BallisticDefenseSystem, separate phase) is unaffected:
                // Patriot always intercepts ballistics. Reader read is a synchronous field, not a sync point.
                if (aaData.Type == AAType.PatriotSAM && !m_PatriotDroneReader.PatriotInterceptsDrones)
                    continue;

                // BuildingTransform is still written downstream from the cached static position; keep
                // the render-write ticket contract so consumers see the mask.
                TargetingPipelineDriver.EnsureRenderTicket(renderTicket, RenderWriteComponentMask.BuildingTransform);

                m_TargetingDriver.AddAA(new AAData
                {
                    EntityIndex = entity.Index,
                    EntityVersion = entity.Version,
                    // Static AA fields from the cache (position is building-static — no Transform read).
                    Position = aaData.Position,
                    RangeSq = aaData.RangeSq,
                    InterceptChance = aaData.InterceptChance,
                    Type = aaData.Type,
                    Building = aaData.Building,
                    // Live per-shot fields from the fresh installation re-read.
                    CooldownDuration = activeAA.CooldownDuration,
                    CurrentAmmo = activeAA.CurrentAmmo,
                    MaxAmmo = activeAA.MaxAmmo
                });
            }
        }

        // ============================================================================
        // FIRE CONTROL (delegated to FireControlCoordinator)
        // ============================================================================

        private void ExecuteFireControl(in TargetingSnapshot snap, double nowGameSeconds)
        {
            var lookups = new FireControlEcsLookups(
                m_ShahedLookup,
                m_CombatStateLookup,
                m_ActiveThreatLookup,
                m_PendingDestructionLookup,
                m_PlayerOutboundLookup,
                m_AALookup,
                m_CooldownLookup,
                m_SimulateLookup,
                m_DeletedLookup,
                m_DestroyedLookup,
                m_IdentifiedTargetLookup,
                m_BuildingLookup,
                m_StorageInfoLookup);

            var result = m_FireControl.Execute(in snap, in lookups, m_Random, nowGameSeconds, EventBus);

            m_Random = result.UpdatedRandom;
            Interlocked.Add(ref s_EcbCommandCount, result.EcbCommands);

            // S004: accumulate, do not write directly. AirDefenseShotStatsFlushSystem flushes
            // after BDS in the same frame, before WaveExecutor reads the debrief. Counted per
            // firing AAType so each type's ammo total falls in step in the UI stats cache.
            if (result.ShotsFired > 0)
            {
                for (int t = 0; t < AirDefenseShotsByType.TypeCount; t++)
                {
                    int typeShots = result.ShotsByType.Get((AAType)t);
                    if (typeShots > 0)
                        AirDefenseShotCounter.AddAAShots((AAType)t, typeShots);
                }
            }

            // R9-M5: Only register when intercept ECB commands were actually produced (not just shots/misses)
            if (result.InterceptCommands > 0)
                m_InterceptBarrier.AddJobHandleForProducer(Dependency);
        }

        // ============================================================================
        // RESET / LOAD RESTORE
        // ============================================================================

        public void ResetState()
        {
            CompletePendingJobsForLoadRestore();
            ResetRuntimeFields(reseedRandom: true);
            Log.Info("State reset (jobs completed, containers cleared)");
        }

        public void ValidateAfterLoad()
        {
            CompletePendingJobsForLoadRestore();
            ResetRuntimeFields(reseedRandom: false);
        }

        private void CompletePendingJobsForLoadRestore()
        {
            m_TargetingDriver.ForceComplete();
        }

        private void ResetRuntimeFields(bool reseedRandom)
        {
            // NOTE: m_StorageInfoLookup is intentionally NOT reset here.
            // EntityStorageInfoLookup captures an EntityComponentStore* and can only be
            // initialized via GetEntityStorageInfoLookup() (OnCreate). Its refresh call
            // merely updates the already-captured store — unlike ComponentLookup<T>.Update,
            // it does NOT rebuild the pointer from a default(EntityStorageInfoLookup) state.
            // The lookup stays valid for the World's lifetime and is refreshed each frame.

            if (!m_TargetingDriver.HasPendingJob)
            {
                m_TargetingDriver.ClearGenerations();
                m_TargetingDriver.ClearScoredCandidates();
            }

            // M1: reset throttle so targeting fires immediately after new game/load
            m_TargetingDriver.ResetThrottle();

            // Counters (static search tree is fetched fresh each pass — nothing to reset)
            AirDefenseShotCounter.Reset();
            Interlocked.Exchange(ref s_EcbCommandCount, 0);

            // Force re-wire of cross-system caches on next OnStartRunning (worlds may rebuild).
            m_DriverWired = false;

            if (reseedRandom)
                m_Random = new SerializableRandom(unchecked(System.Environment.TickCount));
        }
    }
#pragma warning restore CA1001
}
