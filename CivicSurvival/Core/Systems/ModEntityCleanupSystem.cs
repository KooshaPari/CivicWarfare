using Game;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Simulation;
using System.Collections.Generic;
using Unity.Entities;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Domain.GridWarfare;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// Orphan cleanup for mod sidecar entities.
    ///
    /// Small sidecar populations (spotters, contracts, plant records, …) are scanned with a
    /// managed main-thread <c>SystemAPI.Query</c> foreach — the chunk set is stable for the
    /// duration of the loop and the counts are dozens-to-hundreds.
    ///
    /// The psy sidecar scan (one per household, ~170k, Burst job + same-frame fence) lives in
    /// <see cref="PsyOrphanScanSystem"/> — split out so its Complete() stops draining this
    /// system's much wider Dependency (combat sidecar writers; SP:MECS.PsyComplete was
    /// 2900ms/session, 71% of MECS, PERF.log 2026-07-21). This system schedules no jobs.
    /// </summary>
    [ActIndependent]
    public partial class ModEntityCleanupSystem : CivicSystemBase, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ModEntityCleanupSystem");
        private const float THROTTLE_INTERVAL = 0.25f;
        private const int ROTATION_BATCH_COUNT = 4;
        public int HydrationOrder => HydrationPriority.CLEANUP_FIRST;

        private EntityQuery m_SpotterQuery;
        private EntityQuery m_ContractQuery;
        private EntityQuery m_DisabledByDisasterQuery;
        private EntityQuery m_CollapsedProducerQuery;
        private EntityQuery m_EquipmentWearQuery;
        private EntityQuery m_UnderConstructionQuery;
        private EntityQuery m_BackupPowerQuery;
        private EntityQuery m_CounterfeitBatteryQuery;
        private EntityQuery m_PsyOpsAttackQuery;
        private EntityQuery m_PowerPlantDamageQuery;
        private EntityQuery m_AAInstallationQuery;
        private EntityQuery m_DroneLauncherInstallationQuery;
        private EntityQuery m_CivilianDamageQuery;
        private EntityQuery m_DeletedBackupQuery;

        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ModCleanupBarrier m_ModCleanupBarrier = null!;
        private float m_ThrottleTimer;
        [System.NonSerialized] private int m_RotationBatch;
        private bool m_LoggedSpotterRebindBlocked;
        private bool m_LoggedCivilianDamageRebindBlocked;

        // Same-pass dedup guard. Forward cleanup queries are pairwise disjoint by
        // sidecar component type, so cross-type collisions are not expected; this is a
        // cheap defensive guard so a future overlapping component cannot queue a double
        // AddComponent<Deleted>. Field + Clear() per tick (CIVIC050 bans new-in-OnUpdate).
        private readonly HashSet<Entity> m_QueuedDeleted = new(256);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SpotterQuery = GetEntityQuery(ComponentType.ReadOnly<SpotterData>(), ComponentType.Exclude<Deleted>());
            m_ContractQuery = GetEntityQuery(ComponentType.ReadOnly<ContractData>(), ComponentType.Exclude<Deleted>());
            m_DisabledByDisasterQuery = GetEntityQuery(ComponentType.ReadOnly<DisabledByDisaster>(), ComponentType.Exclude<Deleted>());
            m_CollapsedProducerQuery = GetEntityQuery(ComponentType.ReadOnly<CollapsedProducer>(), ComponentType.Exclude<Deleted>());
            m_EquipmentWearQuery = GetEntityQuery(ComponentType.ReadOnly<EquipmentWear>(), ComponentType.Exclude<Deleted>());
            m_UnderConstructionQuery = GetEntityQuery(ComponentType.ReadOnly<UnderConstruction>(), ComponentType.Exclude<Deleted>());
            m_BackupPowerQuery = GetEntityQuery(ComponentType.ReadOnly<BackupPower>(), ComponentType.Exclude<Deleted>());
            m_CounterfeitBatteryQuery = GetEntityQuery(ComponentType.ReadOnly<CounterfeitBattery>(), ComponentType.Exclude<Deleted>());
            m_PsyOpsAttackQuery = GetEntityQuery(ComponentType.ReadOnly<PsyOpsAttack>(), ComponentType.Exclude<Deleted>());
            m_PowerPlantDamageQuery = GetEntityQuery(ComponentType.ReadOnly<PowerPlantDamage>(), ComponentType.Exclude<Deleted>());
            m_AAInstallationQuery = GetEntityQuery(ComponentType.ReadOnly<AirDefenseInstallation>(), ComponentType.Exclude<Deleted>());
            m_DroneLauncherInstallationQuery = GetEntityQuery(ComponentType.ReadOnly<DroneLauncherInstallation>(), ComponentType.Exclude<Deleted>());
            m_CivilianDamageQuery = GetEntityQuery(ComponentType.ReadOnly<CivilianWarDamage>(), ComponentType.Exclude<Deleted>());
            m_DeletedBackupQuery = GetEntityQuery(ComponentType.ReadOnly<BackupPower>(), ComponentType.ReadOnly<Deleted>());

            // No RequireAnyForUpdate — this many queries combined exceed the 2048-byte
            // UnsafeScratchAllocator stack buffer inside EntityQueryManager,
            // causing NRE via buffer overflow (no bounds check in production builds).
            // Instead: OnUpdateImpl does cheap IsEmpty early-out before any lookups/ECB.

            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_ModCleanupBarrier = World.GetOrCreateSystemManaged<ModCleanupBarrier>();

            Log.Info("Created — managed orphan cleanup ready");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (purpose != Purpose.LoadGame && purpose != Purpose.NewGame)
                return;

            int cleared = 0;
            cleared += DestroyPreloadEntities(m_PowerPlantDamageQuery);
            cleared += DestroyPreloadEntities(m_SpotterQuery);
            cleared += DestroyPreloadEntities(m_ContractQuery);
            cleared += DestroyPreloadEntities(m_DisabledByDisasterQuery);
            cleared += DestroyPreloadEntities(m_CollapsedProducerQuery);
            cleared += DestroyPreloadEntities(m_EquipmentWearQuery);
            cleared += DestroyPreloadEntities(m_UnderConstructionQuery);
            cleared += DestroyPreloadEntities(m_BackupPowerQuery);
            cleared += DestroyPreloadEntities(m_CounterfeitBatteryQuery);
            cleared += DestroyPreloadEntities(m_PsyOpsAttackQuery);
            cleared += DestroyPreloadEntities(m_CivilianDamageQuery);
            cleared += DestroyPreloadEntities(m_DeletedBackupQuery);
            // NOTE: m_AAInstallationQuery is intentionally NOT purged at the load boundary
            // (parity with prior behavior — AA installations are restored, not orphaned, at
            // load). Runtime rotation still cleans dead AA sidecars each session.

            if (cleared > 0)
                Log.Info($"OnGamePreload({purpose}): cleared {cleared} pre-load mod entities at load-boundary");
        }

        private int DestroyPreloadEntities(EntityQuery query)
        {
            int count = query.CalculateEntityCount();
            if (count > 0)
                EntityManager.DestroyEntity(query);
            return count;
        }

        /// <summary>Dead target + same-pass dedup. True ⇒ queue the sidecar for Deleted.</summary>
        private bool ShouldQueueDeleted(Entity target, Entity sidecar)
            => TargetLiveness.IsDeadTarget(target, m_StorageInfoLookup, m_DeletedLookup, m_DestroyedLookup)
               && m_QueuedDeleted.Add(sidecar);

        /// <summary>
        /// Gone target (entity missing or Deleted — vanilla <c>Destroyed</c> is NOT gone) +
        /// same-pass dedup. Plant-family sidecars (EquipmentWear, PowerPlantDamage,
        /// UnderConstruction, CollapsedProducer, DisabledByDisaster) use THIS predicate:
        /// a destroyed plant is a standing ruin whose damage record drives the DESTROYED
        /// counter, the knocked-out nameplate exclusion and repair billing. Cleaning these
        /// on Destroyed fought EquipmentWearAssignSystem (its query excludes only Deleted),
        /// producing a delete/recreate loop — the INFRA table flickered between "no plants"
        /// and rows with reset statuses.
        /// </summary>
        private bool ShouldQueueDeletedGone(Entity target, Entity sidecar)
            => TargetLiveness.IsGoneTarget(target, m_StorageInfoLookup, m_DeletedLookup)
               && m_QueuedDeleted.Add(sidecar);

        protected override void OnUpdateImpl()
        {
            m_ThrottleTimer += SystemAPI.Time.DeltaTime;
            if (m_ThrottleTimer < THROTTLE_INTERVAL) return;
            m_ThrottleTimer = 0f;

            bool canPurgeSpotters = BuildingRefRebindRegistry.CanPurge<SpotterData>();
            bool canPurgeCivilianDamage = BuildingRefRebindRegistry.CanPurge<CivilianWarDamage>();
            bool hasSpottersToPurge = canPurgeSpotters && !m_SpotterQuery.IsEmpty;
            bool hasCivilianDamageToPurge = canPurgeCivilianDamage && !m_CivilianDamageQuery.IsEmpty;

            if (!canPurgeSpotters && !m_SpotterQuery.IsEmpty && !m_LoggedSpotterRebindBlocked)
            {
                m_LoggedSpotterRebindBlocked = true;
                Log.Warn("SpotterData cleanup blocked until post-load building-ref rebind completes");
            }
            if (!canPurgeCivilianDamage && !m_CivilianDamageQuery.IsEmpty && !m_LoggedCivilianDamageRebindBlocked)
            {
                m_LoggedCivilianDamageRebindBlocked = true;
                Log.Warn("CivilianWarDamage cleanup blocked until post-load building-ref rebind completes");
            }

            // Early-out: IsEmpty reads cached chunk count (no sync point, no alloc).
            if (!hasSpottersToPurge && m_ContractQuery.IsEmpty && m_DisabledByDisasterQuery.IsEmpty
                && m_CollapsedProducerQuery.IsEmpty && m_EquipmentWearQuery.IsEmpty && m_UnderConstructionQuery.IsEmpty
                && m_BackupPowerQuery.IsEmpty && m_CounterfeitBatteryQuery.IsEmpty
                && m_PowerPlantDamageQuery.IsEmpty && m_AAInstallationQuery.IsEmpty && !hasCivilianDamageToPurge
                && m_DeletedBackupQuery.IsEmpty)
            {
                AdvanceRotationBatch();
                return;
            }

            using (PerformanceProfiler.MeasureDebug("SP:MECS.LookupSync"))
            {
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                m_StorageInfoLookup.Update(this);
            }

            m_QueuedDeleted.Clear();

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            EntityCommandBuffer EnsureEcb()
            {
                if (!ecbCreated)
                {
                    ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }
                return ecb;
            }

            using (PerformanceProfiler.Measure("MECS.CleanupPass"))
            {
                switch (m_RotationBatch)
                {
                    case 0:
                        // PowerPlantDamage — has GetBuildingEntity but does not declare
                        // IBuildingLinked; null building ⇒ gone (IsGoneTarget subsumes it).
                        // Plant family ⇒ gone-only: DamagePercent=1 IS the ruin's destroyed
                        // marker (UI DESTROYED tile, IsKnockedOut nameplate exclusion).
                        if (!m_PowerPlantDamageQuery.IsEmpty)
                        {
                            foreach (var (dmg, entity) in SystemAPI.Query<RefRO<PowerPlantDamage>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                            {
                                var d = dmg.ValueRO;
                                bool gone = d.Building.IsNull
                                    || TargetLiveness.IsGoneTarget(d.Building.ToEntity(),
                                        m_StorageInfoLookup, m_DeletedLookup);
                                if (gone && m_QueuedDeleted.Add(entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                            }
                        }
                        if (hasSpottersToPurge)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<SpotterData>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        break;

                    case 1:
                        if (!m_ContractQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<ContractData>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        // Plant family ⇒ gone-only: the disaster window auto-expires via
                        // RestoreHour and the collapse lifecycle is owned by GridStressSystem
                        // (RestoreAllProducers); a Destroyed ruin keeps both states.
                        if (!m_DisabledByDisasterQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<DisabledByDisaster>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeletedGone(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (!m_CollapsedProducerQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<CollapsedProducer>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeletedGone(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        break;

                    case 2:
                        // Plant family ⇒ gone-only: wiping wear on a Destroyed ruin reset the
                        // plant's wear/ID record and fought EquipmentWearAssignSystem's
                        // recreate (the flicker loop); wiping construction would let a ruined
                        // build site skip the remaining ramp after restoration.
                        if (!m_EquipmentWearQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<EquipmentWear>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeletedGone(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (!m_UnderConstructionQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<UnderConstruction>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeletedGone(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (!m_BackupPowerQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<BackupPower>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        break;

                    default:
                        if (!m_CounterfeitBatteryQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<CounterfeitBattery>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (!m_AAInstallationQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<AirDefenseInstallation>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (!m_DroneLauncherInstallationQuery.IsEmpty)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<DroneLauncherInstallation>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        if (hasCivilianDamageToPurge)
                        {
                            foreach (var (c, entity) in SystemAPI.Query<RefRO<CivilianWarDamage>>()
                                         .WithNone<Deleted>().WithEntityAccess())
                                if (ShouldQueueDeleted(c.ValueRO.GetBuildingEntity(), entity))
                                    EnsureEcb().AddComponent<Deleted>(entity);
                        }
                        break;
                }

                // (Reverse BackupPowerRef repair removed: the building → backup link is no longer a
                // component on the vanilla building. BackupPowerDistributionSystem rebuilds the link
                // map each tick from live BackupPower entities WithNone<Deleted>, so a deleted backup
                // drops out of the map automatically — no per-building repair needed.)
            }

            AdvanceRotationBatch();

            // ECB is filled synchronously on the main thread; ModCleanupBarrier plays it
            // back at its update. This system schedules no jobs, so no producer job is ever
            // in flight here — registering the current dependency keeps the barrier contract
            // intact.
            if (ecbCreated)
                m_ModCleanupBarrier.AddJobHandleForProducer(Dependency);
        }

        public void ValidateAfterLoad()
        {
            m_ThrottleTimer = THROTTLE_INTERVAL;
            m_LoggedSpotterRebindBlocked = false;
            m_LoggedCivilianDamageRebindBlocked = false;
        }

        private void AdvanceRotationBatch()
        {
            m_RotationBatch = (m_RotationBatch + 1) % ROTATION_BATCH_COUNT;
        }
    }
}
