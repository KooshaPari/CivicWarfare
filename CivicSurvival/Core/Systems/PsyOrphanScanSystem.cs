using Game;
using Colossal.Serialization.Entities;
using Game.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Components.PsyImpact;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// Orphan liveness scan for psy sidecars — one sidecar per household (~170k on a large
    /// city, permanently non-empty), split out of <see cref="ModEntityCleanupSystem"/> so the
    /// scan's same-frame fence stops draining that system's much wider Dependency. Inside MECS
    /// the Complete() waited on every writer of every declared sidecar type (CivilianWarDamage
    /// combat jobs each tick, EquipmentWear, PowerPlantDamage, SpotterData, …) plus vanilla
    /// Destroyed writers: 2900ms/session, 71% of MECS, 32ms combat spikes (PERF.log 2026-07-21).
    ///
    /// PERF-LOCK: declarations are strictly three — the PsyHouseholdLink query plus the
    /// Deleted / EntityStorageInfoLookup lookups. That set IS the point of the split: this
    /// system's Dependency then carries only writers of those types (the link is written once
    /// at sidecar creation; Deleted is a structural tag with no per-frame job writers), and
    /// the same-frame Complete() waits on almost nothing. Destroyed is deliberately NOT
    /// declared: a household can never carry it (every Destroy-event producer targets
    /// physical objects; households die via Deleted — HouseholdBehaviorSystem/
    /// HouseholdMoveAwaySystem), while the ruin pipeline (CollapsedBuildingSystem, fire/
    /// maintenance AI) writes Destroyed every combat frame — declaring it pulled those
    /// chains into Dependency and cost 20-52ms fence spikes in combat (PERF.log
    /// 2026-07-21). Any new query or lookup here re-couples live job chains into
    /// Dependency and resurrects the fence.
    ///
    /// Crash provenance for the same-frame Complete: a native <c>0xC0000005</c> AV
    /// (dump c79a5ce5) proved that a cleanup job left in flight ACROSS frames dereferenced a
    /// NULL chunk pointer — the chunk set was invalidated by a load-boundary structural change
    /// while the async job still held per-chunk iterators. With schedule + Complete in the same
    /// call, no job ever survives past OnUpdate, so no load can catch a stale chunk iterator.
    /// Do not convert the scan to a frame N → N+1 async harvest.
    /// </summary>
    [ActIndependent]
    public partial class PsyOrphanScanSystem : CivicSystemBase, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PsyOrphanScanSystem");
        // Effective cadence of the former MECS batch 0 (0.25s throttle × 1-of-4 rotation).
        private const float THROTTLE_INTERVAL = 1.0f;
        public int HydrationOrder => HydrationPriority.CLEANUP_FIRST;

        private EntityQuery m_PsyStateQuery;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ModCleanupBarrier m_ModCleanupBarrier = null!;
        private float m_ThrottleTimer;
        // Standalone EntityManager query (NOT GetEntityQuery): referencing HouseholdPsyState
        // through a system query would pull the psy resolver chains into this system's
        // Dependency (see the class-doc PERF-LOCK). Used only at the load boundary in
        // ValidateAfterLoad, where all jobs are quiesced.
        private EntityQuery m_PreSplitLinklessQuery;

        /// <summary>
        /// Worker-thread liveness scan over all psy sidecars via their identity link.
        /// Scheduled and completed within the same OnUpdate — see the class doc for why the
        /// job must never stay in flight across frames (dump c79a5ce5).
        ///
        /// PERF-LOCK: this job reads <see cref="PsyHouseholdLink"/>, NOT HouseholdPsyState.
        /// The link is written once at sidecar creation; HouseholdPsyState is written every
        /// frame by the psy resolver chains (ResolveHouseholdPsyJob / ResolveWellbeingJob).
        /// Keying the scan on the link keeps those resolvers OUT of this system's Dependency.
        /// Liveness uses gone-semantics (<see cref="TargetLiveness.IsGoneTarget"/>): a household
        /// can never carry vanilla Destroyed (see the class-doc PERF-LOCK), so checking it
        /// bought nothing and cost the combat ruin-pipeline wait.
        /// No dedup guard on the queue: IJobEntity runs Execute once per entity, and between
        /// passes ModCleanupBarrier has already played Deleted back, so Exclude&lt;Deleted&gt;
        /// filters previously queued sidecars.
        /// </summary>
        #if ENABLE_BURST
        [BurstCompile]
        #endif
        private partial struct PsyStateOrphanScanJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<Deleted> DeletedLookup;
            [ReadOnly] public EntityStorageInfoLookup StorageInfoLookup;
            public NativeQueue<Entity>.ParallelWriter Dead;

            public void Execute(Entity entity, in PsyHouseholdLink link)
            {
                // A 0:0 ref is an invalid household (never filled) and is queued for Deleted.
                bool dead = (link.HouseholdIndex == 0 && link.HouseholdVersion == 0)
                    || TargetLiveness.IsGoneTarget(
                        new Entity { Index = link.HouseholdIndex, Version = link.HouseholdVersion },
                        StorageInfoLookup, DeletedLookup);
                if (dead)
                    Dead.Enqueue(entity);
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            // Keyed on PsyHouseholdLink, NOT HouseholdPsyState — see the class-doc PERF-LOCK.
            m_PsyStateQuery = GetEntityQuery(ComponentType.ReadOnly<PsyHouseholdLink>(), ComponentType.Exclude<Deleted>());

            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_ModCleanupBarrier = World.GetOrCreateSystemManaged<ModCleanupBarrier>();

            // Pre-split migration: sidecars restored from a save written before the
            // PsyHouseholdLink split carry HouseholdPsyState but no link — invisible to the
            // link-keyed scan and reconciles. Swept once per load in ValidateAfterLoad.
            m_PreSplitLinklessQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<HouseholdPsyState>() },
                None = new[] { ComponentType.ReadOnly<PsyHouseholdLink>(), ComponentType.ReadOnly<Deleted>() }
            });

            Log.Info("Created — psy orphan scan (split from ModEntityCleanupSystem)");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (purpose != Purpose.LoadGame && purpose != Purpose.NewGame)
                return;

            int count = m_PsyStateQuery.CalculateEntityCount();
            if (count > 0)
            {
                EntityManager.DestroyEntity(m_PsyStateQuery);
                Log.Info($"OnGamePreload({purpose}): cleared {count} psy sidecars at load-boundary");
            }
        }

        [CompletesDependency("PsyOrphanScanSystem.OnUpdateImpl: PsyStateOrphanScanJob is scheduled and completed within the same call by design — the same-frame Complete is the crash guard against load-boundary chunk invalidation (dump c79a5ce5). This system declares only PsyHouseholdLink (written once at creation) plus the Deleted/storage lookups — Destroyed is deliberately absent (unreachable for households, and its combat ruin-pipeline writers cost 20-52ms fence spikes) — so the wait covers almost nothing. Isolating this fence from ModEntityCleanupSystem's wide Dependency is why the system exists. Throttled (1.0s), not a per-frame block")]
        protected override void OnUpdateImpl()
        {
            m_ThrottleTimer += SystemAPI.Time.DeltaTime;
            if (m_ThrottleTimer < THROTTLE_INTERVAL) return;
            m_ThrottleTimer = 0f;

            // Early-out: IsEmpty reads cached chunk count (no sync point, no alloc).
            if (m_PsyStateQuery.IsEmpty)
                return;

            m_DeletedLookup.Update(this);
            m_StorageInfoLookup.Update(this);

            var deadPsy = new NativeQueue<Entity>(Allocator.TempJob);
            try
            {
                // CIVIC112 false positive here: the same-frame Complete IS the design
                // (see the class-doc PERF-LOCK) — it is the crash guard against a load-time
                // structural change catching the scan in flight (dump c79a5ce5), and the
                // three-declaration split already removed the Dependency-drain cost the rule
                // exists to catch. The async frame-N/N+1 shape is forbidden for this job.
                // (Split probes DepDrain/JobRun removed 2026-07-22 after the green verdict:
                // the PsySidecarCreateSystem write-fence split brought the fence drain to
                // 0.0ms across a full combat session — see PSY_LINK_FILL_SPLIT_PLAN.md in
                // Docs/Plans/Completed. The whole scan stays visible in PERF.log through the
                // base-class PsyOrphanScanSystem.OnUpdate measure.)
#pragma warning disable CIVIC112
                JobHandle scanHandle = new PsyStateOrphanScanJob
                {
                    DeletedLookup = m_DeletedLookup,
                    StorageInfoLookup = m_StorageInfoLookup,
                    Dead = deadPsy.AsParallelWriter()
                }.ScheduleParallel(m_PsyStateQuery, Dependency);
                scanHandle.Complete();
#pragma warning restore CIVIC112

                EntityCommandBuffer ecb = default;
                bool ecbCreated = false;
                while (deadPsy.TryDequeue(out Entity deadEntity))
                {
                    if (!ecbCreated)
                    {
                        ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                        ecbCreated = true;
                    }
                    ecb.AddComponent<Deleted>(deadEntity);
                }

                // ECB is filled synchronously on the main thread and the scan job is already
                // completed — no producer job is ever in flight here; registering the current
                // dependency keeps the barrier contract intact.
                if (ecbCreated)
                    m_ModCleanupBarrier.AddJobHandleForProducer(Dependency);
            }
            finally
            {
                deadPsy.Dispose();
            }
        }

        public void ValidateAfterLoad()
        {
            m_ThrottleTimer = THROTTLE_INTERVAL;

            // Pre-split save migration: delete sidecars restored without PsyHouseholdLink —
            // the link-keyed scan and reconciles cannot see them, so without this sweep they
            // would leak invisibly. PILS Phase 1 then recreates fresh linked sidecars for all
            // households (psy values reset — accepted pre-release). Load boundary: all jobs
            // are quiesced, EntityManager batch op is safe here.
            if (!m_PreSplitLinklessQuery.IsEmptyIgnoreFilter)
            {
                int linkless = m_PreSplitLinklessQuery.CalculateEntityCount();
                EntityManager.AddComponent<Deleted>(m_PreSplitLinklessQuery);
                Log.Info($"ValidateAfterLoad: queued {linkless} pre-split (linkless) psy sidecars for deletion — PILS will recreate them with links");
            }
        }

        protected override void OnDestroy()
        {
            // Standalone EntityManager query — not owned by the system, dispose manually.
            if (m_PreSplitLinklessQuery != default)
                m_PreSplitLinklessQuery.Dispose();
            base.OnDestroy();
        }
    }
}
