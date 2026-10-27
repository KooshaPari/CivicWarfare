using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.PsyImpact;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Cognitive.Threats.Systems;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Owner of the incremental sidecar bulk-create path (split out of MentalHealthResolverSystem):
    /// detects new housed households, creates HouseholdPsyState/PsyHouseholdLink/PsySlot sidecar
    /// entities, fills them via Burst job and tags the household with HasPsyState — all in one update.
    ///
    /// WHY A SEPARATE SYSTEM (write-fence split): the fill job needs RW ComponentLookup
    /// registrations of PsyHouseholdLink + HouseholdPsyState. A system's final Dependency is
    /// stamped as the write-fence of EVERY statically registered RW type on EVERY update
    /// (ComponentDependencyManager.AddDependency replaces WriteFence per registered writer type),
    /// whether or not this tick's jobs touched the type. Registered inside MHR, that stamped MHR's
    /// heavy resolve chain (BuildMentalHealthLookupsJob → ResolveHouseholdPsyJob, ~43k households)
    /// into the PsyHouseholdLink fence each tick, and every link reader (PsyOrphanScanSystem,
    /// measured 22.9ms/report DepDrain) drained that wall. Here the registrations live in a system
    /// whose no-op ticks exit with Dependency = default, so the link fence only ever carries
    /// completed fill handles.
    ///
    /// Ordering: registered AFTER MentalHealthResolverSystem (registration-site API, Axiom 7) so
    /// the chain PILS &lt; MHR &lt; this system keeps sidecar creation deterministically after the
    /// PILS post-load Phase 1 window within a frame (PILS is registered before MHR).
    /// </summary>
#pragma warning disable CIVIC076 // Ticks every frame by design: cheap query/timer gate; real work ~1 per BULK_CREATE interval
    [ActIndependent]
    public partial class PsySidecarCreateSystem : CivicSystemBase, IResettable, IPostLoadValidation
#pragma warning restore CIVIC076
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PsySidecarCreateSystem");

        // PsySlot round-robin: must match the 4-slot layout MHR's fire cycle walks (PsySlot doc).
        private const int SLOT_COUNT = 4;
        private const int SLOT_MASK = SLOT_COUNT - 1; // 0x3

        // CreateEntity cost scales with entity count: 5ms@16 → 44ms@256.
        // Smaller batches = smaller spikes, more iterations to drain backlog.
        private const int BULK_CAP_SMALL = 8;
        private const int BULK_CAP_MEDIUM = 32;
        private const int BULK_CAP_LARGE = 64;
        // Real seconds (see PERF-LOCK below), not sim seconds.
        private const float BULK_CREATE_IDLE_SECONDS = 5f;
        private const float BULK_CREATE_DRAIN_SECONDS = 1f;

        // New housed households without a sidecar yet.
        private EntityQuery m_UntaggedHouseholdQuery;
        // Keyed on the LINK (identity component) — pre-split restored sidecars without a
        // link are excluded by design (swept by the MECS ValidateAfterLoad migration).
        private EntityQuery m_ModEntityQuery;
        // PERF: Skip BulkCreate during combat — EntityManager.CreateEntity costs 20-36ms on 177K entity world
        private EntityQuery m_ActiveThreatQuery;
        private EntityArchetype m_ModArchetype;
#pragma warning disable CIVIC269 // Write via IJobParallelFor, not direct indexer
        private ComponentLookup<HouseholdPsyState> m_PsyStateLookup;
        private ComponentLookup<PsyHouseholdLink> m_LinkLookup;
#pragma warning restore CIVIC269
#pragma warning disable CIVIC168 // Throttle stamp, not persisted — -1 sentinel re-arms on load (first fire after 5s real)
        // PERF-LOCK: bulk-create throttle runs on REAL time, not sim dt. Each fire pays two
        // structural sync points (CreateEntity + AddComponent) whose cost is real frame time;
        // a sim-dt timer contracts the interval by the game-speed multiplier (5s → ~1s at 3×),
        // which produced a structural sync every real second all session (PERF.log 2026-07-14).
        private double m_LastBulkCreateRealTime = -1d;
        private float m_BulkCreateInterval = BULK_CREATE_IDLE_SECONDS;
