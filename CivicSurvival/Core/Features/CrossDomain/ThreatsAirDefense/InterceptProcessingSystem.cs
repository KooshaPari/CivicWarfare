using Game;
using Game.Common;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;

using CivicSurvival.Core.Utils;
using System.Threading;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Core.Features.CrossDomain.ThreatsAirDefense
{
    /// <summary>
    /// Single consumer for InterceptRequest entities.
    ///
    /// SOLID: Single Responsibility - only processes intercept requests.
    ///
    /// Responsibilities:
    /// 1. Count intercepts (update InterceptStatsSingleton)
    /// 2. Spawn debris for Shahed intercepts
    /// 3. Destroy request entities
    ///
    /// Producer: AirDefenseSystem / BallisticDefenseSystem
    /// Consumer: This system (ONLY)
    ///
    /// Flow:
    /// 1. AirDefenseSystem creates InterceptRequest via InterceptBarrier
    /// 2. InterceptBarrier.Playback() - request entity appears, IsIntercepted set
    /// 3. InterceptProcessingSystem (this) processes ALL requests in one place
    /// 4. ThreatLifecycleBarrier destroys request entities (bundled with threat
    ///    teardown commands in the same ECB so render-job gating covers both)
    /// </summary>
    [ActIndependent]
    // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5. The InterceptedCount++ is lossy-safe
    // (InterceptStatsSingleton is IEmptySerializable, per-wave). The durable-coupled side-effect —
    // the persisted IsIntercepted marker (BallisticInterceptState / ShahedCombatState) that would
    // otherwise strand a frozen immortal intercepted threat across a save in the in-flight window
    // — is reconciled by ThreatLoadRenderReinitSystem (C1): it runs in ModificationEnd (frame 0,
    // pause-safe) and destroys every restored drone whose persisted marker says intercepted (or
    // whose Shahed/Ballistic says arrived), before this system's GameSimulation hooks run. Stale
    // InterceptRequest entities are purged in PurgeAfterLoad. So losing the in-flight
    // InterceptRequest here is safe by design: the intercepted-zombie-on-load case (a frozen
    // immortal threat whose intercept request was lost across the save) is closed by the reinit
    // pass, not this hook.
    [TransientConsumerReconcile(typeof(InterceptRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Stale InterceptRequest is purged in PurgeAfterLoad; the intercepted-drone teardown the request drives is reconciled by ThreatLoadRenderReinitSystem (ModificationEnd, pause-safe) from the persisted ShahedCombatState/BallisticInterceptState markers (C1).")]
    public partial class InterceptProcessingSystem : CivicSystemBase, IPostLoadValidation, IInitializable, IRequestPurger
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public int InitOrder => CivicSurvival.Core.Interfaces.Core.InitPriority.DEFAULT;

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private static readonly LogContext Log = new("InterceptProcessingSystem");

        private CivicSingletonHandle<InterceptStatsSingleton> m_StatsHandle;
        private ThreatLifecycleBarrier m_ThreatLifecycleBarrier = null!;

        // EntityQuery for InterceptRequest - same pattern as ThreatDamageSystem
        private EntityQuery m_InterceptRequestQuery;
        private EntityQuery m_StatsQuery;

        // C-5: read the SOURCE threat generation so spawned debris carries the
        // threat's loaded-world generation (NOT current-at-intercept) — debris from
        // a pre-load transient must be dropped if it lands after restore/reset.
        private ComponentLookup<Shahed> m_ShahedLookup;
        private ComponentLookup<Ballistic> m_BallisticLookup;
        private ComponentLookup<ShahedCombatState> m_ShahedCombatStateLookup;
        private ComponentLookup<BallisticInterceptState> m_BallisticInterceptStateLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<PendingDestruction> m_PendingDestructionLookup;
        [System.NonSerialized] private IThreatTerminalizationSink m_TerminalizationQueue = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_StatsQuery = GetEntityQuery(ComponentType.ReadWrite<InterceptStatsSingleton>());
            m_StatsHandle = CreateSingletonHandle<InterceptStatsSingleton>(m_StatsQuery);
            CreateStatsSingletonIfMissing();

            m_ThreatLifecycleBarrier = World.GetOrCreateSystemManaged<ThreatLifecycleBarrier>();

            // Query for InterceptRequest entities (same pattern as ThreatDamageSystem.m_ImpactEventQuery)
            m_InterceptRequestQuery = GetEntityQuery(
                ComponentType.ReadWrite<InterceptRequest>()
            );
            m_ShahedLookup = GetComponentLookup<Shahed>(true);
            m_BallisticLookup = GetComponentLookup<Ballistic>(true);
            // PERF-LOCK: these two lookups MUST stay read-only — the leak-floor write is deferred
            // through the ThreatLifecycleBarrier ECB (see MarkLeaked). A main-thread RW access here
            // would complete the read-only ShahedCombatState/BallisticInterceptState jobs in
            // ThreatTargetSystem/ThreatMovementSystem every leak frame (a sync point); a read-only
            // access completes nothing, since no Burst job writes these components. Flipping to
            // GetComponentLookup<...>(false) silently reintroduces the sync.
            // The "no Burst job writes these" clause is load-bearing for the whole mod: the
            // leak-attribution telemetry bits (WasCandidate/WasEngaged) are therefore written
            // MAIN-THREAD-ONLY (AirDefenseOrchestrator.MarkCandidateBits, FireControlExecutor,
            // BallisticDefenseSystem). Introducing any job-writer of ShahedCombatState or
            // BallisticInterceptState turns every RO lookup on these types into a force-complete
            // of that job's chain — do not do it for a telemetry bit.
            m_ShahedCombatStateLookup = GetComponentLookup<ShahedCombatState>(true);
            m_BallisticInterceptStateLookup = GetComponentLookup<BallisticInterceptState>(true);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_PendingDestructionLookup = GetComponentLookup<PendingDestruction>(true);

            // Zero scheduling cost when no intercepts pending (95%+ of game time)
            RequireForUpdate(m_InterceptRequestQuery);

            Log.Info("Created (single consumer pattern, EntityQuery)");
        }

        public void ValidateAfterLoad()
        {
            ReResolveRuntimeRefs();
            // C1: the intercepted-zombie reconcile that used to live here is now subsumed by
            // ThreatLoadRenderReinitSystem. That one-shot runs in ModificationEnd (frame 0,
            // pause-safe) and routes every restored drone whose persisted ShahedCombatState /
            // BallisticInterceptState marks it intercepted (or whose Shahed/Ballistic marks it
            // arrived) straight to DestroyEntity — strictly before this hook, which runs via
            // PostLoadValidationSystem in GameSimulation (frame +2, pause-gated). After C1 the
            // ActiveThreat tag is also stripped on save, so the old WithAll<ActiveThreat> reconcile
            // query would match nothing here anyway. Stale InterceptRequest purge stays in
            // PurgeAfterLoad (a separate transient, untouched by the reinit pass).
        }

        [CompletesDependency("PurgeAfterLoad: one-shot post-load purge of stale InterceptRequest entities; CalculateEntityCount is diagnostic-only, sync amortised against the DestroyEntity that follows")]
        public void PurgeAfterLoad()
        {
            if (m_InterceptRequestQuery.IsEmptyIgnoreFilter)
                return;

            int destroyed = m_InterceptRequestQuery.CalculateEntityCount();
            EntityManager.DestroyEntity(m_InterceptRequestQuery);

            if (destroyed > 0)
                Log.Info($"PurgeAfterLoad: destroyed {destroyed} stale intercept request entities");
        }

        public void OnInitialize()
        {
            CreateStatsSingletonIfMissing();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            CreateStatsSingletonIfMissing();
            ReResolveRuntimeRefs();
        }

        protected override void OnDestroy()
        {
            if (m_StatsHandle.Entity != Entity.Null && EntityManager.Exists(m_StatsHandle.Entity))
            {
                EntityManager.DestroyEntity(m_StatsHandle.Entity);
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Ensure singleton exists — entity world is cleared on load but OnCreate doesn't re-run.
        /// </summary>
        private bool ResolveStatsSingleton()
        {
            // Inv 2 / CIVIC427: liveness-validate + query-first + dedup +
            // create-if-absent, centralized in CivicSystemBase. Self-heals after
            // load without leaving a stale handle or duplicate singleton.
            EnsureSingleton(ref m_StatsHandle, default);
            return m_StatsHandle.Entity != Entity.Null
                && EntityManager.HasComponent<InterceptStatsSingleton>(m_StatsHandle.Entity);
        }

        private void CreateStatsSingletonIfMissing()
        {
            // EnsureSingleton (inside ResolveStatsSingleton) creates it if absent.
            ResolveStatsSingleton();
        }

        private bool TryGetStatsSingleton(out Entity entity)
        {
            entity = m_StatsHandle.Entity;
            if (entity != Entity.Null && SystemAPI.HasComponent<InterceptStatsSingleton>(entity))
                return true;

            return m_StatsQuery.TryGetSingletonEntity<InterceptStatsSingleton>(out entity);
        }

        protected override void OnUpdateImpl()
        {
            if (!TryGetStatsSingleton(out var statsEntity))
            {
                Log.Warn("InterceptStatsSingleton missing; intercept processing deferred");
                return;
            }

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            EntityCommandBuffer EnsureEcb()
            {
                if (!ecbCreated)
                {
                    ecb = m_ThreatLifecycleBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }
                return ecb;
            }

            var stats = SystemAPI.GetComponentRW<InterceptStatsSingleton>(statsEntity);

            // C-5: refresh before SpawnDebris reads the source threat's epoch.
            m_ShahedLookup.Update(this);
            m_BallisticLookup.Update(this);
            m_ShahedCombatStateLookup.Update(this);
            m_BallisticInterceptStateLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_PendingDestructionLookup.Update(this);
            m_TerminalizationQueue ??= ServiceRegistry.Instance.Require<IThreatTerminalizationSink>();

            int processed = 0;
            int deferred = 0;
            int deniedByLeakFloor = 0;

            foreach (var (requestRW, requestEntity) in
                SystemAPI.Query<RefRW<InterceptRequest>>()
                .WithEntityAccess())
            {
                ref var request = ref requestRW.ValueRW;

                // Idempotency: skip already processed requests
                // (SimulationSystemGroup may run multiple ticks per rendering frame)
                if (request.Consumed) continue;

                // Mark as consumed (sync write - visible immediately via RefRW)
                request.Consumed = true;

                // Reconstruct Entity from Index+Version (stored separately to avoid orphan detection)
                var threatEntity = request.GetThreatEntity();
                bool handled = false;

                // Guard: threat entity may already be destroyed (race condition)
#pragma warning disable CIVIC436 // Exists check on threat entity, not owned singleton
                if (SystemAPI.Exists(threatEntity))
#pragma warning restore CIVIC436
                {
                    if (ShouldLeak(threatEntity, request.IsBallistic))
                    {
                        MarkLeaked(EnsureEcb(), threatEntity, request.IsBallistic);
                        stats.ValueRW.LeakedCount++;
                        deniedByLeakFloor++;
                    }
                    else if (IsAwaitingInterceptorImpact(threatEntity, request.IsBallistic))
                    {
                        // Patriot intercept: the threat is decided dead (IsIntercepted set at fire
                        // success) but keeps coasting. DEFER the explosion + render delete to the
                        // resolution triggers (interceptor arrival / interceptor despawn / coast
                        // arrival). Do NOT queue the terminal outcome now — that is the old
                        // freeze-and-delete-now behavior we are splitting off. Count it handled: the
                        // decision stands, the visual is owed.
                        handled = true;
                        deferred++;
                    }
                    else if (!request.IsBallistic)
                    {
                        // Gun intercept (Bofors/Gepard/Heritage): no missile, freeze + explode now.
                        handled = TryQueueTerminalOutcome(threatEntity, request.Position, isBallistic: false);
                    }
                    else
                    {
                        handled = TryQueueTerminalOutcome(threatEntity, request.Position, isBallistic: true);
                    }
                }
                else
                {
                    Log.Warn($"ThreatEntity {request.ThreatEntityIndex} already destroyed, skipping cleanup");
                }

                if (handled)
                {
                    processed++;
                    // Count the intercept at DECISION time (here), NOT at terminalization. For deferred
                    // Patriot intercepts terminalization lands ~missile-flight later and could cross a
                    // ResetForNewWave or be lost on a mid-coast save (the threat is purged on load by
                    // IsIntercepted without ever terminalizing). Counting here makes the stat wave-accurate
                    // and save-stable. Covers gun (immediate) and Patriot (deferred) — both reach here.
                    stats.ValueRW.InterceptedCount++;
                    // Drone/ballistic split for balance telemetry (balance.wave_result). request.IsBallistic
                    // is the same flag the kill decision above already branched on — booking the slice here
                    // adds a counter, not a gameplay branch.
                    if (request.IsBallistic)
                        stats.ValueRW.InterceptedBallisticCount++;
                    else
                        stats.ValueRW.InterceptedShahedCount++;
                }

                // Destroy request entity - processing complete
                EnsureEcb().DestroyEntity(requestEntity);
                IncrementEcbCount();
            }

            if (processed > 0)
            {
                // Patriot intercepts are DEFERRED here — terminalized later by InterceptorMovementSystem
                // when the missile arrives — NOT queued now. Only gun intercepts queue immediately.
                Log.Info($"Intercepts handled: {processed - deferred} terminalized now (gun), {deferred} deferred to interceptor arrival");
            }
            if (deniedByLeakFloor > 0 && Log.IsDebugEnabled)
            {
                Log.Debug($"Denied {deniedByLeakFloor} intercept(s) by wave leak floor");
            }

            if (ecbCreated)
                m_ThreatLifecycleBarrier.AddJobHandleForProducer(Dependency);
        }

        private bool ShouldLeak(Entity threatEntity, bool isBallistic)
        {
            return LeakFloorLogic.Leaks(GetTargetBuildingIndex(threatEntity, isBallistic), BalanceConfig.Current.Waves.MaxWaveInterceptFraction);
        }

        private int GetTargetBuildingIndex(Entity threatEntity, bool isBallistic)
        {
            if (isBallistic)
                return m_BallisticLookup.TryGetComponent(threatEntity, out var ballistic) ? ballistic.TargetBuilding.Index : 0;
            return m_ShahedLookup.TryGetComponent(threatEntity, out var shahed) ? shahed.TargetBuilding.Index : 0;
        }

        /// <summary>
        /// True if the threat is a Patriot-intercepted, still-coasting one (AwaitingInterceptorImpact).
        /// Such accepts defer the terminal outcome to the interceptor's arrival rather than queuing it
        /// now. RO reads off the already-RO, already-Updated lookups — PERF-LOCK at OnCreate preserved.
        /// </summary>
        private bool IsAwaitingInterceptorImpact(Entity threatEntity, bool isBallistic)
        {
            if (isBallistic)
                return m_BallisticInterceptStateLookup.TryGetComponent(threatEntity, out var bis)
                    && bis.AwaitingInterceptorImpact;
            return m_ShahedCombatStateLookup.TryGetComponent(threatEntity, out var cs)
                && cs.AwaitingInterceptorImpact;
        }

        /// <summary>
        /// Mark a threat as leaked by the wave floor: clear IsIntercepted so it impacts
        /// normally, and set IsLeaked so air defense stops re-targeting it (otherwise the
        /// rolled-back marker re-enters the targeting pool and AA drains ammo on a drone
        /// it is not permitted to kill).
        ///
        /// The write is deferred through the ThreatLifecycleBarrier ECB so the lookups stay
        /// read-only on the main thread (no sync point — see OnCreate). The one-frame latency
        /// is harmless: IsIntercepted=true already blocks targeting until IsLeaked takes over,
        /// so there is no re-targeting window, and the drone (already frozen by TMS while
        /// intercepted) simply resumes one tick later.
        /// </summary>
        private void MarkLeaked(EntityCommandBuffer ecb, Entity threatEntity, bool isBallistic)
        {
            if (isBallistic)
            {
                if (m_BallisticInterceptStateLookup.TryGetComponent(threatEntity, out var interceptState))
                {
                    interceptState.IsIntercepted = false;
                    interceptState.IsLeaked = true;
                    // Clear coast: a leaked threat is no longer neutralized, it resumes a normal
                    // damaging flight. Leaving this set would coast it forever without dealing damage.
                    interceptState.AwaitingInterceptorImpact = false;
                    ecb.SetComponent(threatEntity, interceptState);
                    IncrementEcbCount();
                }
                return;
            }

            if (m_ShahedCombatStateLookup.TryGetComponent(threatEntity, out var combatState))
            {
                combatState.IsIntercepted = false;
                combatState.IsLeaked = true;
                // Clear coast: see ballistic branch — a leaked drone resumes normal damaging flight.
                combatState.AwaitingInterceptorImpact = false;
                ecb.SetComponent(threatEntity, combatState);
                IncrementEcbCount();
            }
        }

        private void ReResolveRuntimeRefs()
        {
            if (ServiceRegistry.IsInitialized)
                m_TerminalizationQueue = ServiceRegistry.Instance.Require<IThreatTerminalizationSink>();
        }

        private bool TryQueueTerminalOutcome(Entity threatEntity, float3 position, bool isBallistic)
        {
            if (m_DeletedLookup.HasComponent(threatEntity)
                || (m_PendingDestructionLookup.HasComponent(threatEntity) && m_PendingDestructionLookup.IsComponentEnabled(threatEntity)))
                return false;

            int sourceGeneration = ThreatGenerationClock.Unstamped;
            if (!isBallistic && m_ShahedLookup.TryGetComponent(threatEntity, out var srcShahed))
                sourceGeneration = srcShahed.ThreatGeneration;
            else if (isBallistic && m_BallisticLookup.TryGetComponent(threatEntity, out var srcBallistic))
                sourceGeneration = srcBallistic.ThreatGeneration;

            m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
            {
                Entity = threatEntity,
                Kind = isBallistic ? ThreatTerminalOutcomeKind.BallisticIntercepted : ThreatTerminalOutcomeKind.ShahedIntercepted,
                Position = position,
                EventPosition = position,
                IsBallistic = isBallistic,
                DebrisFallTime = isBallistic ? 0f : BalanceConfig.Current.Threats.DebrisFallTime,
                ThreatGeneration = sourceGeneration
            });
            return true;
        }

        /// <summary>
        /// Reset stats for new wave. Called by WaveExecutor on wave start.
        /// </summary>
#pragma warning disable CIVIC231 // Called by WaveExecutor — caller checks act
        public void ResetForNewWave(int waveNumber)
        {
#pragma warning restore CIVIC231
            if (!ResolveStatsSingleton())
            {
                Log.Warn($"Stats reset for wave #{waveNumber} skipped: InterceptStatsSingleton missing");
                return;
            }

            var stats = new InterceptStatsSingleton
            {
                InterceptedCount = 0,
                InterceptedShahedCount = 0,
                InterceptedBallisticCount = 0,
                LeakedCount = 0
            };
            EntityManager.SetComponentData(m_StatsHandle.Entity, stats);

            if (Log.IsDebugEnabled) Log.Debug($"Stats reset for wave #{waveNumber}");
        }
    }
}
