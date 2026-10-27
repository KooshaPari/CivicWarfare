using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.AirDefense;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.AirDefense.Jobs;
using CivicSurvival.Domains.AirDefense.Logic;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Shared live-AA cache. Holds a snapshot of every LIVE air-defense installation
    /// (Simulate enabled, not Deleted/Destroyed, building Transform resolved) in a
    /// Persistent <see cref="NativeList{AAData}"/>, with NO ammo/crew/cooldown filter —
    /// membership is liveness + position only. Consumers (BallisticDefenseSystem,
    /// AirDefenseOrchestrator) iterate the cache and re-read live ammo/crew/cooldown via
    /// their own RW ComponentLookup; the cache never holds ammo/cooldown as authority.
    ///
    /// Why this exists: the only main-thread touch of the vanilla <c>Simulate</c> tag (and
    /// of <c>Game.Objects.Transform</c>) is the cache rebuild. Per-frame consumer queries
    /// with <c>.WithAll&lt;Simulate&gt;()</c> forced a city-wide
    /// <c>CompleteDependencyBeforeRO&lt;Simulate&gt;</c> drain (700-890 ms during waves).
    /// The rebuild is gated by an ECS structural order-version cursor, so the Simulate +
    /// Transform completion is paid ONLY when the AA set actually changes (placement /
    /// destroy / load-boundary clear), not every frame.
    ///
    /// The gate query is AA-type only (no <c>Simulate</c>), so vanilla Simulate enabled-bit
    /// churn does not bump the cursor — exactly the AACrewReleaseSystem gate pattern.
    /// </summary>
    [ActIndependent]
    public sealed partial class LiveAACacheSystem : CivicSystemBase, IPostLoadValidation, IAirDefenseCoverageReader
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("LiveAACacheSystem");

        // Gate query: AA-type only, no Simulate (vanilla Simulate churn must not bump the cursor).
        // Mirrors AACrewReleaseSystem.cs:68-71.
        private EntityQuery m_aaGateQuery;

        // Order-version cursor. CS2 reuses system instances across load, so [NonSerialized]
        // survives — the explicit reset to 0 in ValidateAfterLoad makes the first post-load
        // rebuild deterministic (0 never equals a real version).
        [System.NonSerialized]
        [EntityQueryOrderCursor("Invalidates m_cache when the AirDefenseInstallation archetype set changes structurally (placement/destroy/load-boundary clear).")]
        private uint m_lastAAOrderVersion;

        // Persistent snapshot. Rebuilt only on order-version change. Valid for the current frame.
        // Derived ECS cache — not serialized by design (rebuilt from the AA query on the first
        // post-load tick via the order-version cursor; see ValidateAfterLoad).
        private NativeList<AAData> m_cache;

        // Managed coverage projection for the radar overlay (position + range only), rebuilt
        // in lockstep with m_cache so IAirDefenseCoverageReader.GetCoverage() returns a ready
        // list with no per-call sqrt/alloc. Reused across rebuilds.
        private readonly List<AaCoverage> m_coverage = new(16);

        private ComponentLookup<AirDefenseInstallation> m_aaLookup;
        private ComponentLookup<Simulate> m_simulateLookup;
        private ComponentLookup<Deleted> m_deletedLookup;
        private ComponentLookup<Destroyed> m_destroyedLookup;
        private ComponentLookup<Transform> m_transformLookup;
        // Type handle for the prop's Transform chunk change-version (metadata only, no data read / no
        // completion) — the persistent relocation signal; see OnUpdateImpl for why Updated cannot be used.
        private ComponentTypeHandle<Transform> m_transformHandle;
        private EntityStorageInfoLookup m_storageInfoLookup;

        private IRenderWriteBarrier m_renderWriteBarrier = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            // AA-type only — NO Simulate (so vanilla Simulate enabled-bit churn cannot bump the
            // cursor). Exactly AACrewReleaseSystem.cs:68-71.
            m_aaGateQuery = GetEntityQuery(
                ComponentType.ReadOnly<AirDefenseInstallation>(),
                ComponentType.Exclude<Deleted>()
            );

            m_aaLookup = GetComponentLookup<AirDefenseInstallation>(true);
            m_simulateLookup = GetComponentLookup<Simulate>(true);
            m_deletedLookup = GetComponentLookup<Deleted>(true);
            m_destroyedLookup = GetComponentLookup<Destroyed>(true);
            m_transformLookup = GetComponentLookup<Transform>(true);
            m_transformHandle = GetComponentTypeHandle<Transform>(true);
            m_storageInfoLookup = GetEntityStorageInfoLookup();

            m_cache = new NativeList<AAData>(16, Allocator.Persistent);

            // Single owner of the live AA snapshot — expose the coverage circles (position +
            // range) to the Core/CrossDomain radar reader without it importing AirDefense.Systems
            // (Axiom 5). Fail-closed: the null-object projects an empty defended zone.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IAirDefenseCoverageReader>(this);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_renderWriteBarrier ??= ServiceRegistry.Instance.Require<IRenderWriteBarrier>();
        }

        /// <summary>
        /// PLVS reconcile pass. The load-boundary destroy+create bumps AirDefenseInstallation's
        /// order version, so the first post-load tick would rebuild anyway; resetting the cursor
        /// to 0 makes that rebuild deterministic rather than relying on the version having moved.
        /// AirDefenseStateSystem.OnGamePreload destroys all pre-load installations before
        /// deserialize, so any Entity cached from the old world is dangling until this rebuild.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_lastAAOrderVersion = 0;
            if (m_cache.IsCreated)
                m_cache.Clear();
            m_coverage.Clear();
        }

        protected override void OnUpdateImpl()
        {
            // Metadata read, NOT a sync point (cf. BlackoutSystem.cs:472-473). Touches no
            // Simulate/Transform completion — only the structural order version of the AA set.
            // includeEntityType:false is DELIBERATE: with true, RequiredComponents[0] is the
            // Entity type (present in every archetype), whose order version increments on ANY
            // world-wide create/destroy (decompile EntityQueryImpl.cs:830-838 +
            // EntityComponentStore.IncrementComponentTypeOrderVersion:1615-1622) — i.e. every
            // frame in a live city, forcing a rebuild every frame. With false the version is the
            // order version of AirDefenseInstallation alone, which bumps only when the AA set
            // changes structurally (placement / Deleted-mark / destroy / load-clear). Exclude<Deleted>
            // is not in RequiredComponents (Deleted is not enableable), so it does not affect the sum.
            uint v = (uint)m_aaGateQuery.GetCombinedComponentOrderVersion(includeEntityType: false);
            if (v != m_lastAAOrderVersion)
            {
                RebuildCache();
                m_lastAAOrderVersion = v;
                return;
            }

            // No STRUCTURAL change to the AA set — but an installation's prop may have been RELOCATED
            // in place (Move It, or the vanilla relocate/modify path). That changes the prop's Transform
            // VALUE without bumping the AirDefenseInstallation order version, so the cached Position and
            // radar coverage would otherwise stay at the OLD spot (AA fires from / radar draws at the
            // pre-move location).
            //
            // We must NOT key on the vanilla Updated tag: it is stamped in ApplyTool (phase 25) and
            // cleared in Cleanup (phase 37), while this system runs in GameSimulation (phase 18) — before
            // the tag exists in frame N and after it is gone in frame N+1 — so GameSimulation never sees
            // it (and on pause GameSimulation does not tick at all). Instead we compare each cached prop's
            // Transform CHUNK CHANGE-VERSION to the value snapshotted at the last rebuild. The change
            // version is persistent (not a transient tag), bumps on any RW Transform write (both Move It
            // and vanilla relocate write RW), and is read from chunk metadata via GetChangeVersion — no
            // Transform data read, hence no completion / sync point. A destroyed prop is caught by the
            // structural gate above; guard with Exists anyway.
            m_transformHandle.Update(this);
            m_storageInfoLookup.Update(this);
            for (int i = 0; i < m_cache.Length; i++)
            {
                var prop = m_cache[i].Building.ToEntity();
                if (!m_storageInfoLookup.Exists(prop))
                    continue;
                uint currentVersion = m_storageInfoLookup[prop].Chunk.GetChangeVersion(ref m_transformHandle);
                if (ChangeVersionUtility.DidChange(currentVersion, m_cache[i].TransformVersion))
                {
                    RebuildCache();
                    break;
                }
            }
        }

        /// <summary>
        /// The ONLY site that runs <c>.WithAll&lt;Simulate&gt;()</c> + the per-AA Transform read,
        /// i.e. the only main-thread Simulate/Transform completion in the AA targeting path. Runs
        /// only when the AA set changed (seconds-minutes apart), so the drain is paid rarely.
        /// No ammo/crew/cooldown filter — membership is liveness + resolved building position.
        /// </summary>
        [CompletesDependency("RebuildCache: order-version-gated full AA-set rebuild on placement/destroy/load. Runs .WithAll<Simulate>() + per-AA Transform read (the city-wide Simulate/Transform completion) only when the AA set actually changed — rarer than any frame throttle.")]
        private void RebuildCache()
        {
            m_aaLookup.Update(this);
            m_simulateLookup.Update(this);
            m_deletedLookup.Update(this);
            m_destroyedLookup.Update(this);
            m_transformLookup.Update(this);
            m_transformHandle.Update(this);
            m_storageInfoLookup.Update(this);

            m_cache.Clear();
            m_coverage.Clear();

            var renderTicket = m_renderWriteBarrier.Consume(GetType(), RenderWriteComponentMask.BuildingTransform);
            EnsureRenderTicket(renderTicket, RenderWriteComponentMask.BuildingTransform);

            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<AirDefenseInstallation>>()
                .WithAll<Simulate>()
                .WithNone<Deleted, Destroyed>()
                .WithEntityAccess())
            {
                // Liveness + linked-building check (no ammo/crew/cooldown gate — those are
                // live-re-read by consumers, never cached).
                if (!AirDefenseLifecycle.TryGetActiveInstallation(
                        entity,
                        m_aaLookup,
                        m_storageInfoLookup,
                        m_simulateLookup,
                        m_deletedLookup,
                        m_destroyedLookup,
                        out var activeAA))
                    continue;

                // Position from the placed AA object's Transform — refreshed on placement/destroy
                // (order-version gate) and on in-place relocation (the OnUpdateImpl Updated scan).
                var buildingEntity = activeAA.GetBuildingEntity();
                if (!m_transformLookup.TryGetComponent(buildingEntity, out var buildingTransform))
                {
                    if (Log.IsDebugEnabled)
                        Log.Debug($"AA {entity.Index}: building {buildingEntity.Index}v{buildingEntity.Version} missing Transform — excluded from cache");
                    continue;
                }

                m_cache.Add(new AAData
                {
                    EntityIndex = entity.Index,
                    EntityVersion = entity.Version,
                    Position = buildingTransform.m_Position,
                    Rotation = buildingTransform.m_Rotation,
                    RangeSq = activeAA.Range * activeAA.Range,
                    InterceptChance = activeAA.InterceptChanceShahed,
                    InterceptChanceBallistic = activeAA.InterceptChanceBallistic,
                    CooldownDuration = activeAA.CooldownDuration,
                    CurrentAmmo = activeAA.CurrentAmmo,
                    MaxAmmo = activeAA.MaxAmmo,
                    Type = activeAA.Type,
                    Building = BuildingRef.FromEntity(buildingEntity),
                    // Snapshot the prop's Transform chunk change-version so OnUpdateImpl can detect a
                    // later in-place relocation without reading Transform data (metadata only).
                    TransformVersion = m_storageInfoLookup[buildingEntity].Chunk.GetChangeVersion(ref m_transformHandle)
                });

                // Coverage projection for the radar overlay — world X/Z + range (meters).
                m_coverage.Add(new AaCoverage(
                    buildingTransform.m_Position.x,
                    buildingTransform.m_Position.z,
                    activeAA.Range));
            }
        }

        /// <summary>
        /// IAirDefenseCoverageReader — live coverage circles for the radar defended-zone
        /// overlay. Returns the managed projection rebuilt in lockstep with the AA snapshot
        /// (no per-call sqrt/alloc; valid for the current frame, like the snapshot itself).
        /// </summary>
        public IReadOnlyList<AaCoverage> GetCoverage() => m_coverage;

        /// <summary>
        /// Snapshot of all live AA for the current frame. Read-only view over the Persistent
        /// list — no sync point, no allocation. Ammo/cooldown in the entries are a snapshot from
        /// the last rebuild; consumers MUST re-read those live via their own RW ComponentLookup.
        /// </summary>
        public NativeArray<AAData>.ReadOnly GetLiveAASnapshot() => m_cache.AsArray().AsReadOnly();

        private static void EnsureRenderTicket(RenderWriteTicket renderTicket, RenderWriteComponentMask requiredMask)
        {
            if (!renderTicket.Covers(requiredMask))
                throw new System.InvalidOperationException($"Render write ticket does not cover {requiredMask}");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IAirDefenseCoverageReader>(this);
            if (m_cache.IsCreated)
                m_cache.Dispose();
            base.OnDestroy();
            Log.Info("Destroyed");
        }
    }
}