#pragma warning restore CIVIC168
        // PsySlot assignment counter for incremental bulk creates (wraps at SLOT_COUNT)
        // Not serialized — resets to 0 on load, produces even distribution regardless of start
        [System.NonSerialized] private int m_SlotAssignCounter;

        // Same-domain (Cognitive) reference: PILS owns the post-load re-tag window; the
        // restored-state reconcile is only needed while that window is open (BulkInitDone == false).
        // Resolved lazily through FeatureRegistry (CIVIC400/403 — never GetOrCreate a gated system,
        // never resolve the registry in OnCreate); null (feature registration in flight) is treated
        // as "window open" so the reconcile fail-safes toward running.
        private PsyImpactLifecycleSystem? m_PsyImpactLifecycle;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_UntaggedHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<HasPsyState>(),
                ComponentType.Exclude<Deleted>()
            );
            m_ModEntityQuery = GetEntityQuery(
                ComponentType.ReadOnly<PsyHouseholdLink>(),
                ComponentType.Exclude<Deleted>()
            );
            m_ActiveThreatQuery = GetEntityQuery(
                ComponentType.ReadOnly<ActiveThreat>(),
                ComponentType.Exclude<Deleted>()
            );
            m_ModArchetype = EntityManager.CreateArchetype(typeof(HouseholdPsyState), typeof(PsyHouseholdLink), typeof(PsySlot));
            m_PsyStateLookup = GetComponentLookup<HouseholdPsyState>(false);
            m_LinkLookup = GetComponentLookup<PsyHouseholdLink>(false);

            Log.Info("Created (sidecar bulk-create, write-fence split from MentalHealthResolverSystem)");
        }

        [CompletesDependency("PsySidecarCreateSystem.OnUpdateImpl: throttled bulk-create path (timer-gated, ~64 entities cap) materialises NativeArray<Entity> for Burst IJobParallelFor — no SystemAPI.Query equivalent. Not a [HotPathSystem]; real work fires at most once per BULK_CREATE interval")]
        protected override void OnUpdateImpl()
        {
            // Real-time stamp for the bulk-create throttle (see PERF-LOCK on the field).
            // -1 sentinel arms on the first tick of a session/city: first fire after one full interval.
            double nowReal = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (m_LastBulkCreateRealTime < 0d)
                m_LastBulkCreateRealTime = nowReal;

            // PERF: Skip BulkCreate during active combat — EntityManager.CreateEntity = 20-36ms on 177K world.
            // New households can wait until threats are gone. The real-time stamp is untouched during
            // combat, so the first post-combat fire triggers immediately.
            bool hasCombat = !m_ActiveThreatQuery.IsEmpty;
            if (hasCombat || nowReal - m_LastBulkCreateRealTime < m_BulkCreateInterval || m_UntaggedHouseholdQuery.IsEmpty)
            {
                // SAFETY-LOCK: every no-op exit of this system MUST leave Dependency = default.
                // BeforeOnUpdate completes the previous final handle but does NOT zero it; without
                // this reset AfterOnUpdate would stamp that stale COMPLETED handle as the write-fence
                // of BOTH registered RW types (AddDependency REPLACES WriteFence), clobbering the
                // LIVE HouseholdPsyState fence MHR stamped this frame — readers would stop waiting
                // for the in-flight resolve job (data race), and the PsyHouseholdLink fence would be
                // re-poisoned with the input chain. Dependency = default makes AfterOnUpdate skip
                // the stamp entirely (SystemState.AfterOnUpdate checks against default), leaving the
                // manager's fences untouched. Legal here because this path schedules nothing.
                // CIVIC184 false positive: no handle is lost — nothing was scheduled on this path
                // and the previous tick's handle was completed by BeforeOnUpdate.
#pragma warning disable CIVIC184
                Dependency = default;
#pragma warning restore CIVIC184
                return;
            }

            m_LastBulkCreateRealTime = nowReal;
            using (PerformanceProfiler.Measure("SP:MH.BulkCreate"))
            {
                // PERF-LOCK: reconcile only while the PILS Phase 1 post-load window is open.
                // The reconcile does ToComponentDataArray over ALL sidecar links (~170k on a
                // large city, ~25ms + a sync on the resolve jobs) — after Phase 1 completes, no
                // untagged household can match a restored key, so an unconditional call burns
                // that scan on every fire for nothing (14.7s over one session, PERF.log 2026-07-14).
                m_PsyImpactLifecycle ??= FeatureRegistry.Instance.Query<PsyImpactLifecycleSystem>();
                if (m_PsyImpactLifecycle == null || !m_PsyImpactLifecycle.BulkInitDone)
                    ReconcileLoadedHouseholdTags();
                if (m_UntaggedHouseholdQuery.IsEmpty)
                {
                    m_BulkCreateInterval = BULK_CREATE_IDLE_SECONDS;
                }

                NativeArray<Entity> allHouseholds;
                using (PerformanceProfiler.Measure("MH.BulkCreate.ToEntityArray"))
                {
                    allHouseholds = m_UntaggedHouseholdQuery.ToEntityArray(Allocator.TempJob);
                }
                int totalNew = allHouseholds.Length;
                int cap;
                if (totalNew > 128) cap = BULK_CAP_LARGE;
                else if (totalNew > 16) cap = BULK_CAP_MEDIUM;
                else cap = BULK_CAP_SMALL;
                int processCount = totalNew > cap ? cap : totalNew;

                if (processCount > 0)
                {
                    NativeArray<Entity> modEntities;
                    using (PerformanceProfiler.Measure("MH.BulkCreate.CreateEntity"))
                    {
#pragma warning disable CIVIC006 // Bulk create — ECB impossible, need NativeArray for Burst fill
                        modEntities = EntityManager.CreateEntity(m_ModArchetype, processCount, Allocator.TempJob);
#pragma warning restore CIVIC006
                    }

                    // Assign PsySlot round-robin (structural, BEFORE fill job)
                    // Skip slot 0 — default SharedComponent value, SetSharedComponent asserts on same-chunk move
                    for (int i = 0; i < processCount; i++)
                    {
                        int slot = (m_SlotAssignCounter + i) & SLOT_MASK;
                        if (slot != 0)
                            EntityManager.SetSharedComponent(modEntities[i], new PsySlot { SlotIndex = slot });
                    }
                    m_SlotAssignCounter = (m_SlotAssignCounter + processCount) & SLOT_MASK;

                    // Copy batch for tagging (fill job holds allHouseholds — avoid aliasing)
                    var batchToTag = new NativeArray<Entity>(processCount, Allocator.TempJob);
                    try
                    {
                        NativeArray<Entity>.Copy(allHouseholds, batchToTag, processCount);

                        // Fill PsyHouseholdLink + psy defaults via Burst job — reads allHouseholds[0..processCount-1]
                        // Structural change below completes Dependency implicitly
#pragma warning disable CIVIC289 // Intentional: Update after EntityManager.CreateEntity (structural change invalidates lookup pointers)
                        m_PsyStateLookup.Update(this);
                        m_LinkLookup.Update(this);
#pragma warning restore CIVIC289
                        var fillJob = new FillPsyModEntitiesJob
                        {
                            ModEntities = modEntities,
                            Households = allHouseholds,
                            PsyStateLookup = m_PsyStateLookup,
                            LinkLookup = m_LinkLookup
                        };
                        if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                            CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre FillPsyModEntitiesJob.Schedule processCount={processCount} modEntities={modEntities.IsCreated}/{modEntities.Length} households={allHouseholds.IsCreated}/{allHouseholds.Length} batchToTag={batchToTag.IsCreated}/{batchToTag.Length}");
                        Dependency = fillJob.Schedule(processCount, 64, Dependency);
                        if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                            CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post FillPsyModEntitiesJob.Schedule processCount={processCount} modEntities={modEntities.IsCreated}/{modEntities.Length} households={allHouseholds.IsCreated}/{allHouseholds.Length} batchToTag={batchToTag.IsCreated}/{batchToTag.Length}");
                        if (modEntities.IsCreated) Dependency = modEntities.Dispose(Dependency);
                        if (allHouseholds.IsCreated) Dependency = allHouseholds.Dispose(Dependency);

                        // Tag only processed batch — structural change completes Dependency (fill + dispose)
                        // PERF-LOCK: this structural AddComponent forces FillPsyModEntitiesJob to
                        // complete before OnUpdate exits — no sidecar ever leaves this frame with the
                        // default 0:0 PsyHouseholdLink, which the orphan scan reads as "dead" and
                        // deletes. Making the fill async past this point would let the scan destroy
                        // every fresh sidecar. (CompleteAllJobs completes the executing system's live
                        // m_JobHandle — decompile-verified, ComponentDependencyManager.cs:126-142.)
                        using (PerformanceProfiler.Measure("MH.BulkCreate.AddTag"))
                        {
#pragma warning disable CIVIC006 // Bulk tag on vanilla entities — same pattern as PowerCapacityModifiers
                            EntityManager.AddComponent<HasPsyState>(batchToTag);
#pragma warning restore CIVIC006
                        }
                    }
                    finally
                    {
                        if (batchToTag.IsCreated) batchToTag.Dispose();
                    }

                    // Dynamic interval: drain when backlog exceeds one future batch
                    int backlog = totalNew - processCount;
                    m_BulkCreateInterval = backlog > BULK_CAP_SMALL ? BULK_CREATE_DRAIN_SECONDS : BULK_CREATE_IDLE_SECONDS;

                    Log.Info($"[LAZY INIT] Created {processCount} PsyState (cap: {cap}, backlog: {backlog})");
                }
                else
                {
                    if (allHouseholds.IsCreated) allHouseholds.Dispose();
                }
            }

            // SAFETY-LOCK: same invariant as the no-op exit above. Everything this system schedules
            // is force-completed by the structural AddComponent before this line (fill + deferred
            // disposes; on the processCount == 0 leg nothing was scheduled at all), so skipping the
            // AfterOnUpdate stamp is safe — and stamping would overwrite the live HouseholdPsyState
            // fence MHR wrote this frame with an already-completed handle. If an async job is ever
            // added past the AddComponent, this line must be revisited.
            // CIVIC184 false positive: no handle is lost — the fill + deferred disposes were
            // force-completed by the structural AddComponent (CompleteAllJobs completes the
            // executing system's live m_JobHandle) before this assignment.
#pragma warning disable CIVIC184
            Dependency = default;
#pragma warning restore CIVIC184
        }

        [CompletesDependency("ReconcileLoadedHouseholdTags: lazy-create pre-pass materialises restored HouseholdPsyState keys and untagged households to avoid duplicate mod-entities while the PILS Phase 1 post-load window is open")]
        private void ReconcileLoadedHouseholdTags()
        {
            if (m_ModEntityQuery.IsEmpty || m_UntaggedHouseholdQuery.IsEmpty)
                return;

            var households = m_UntaggedHouseholdQuery.ToEntityArray(Allocator.Temp);
            var links = m_ModEntityQuery.ToComponentDataArray<PsyHouseholdLink>(Allocator.Temp);
            var existingKeys = new NativeParallelHashSet<long>(links.Length, Allocator.Temp);
            var existingToTag = new NativeList<Entity>(households.Length, Allocator.Temp);

            try
            {
                for (int i = 0; i < links.Length; i++)
                {
                    if (HouseholdPsyIdentity.IsValidHouseholdKey(links[i].HouseholdIndex, links[i].HouseholdVersion))
                        existingKeys.Add(HouseholdPsyIdentity.MakeHouseholdKey(links[i].HouseholdIndex, links[i].HouseholdVersion));
                }

                for (int i = 0; i < households.Length; i++)
                {
                    var household = households[i];
                    if (HouseholdPsyIdentity.IsValidHouseholdKey(household.Index, household.Version)
                        && existingKeys.Contains(HouseholdPsyIdentity.MakeHouseholdKey(household.Index, household.Version)))
                        existingToTag.Add(household);
                }

                if (existingToTag.Length > 0)
                {
#pragma warning disable CIVIC006 // Load-time bulk re-tag mirrors PILS reconcile; prevents duplicate lazy-create.
                    EntityManager.AddComponent<HasPsyState>(existingToTag.AsArray());
#pragma warning restore CIVIC006
                    Log.Info($"[LAZY INIT] Re-tagged {existingToTag.Length} restored PsyState households before create");
                }
            }
            finally
            {
                if (existingToTag.IsCreated) existingToTag.Dispose();
                if (existingKeys.IsCreated) existingKeys.Dispose();
                if (links.IsCreated) links.Dispose();
                if (households.IsCreated) households.Dispose();
            }
        }

        /// <summary>
        /// Load-boundary reset: re-arm the real-time throttle and slot counter. No native
        /// containers and no persisted state — this is the whole reset (m_SlotAssignCounter is
        /// semantically safe to restart at 0: round-robin position only needs balance).
        /// </summary>
        private void ClearThrottleState()
        {
            m_LastBulkCreateRealTime = -1d;  // re-arm the real-time stamp: first fire after one full interval
            m_BulkCreateInterval = BULK_CREATE_IDLE_SECONDS;
            m_SlotAssignCounter = 0;         // even distribution regardless of prior city (its own contract)
        }

        void IResettable.ResetState() => ClearThrottleState();

        public void ValidateAfterLoad() => ClearThrottleState();
    }
}
