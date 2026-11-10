using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Cognitive;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Projects the vanilla telecom-tower set as broadcast-coverage circles (world X/Z +
    /// signal range in meters) for the radar mental-view "broadcast umbrella" overlay. Single
    /// owner/registrar of <see cref="IBroadcastCoverageReader"/> — modelled on
    /// <c>LiveAACacheSystem</c>/<c>CognitiveCoverageCacheSystem</c>. Reads ONLY vanilla components
    /// (<c>TelecomFacility</c> + <c>Transform</c> + the prefab <see cref="TelecomFacilityData"/>
    /// range), writes nothing (CIVIC175 clean). Not serialized — rebuilt on the first post-load
    /// tick. Telecom coverage is near-static (the vanilla cell map refreshes every 4096 ticks), so
    /// a 1 s throttle is ample; the base <c>m_Range</c> is used (efficiency/upgrade scaling is a
    /// later refinement, not needed for the overlay footprint).
    /// </summary>
    [ActIndependent]
    public sealed partial class TelecomBroadcastCacheSystem : ThrottledSystemBase, IBroadcastCoverageReader, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("TelecomBroadcastCacheSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        private ComponentLookup<TelecomFacilityData> m_PrefabTelecomLookup;

        // Gate query: telecom-facility type only — used solely for its structural order
        // version, never iterated. Mirrors LiveAACacheSystem.m_aaGateQuery.
        private EntityQuery m_TelecomGateQuery;

        // Order-version cursor. CS2 reuses system instances across load, so [NonSerialized]
        // survives — the explicit reset to 0 in ValidateAfterLoad makes the first post-load
        // rebuild deterministic (0 never equals a real version).
        [System.NonSerialized]
        [EntityQueryOrderCursor("Invalidates m_coverage when the telecom-tower set changes structurally (placement/demolition/load-boundary).")]
        private uint m_LastTelecomOrderVersion;

        // Type handle for the tower's Transform chunk change-version (metadata only, no data
        // read / no completion) — the persistent in-place relocation signal, exactly the
        // LiveAACacheSystem pattern (the transient vanilla Updated tag is invisible to
        // GameSimulation-phase systems).
        private ComponentTypeHandle<Transform> m_transformHandle;
        private EntityStorageInfoLookup m_storageInfoLookup;

        // Managed coverage projection, rebuilt in place each rebuild (no per-call alloc),
        // valid for the current frame. Mirrors LiveAACacheSystem.m_coverage.
        private readonly List<BroadcastCoverage> m_coverage = new(16);

        // Per-tower Transform chunk change-version snapshot, rebuilt in lockstep with
        // m_coverage so CheckTowerRelocation can detect an in-place move without reading
        // Transform data.
        private readonly List<(Entity Tower, uint TransformVersion)> m_towerVersions = new(16);

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabTelecomLookup = GetComponentLookup<TelecomFacilityData>(true);
            m_transformHandle = GetComponentTypeHandle<Transform>(true);
            m_storageInfoLookup = GetEntityStorageInfoLookup();

            m_TelecomGateQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Buildings.TelecomFacility>());

            // Single owner of the telecom broadcast snapshot — expose the coverage circles to the
            // ThreatUI radar without it importing the Cognitive domain (Axiom 5). Fail-closed: the
            // null-object projects an empty broadcast umbrella.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IBroadcastCoverageReader>(this);

            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IBroadcastCoverageReader>(this);
            base.OnDestroy();
            Log.Info("Destroyed");
        }

        /// <summary>
        /// PLVS reconcile pass. The load-boundary world swap bumps the telecom order version,
        /// so the first post-load tick would rebuild anyway; resetting the cursor to 0 makes
        /// that rebuild deterministic rather than relying on the version having moved, and
        /// clearing the projections drops any circles cached from the pre-load city.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_LastTelecomOrderVersion = 0;
            m_coverage.Clear();
            m_towerVersions.Clear();
        }

        protected override void OnThrottledUpdate()
        {
            // PERF-LOCK: order-version gate — the city-wide Transform completion inside
            // RebuildCoverage is paid only when the telecom-tower set actually changed
            // (placement/demolition/load), not every throttle tick. Removing this gate
            // reinstates a per-second main-thread Transform sync point.
            // includeEntityType:false is DELIBERATE — with true the Entity type's order
            // version (bumped by ANY world-wide create/destroy) dominates the sum and the
            // gate opens every tick; see LiveAACacheSystem.OnUpdateImpl for the decompile
            // trail.
            uint v = (uint)m_TelecomGateQuery.GetCombinedComponentOrderVersion(includeEntityType: false);
            if (v != m_LastTelecomOrderVersion)
            {
                RebuildCoverage();
                m_LastTelecomOrderVersion = v;
                return;
            }

            CheckTowerRelocation();
        }

        /// <summary>
        /// No structural change to the telecom set — but a tower may have been relocated in
        /// place (Move It / vanilla relocate), which changes its Transform VALUE without
        /// bumping the TelecomFacility order version. Compare each cached tower's Transform
        /// chunk change-version against the snapshot taken at the last rebuild.
        /// </summary>
        private void CheckTowerRelocation()
        {
            m_transformHandle.Update(this);
            m_storageInfoLookup.Update(this);
            for (int i = 0; i < m_towerVersions.Count; i++)
            {
                var (tower, snapshotVersion) = m_towerVersions[i];
                if (!m_storageInfoLookup.Exists(tower))
                    continue;
                // PERF-LOCK: chunk-metadata read only (GetChangeVersion) — no Transform data
                // access, hence zero sync points. Replacing this with "read the Transform and
                // compare positions" reinstates the per-second Transform completion.
                uint currentVersion = m_storageInfoLookup[tower].Chunk.GetChangeVersion(ref m_transformHandle);
                if (ChangeVersionUtility.DidChange(currentVersion, snapshotVersion))
                {
                    RebuildCoverage();
                    break;
                }
            }
        }

        [CompletesDependency("RebuildCoverage: order-version-gated full telecom-set rebuild on placement/demolition/load (plus the relocation metadata-scan trigger). Runs the city-wide Transform read only when the tower set actually changed — rarer than the 1 s throttle.")]
        private void RebuildCoverage()
        {
            m_PrefabTelecomLookup.Update(this);
            m_transformHandle.Update(this);
            m_storageInfoLookup.Update(this);
            m_coverage.Clear();
            m_towerVersions.Clear();

            // Draw the umbrella at the DEFENDED radius, not the full vanilla signal radius: the
            // vanilla signal falls off as 1-(d/range)^2, so the radius where it reaches the
            // per-household defence cutoff is range*sqrt(1-threshold). This is the SAME cutoff the
            // MHR resolver feeds ResolveHouseholdPsyJob.InCoverage, so the overlay and the real
            // defended zone match — one tower stops blanketing the city. The vanilla m_Range (and
            // citizen telecom coverage/happiness) are untouched; we only scale what we draw/defend.
            // No local clamp: the [0..1] range is guaranteed at the source by the balance contract
            // (unit: ratio on Cognitive.CognitiveDefenseSignalThreshold), so the overlay reads the
            // SAME raw value the MHR resolver feeds the job — no per-consumer reinterpretation.
            var cog = BalanceConfig.Current?.Cognitive;
            float threshold = cog != null ? cog.CognitiveDefenseSignalThreshold : 0f;
            float defenceRadiusFactor = math.sqrt(1f - threshold);

            // Placed telecom towers only (exclude tool previews and bulldozed/marked entities).
            // Range comes from the prefab TelecomFacilityData; position from the placed object's
            // Transform. Both vanilla read-only — no mod state touched.
            foreach (var (transform, prefabRef, entity) in
                SystemAPI.Query<RefRO<Transform>, RefRO<PrefabRef>>()
                    .WithAll<Game.Buildings.TelecomFacility>()
                    .WithNone<Deleted, Temp>()
                    .WithEntityAccess())
            {
                if (!m_PrefabTelecomLookup.TryGetComponent(prefabRef.ValueRO.m_Prefab, out var data))
                    continue;

                float range = data.m_Range * defenceRadiusFactor;
                if (range <= 0f || float.IsNaN(range) || float.IsInfinity(range))
                    continue;

                float3 pos = transform.ValueRO.m_Position;
                if (float.IsNaN(pos.x) || float.IsInfinity(pos.x) ||
                    float.IsNaN(pos.z) || float.IsInfinity(pos.z))
                    continue;

                m_coverage.Add(new BroadcastCoverage(pos.x, pos.z, range));

                // Snapshot the tower's Transform chunk change-version so CheckTowerRelocation
                // can detect a later in-place move without reading Transform data.
                m_towerVersions.Add((entity, m_storageInfoLookup[entity].Chunk.GetChangeVersion(ref m_transformHandle)));
            }
        }

        /// <summary>
        /// <see cref="IBroadcastCoverageReader"/> — telecom broadcast coverage circles for the
        /// radar mental-view overlay. Returns the managed projection rebuilt on the throttled tick
        /// (no per-call alloc; valid for the current frame).
        /// </summary>
        public IReadOnlyList<BroadcastCoverage> GetCoverage() => m_coverage;
    }
}
