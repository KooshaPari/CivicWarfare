using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Colossal.Logging;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using Game.Areas;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.PowerBackup.Systems
{
    /// <summary>
    /// Distribution system for backup power - assigns batteries to buildings.
    ///
    /// Level-based distribution (Building Level 1-5):
    /// - Higher level = wealthier building = higher chance of backup power
    /// - Creates "class inequality" - poor areas dark, rich areas lit during blackouts
    ///
    /// Example (Residential):
    /// - Lvl 1 (Poor tenement): 0% chance
    /// - Lvl 3 (Middle class): 30% chance
    /// - Lvl 5 (Elite penthouse): 90% chance
    /// </summary>
#pragma warning disable CA1001 // m_LinkMap disposed in OnDestroy (ECS lifecycle, not IDisposable pattern)
    [ActIndependent]
    public partial class BackupPowerDistributionSystem : ThrottledSystemBase, IPostLoadValidation
#pragma warning restore CA1001
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("BackupPowerDistribution");
        private const uint LCG_MULTIPLIER = 1103515245u;
        private const uint FRAME_MIX_MULTIPLIER = 2654435769u;
        private const uint COUNT_MIX_MULTIPLIER = 2246822519u;
        private const uint CATCH_UP_SEED_SALT = 0xBACC_0FFEu;

        private EntityQuery m_NewBuildingsQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ComponentLookup<ResidentialProperty> m_ResidentialLookup;
        private ComponentLookup<CommercialProperty> m_CommercialLookup;
        private ComponentLookup<IndustrialProperty> m_IndustrialLookup;
        private ComponentLookup<OfficeProperty> m_OfficeLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentLookup<SpawnableBuildingData> m_SpawnableLookup;
        private ComponentLookup<Game.Buildings.Hospital> m_HospitalLookup;
        private ComponentLookup<Game.Buildings.School> m_SchoolLookup;
        // Building → live backup mod-entity link map (Core service), replacing the BackupPowerRef
        // component that used to hang on vanilla buildings (archetype churn → vanilla render
        // chunk-cache rot → crash). Owned here: registered in OnCreate, disposed in OnDestroy.
        private BackupPowerLinkMap m_LinkMap = null!;
        private EntityQuery m_UnprocessedBuildingsQuery;

        // M2 FIX: Cache ModSettings (avoid lookup in hot path)
        private ModSettings? m_Settings;

        // Migration: scan existing Hospital/School buildings once
        private bool m_MigrationComplete;
        // Migration: scan existing BackupPower entities for missing BatteryLayerTag
        private bool m_LayerTagMigrationComplete;
        // Tracks if system was disabled by BackupPowerEnabled setting (not by load/generic enable)
        [System.NonSerialized] private bool m_WasDisabledBySettings;
        private readonly HashSet<long> m_NoBackupBuildingKeys = new();
        // Reused scratch for the per-scan assigned-backup set — avoids a per-call HashSet alloc
        // (CIVIC050). Safe to share: the scan path (BuildProcessedBackupBuildingSet) and the save
        // path (BuildNoBackupBuildingRefsForSave) never hold a result across each other's call, and
        // each call clears it first.
        private readonly HashSet<long> m_AssignedBackupScratch = new();

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND; // Every 60 frames (~1 second)

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            Log.Info($"{nameof(BackupPowerDistributionSystem)} created (level-based distribution)");

            // Query for NEWLY CREATED buildings with electricity
            m_NewBuildingsQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<ElectricityConsumer>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Created>(),  // Only new buildings!
                ComponentType.Exclude<Deleted>()
            );

            m_ResidentialLookup = GetComponentLookup<ResidentialProperty>(true);
            m_CommercialLookup = GetComponentLookup<CommercialProperty>(true);
            m_IndustrialLookup = GetComponentLookup<IndustrialProperty>(true);
            m_OfficeLookup = GetComponentLookup<OfficeProperty>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_SpawnableLookup = GetComponentLookup<SpawnableBuildingData>(true);
            m_HospitalLookup = GetComponentLookup<Game.Buildings.Hospital>(true);
            m_SchoolLookup = GetComponentLookup<Game.Buildings.School>(true);

            // All electricity buildings. The "already processed" filter is now per-entity against
            // the scanned processed set (no queryable BackupPowerRef marker — that component is gone).
            m_UnprocessedBuildingsQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<ElectricityConsumer>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>()
            );

            // Own the building → backup link map (replaces the BackupPowerRef component). Lifetime =
            // world; consumers (BlackoutSystem, Corruption) resolve the reader in OnStartRunning.
            m_LinkMap = new BackupPowerLinkMap();
            m_LinkMap.Initialize();
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IBackupPowerLinkReader>(m_LinkMap);
                ServiceRegistry.Instance.Register<IBackupPowerLinkWriter>(m_LinkMap);
            }
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
        }

        protected override bool ShouldSkipUpdate()
        {
            bool skip = m_Settings == null || !m_Settings.BackupPowerEnabled;
            if (skip) m_WasDisabledBySettings = true;
            return skip;
        }

        protected override void OnThrottledUpdate()
        {
            m_PrefabRefLookup.Update(this);
            m_HospitalLookup.Update(this);
            m_SchoolLookup.Update(this);
            m_ResidentialLookup.Update(this);
            m_CommercialLookup.Update(this);
            m_IndustrialLookup.Update(this);
            m_OfficeLookup.Update(this);
            m_SpawnableLookup.Update(this);

            // Migration: add BatteryLayerTag to existing BackupPower entities without it
            if (!m_LayerTagMigrationComplete)
            {
                MigrateLayerTags();
                m_LayerTagMigrationComplete = true;
            }

            var processedBuildingKeys = BuildProcessedBackupBuildingSet();

            // Migration: create battery entities for existing Hospital/School buildings
            if (!m_MigrationComplete)
            {
                MigrateHospitalSchoolBuildings(processedBuildingKeys);
                m_MigrationComplete = true;
            }

            // New Created entities get first chance, then the no-Created catch-up path
            // handles buildings missed between throttled scans or settings disable/enable.
            ProcessNewBuildings(processedBuildingKeys);
            CatchUpMissedBuildings(processedBuildingKeys);

            // Publish the building → live backup mod-entity link map for consumers (BlackoutJob,
            // Corruption). Rebuilt wholesale from live BackupPower entities — zero components on any
            // vanilla building, so zero archetype churn. Entities created via ECB this tick become
            // visible next tick (ECB plays back after this system); within-tick dedup is the
            // processed set above.
            RebuildLinkMap();

            if (m_WasDisabledBySettings)
                m_WasDisabledBySettings = false;
        }

        /// <summary>
        /// Rebuild the building → live backup mod-entity link buffer from live BackupPower entities.
        /// Wholesale rebuild into the triple-buffer write slot, then publish. No structural change
        /// on any vanilla building (the old BackupPowerRef component is gone).
        /// </summary>
        private void RebuildLinkMap()
        {
            var links = m_LinkMap.BeginWrite();
            foreach (var (backup, modEntity) in
                SystemAPI.Query<RefRO<BackupPower>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                if (backup.ValueRO.Type == BackupPowerType.None)
                    continue;
                var building = backup.ValueRO.Building;
                if (building.IsNull)
                    continue;
                links.TryAdd(building.Packed, modEntity);
            }
            m_LinkMap.CommitWrite();
        }

        [CompletesDependency("ProcessNewBuildings: count short-circuits empty case before allocating ECB; called from OnThrottledUpdate (60-frame interval), sync amortised")]
        private void ProcessNewBuildings(HashSet<long> processedBuildingKeys)
        {
            int entityCount = m_NewBuildingsQuery.CalculateEntityCount();
            if (entityCount == 0)
                return;

            if (Log.IsDebugEnabled) Log.Debug($"BackupPowerDistribution: Processing {entityCount} new buildings");

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            int assigned = 0;
            // RNG-001 FIX: Hash mixing to avoid commutative collision (frame+count = count+frame)
            uint seed = ((uint)System.Environment.TickCount * FRAME_MIX_MULTIPLIER)
                        ^ ((uint)UnityEngine.Time.frameCount * LCG_MULTIPLIER)
                        ^ ((uint)entityCount * COUNT_MIX_MULTIPLIER);
            if (seed == 0) seed = 1;
            var random = new Random(seed);

            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<PrefabRef>>()
                .WithAll<Building, ElectricityConsumer, Created>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Skip if building already processed (has a live backup or a no-backup sentinel)
                if (IsBackupProcessingKnown(entity, processedBuildingKeys))
                    continue;

                var result = DetermineBackupPowerWithLayer(entity, ref random);

                if (result.Backup.HasValue)
                {
                    if (!ecbCreated)
                    {
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        ecbCreated = true;
                    }
                    var backupData = result.Backup.Value;
                    backupData.Building = BuildingRef.FromEntity(entity);
                    var backupEntity = ecb.CreateEntity();
                    ecb.AddComponent(backupEntity, backupData);
                    ecb.AddComponent(backupEntity, new BatteryLayerTag { Layer = result.Layer });
                    // Link is established via backupData.Building; RebuildLinkMap publishes it.
                    // Nothing is written on the vanilla building entity.
                    m_NoBackupBuildingKeys.Remove(BuildingRef.FromEntity(entity).Packed);
                    MarkBackupProcessingKnown(entity, processedBuildingKeys);
                    assigned++;
                }
                else
                {
                    // No backup power — mark as processed via the persistent no-backup set.
                    m_NoBackupBuildingKeys.Add(BuildingRef.FromEntity(entity).Packed);
                    MarkBackupProcessingKnown(entity, processedBuildingKeys);
                }
            }

            if (ecbCreated)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);

            if (assigned > 0)
            {
                Log.Info($"BackupPowerDistribution: Assigned backup power to {assigned} buildings");
            }
        }

        /// <summary>
        /// Get building level from prefab (1-5).
        /// Returns 1 as fallback if level cannot be determined.
        /// </summary>
        private int GetBuildingLevel(Entity entity)
        {
            // Get prefab reference
            if (!m_PrefabRefLookup.HasComponent(entity))
                return 1;

            Entity prefab = m_PrefabRefLookup[entity].m_Prefab;

            // Get spawnable data from prefab
            if (!m_SpawnableLookup.HasComponent(prefab))
                return 1;

            int level = m_SpawnableLookup[prefab].m_Level;

            // Clamp to valid range
            return math.clamp(level, 1, 5);
        }

        private readonly struct BackupPowerResult
        {
            public readonly BackupPower? Backup;
            public readonly BatteryLayer Layer;

            public BackupPowerResult(BackupPower? backup, BatteryLayer layer)
            {
                Backup = backup;
                Layer = layer;
            }
        }

        private BackupPowerResult DetermineBackupPowerWithLayer(Entity entity, ref Random random)
        {
            // Hospital: always gets IndustrialBattery (100kWh), 100% probability
            if (m_HospitalLookup.HasComponent(entity))
            {
                return new BackupPowerResult(BackupPowerFactory.CreateIndustrialBattery(), BatteryLayer.Hospital);
            }

            // School: always gets BusinessUPS (10kWh), 100% probability
            if (m_SchoolLookup.HasComponent(entity))
            {
                return new BackupPowerResult(BackupPowerFactory.CreateBusinessUPS(), BatteryLayer.School);
            }

            // Private: R/C/I/O with level-based probability
            int level = GetBuildingLevel(entity);

            if (m_ResidentialLookup.HasComponent(entity))
            {
                float chance = BackupPowerFactory.GetResidentialChance(level);
                if (random.NextFloat() < chance)
                    return new BackupPowerResult(BackupPowerFactory.CreateHomeBattery(), BatteryLayer.Private);
            }
            else if (m_CommercialLookup.HasComponent(entity))
            {
                float chance = BackupPowerFactory.GetCommercialChance(level);
                if (random.NextFloat() < chance)
                    return new BackupPowerResult(BackupPowerFactory.CreateBusinessUPS(), BatteryLayer.Private);
            }
            else if (m_IndustrialLookup.HasComponent(entity))
            {
                float chance = BackupPowerFactory.GetIndustrialChance(level);
                if (random.NextFloat() < chance)
                    return new BackupPowerResult(BackupPowerFactory.CreateIndustrialBattery(), BatteryLayer.Private);
            }
            else if (m_OfficeLookup.HasComponent(entity))
            {
                float chance = BackupPowerFactory.GetOfficeChance(level);
                if (random.NextFloat() < chance)
                    return new BackupPowerResult(BackupPowerFactory.CreateBusinessUPS(), BatteryLayer.Private);
            }

            return new BackupPowerResult(null, BatteryLayer.Private);
        }

        /// <summary>
        /// Migration: create battery mod entities for existing Hospital/School buildings
        /// that don't have BackupPower yet.
        /// </summary>
        private void MigrateHospitalSchoolBuildings(HashSet<long> processedBuildingKeys)
        {
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            int migrated = 0;

            // Hospitals
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<Game.Buildings.Hospital>>()
                .WithAll<Building, ElectricityConsumer>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Skip if already processed (has a live backup link or a no-backup sentinel)
                if (IsBackupProcessingKnown(entity, processedBuildingKeys))
                    continue;

                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }

                var backupData = BackupPowerFactory.CreateIndustrialBattery();
                backupData.Building = BuildingRef.FromEntity(entity);

                var backupEntity = ecb.CreateEntity();
                ecb.AddComponent(backupEntity, backupData);
                ecb.AddComponent(backupEntity, new BatteryLayerTag { Layer = BatteryLayer.Hospital });
                m_NoBackupBuildingKeys.Remove(BuildingRef.FromEntity(entity).Packed);
                MarkBackupProcessingKnown(entity, processedBuildingKeys);
                migrated++;
            }

            // Schools
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<Game.Buildings.School>>()
                .WithAll<Building, ElectricityConsumer>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Skip if already processed (has a live backup link or a no-backup sentinel)
                if (IsBackupProcessingKnown(entity, processedBuildingKeys))
                    continue;

#pragma warning disable CIVIC118 // Lazy ECB: created at most once via hasEcb guard
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
#pragma warning restore CIVIC118

                var backupData = BackupPowerFactory.CreateBusinessUPS();
                backupData.Building = BuildingRef.FromEntity(entity);

                var backupEntity = ecb.CreateEntity();
                ecb.AddComponent(backupEntity, backupData);
                ecb.AddComponent(backupEntity, new BatteryLayerTag { Layer = BatteryLayer.School });
                m_NoBackupBuildingKeys.Remove(BuildingRef.FromEntity(entity).Packed);
                MarkBackupProcessingKnown(entity, processedBuildingKeys);
                migrated++;
            }

            if (hasEcb) m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);

            if (migrated > 0)
            {
                Log.Info($"BackupPowerDistribution: Migrated {migrated} Hospital/School buildings to three-layer battery system");
            }
        }

        public void ValidateAfterLoad()
        {
            int validated = ValidateNoBackupSentinelsAfterLoad();
            // Seed the link map immediately so consumers (BlackoutJob, Corruption) have data on the
            // first post-load frame instead of waiting for the first throttled tick.
            RebuildLinkMap();
            Log.Info($"Post-load: validated {validated} no-backup sentinels, seeded backup link map");
        }

        /// <summary>
        /// Drop no-backup sentinel keys whose building no longer exists after load. The live
        /// building → backup links themselves are rebuilt from the saved BackupPower entities
        /// (see <see cref="RebuildLinkMap"/>); nothing is written on any vanilla building.
        /// </summary>
        private int ValidateNoBackupSentinelsAfterLoad()
        {
            List<long>? invalidKeys = null;
            foreach (long key in m_NoBackupBuildingKeys)
            {
                var buildingEntity = BuildingRef.FromPacked(key).ToEntity();
                if (buildingEntity == Entity.Null ||
                    !EntityManager.Exists(buildingEntity) ||
                    !EntityManager.HasComponent<PrefabRef>(buildingEntity))
                {
                    invalidKeys ??= new List<long>();
                    invalidKeys.Add(key);
                }
            }

            if (invalidKeys != null)
            {
                for (int i = 0; i < invalidKeys.Count; i++)
                    m_NoBackupBuildingKeys.Remove(invalidKeys[i]);
            }

            return m_NoBackupBuildingKeys.Count;
        }

        /// <summary>
        /// Migration: add BatteryLayerTag to existing BackupPower mod entities without it.
        /// Infers layer from vanilla building type (Hospital/School → typed, else Private).
        /// </summary>
        private void MigrateLayerTags()
        {
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            int migrated = 0;

            foreach (var (backup, backupEntity) in
                SystemAPI.Query<RefRO<BackupPower>>()
                .WithNone<Deleted, BatteryLayerTag>()
                .WithEntityAccess())
            {
                var buildingEntity = backup.ValueRO.GetBuildingEntity();
                BatteryLayer layer = BatteryLayer.Private;

                if (m_HospitalLookup.HasComponent(buildingEntity))
                    layer = BatteryLayer.Hospital;
                else if (m_SchoolLookup.HasComponent(buildingEntity))
                    layer = BatteryLayer.School;

                if (!ecbCreated)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }
                ecb.AddComponent(backupEntity, new BatteryLayerTag { Layer = layer });
                migrated++;
            }

            if (ecbCreated) m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            if (migrated > 0) Log.Info($"Migrated {migrated} existing BackupPower entities with BatteryLayerTag");
        }

        /// <summary>
        /// Catch-up: assign backup power to buildings that missed their one-frame Created scan.
        /// Uses same logic as ProcessNewBuildings but WITHOUT Created filter.
        /// The link map and the per-tick processed set keep this idempotent.
        /// </summary>
        private void CatchUpMissedBuildings(HashSet<long> processedBuildingKeys)
        {
            // Lookups already updated at the top of OnThrottledUpdate

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            int assigned = 0;
            // Unprocessed = all electricity buildings minus those already processed (live link or
            // no-backup sentinel). The query no longer excludes a BackupPowerRef component, so the
            // "not processed" count is computed here and re-checked per entity below.
            int entityCount = CountAllBuildingsForCatchUp() - processedBuildingKeys.Count;
            if (entityCount <= 0) return;

            uint seed = math.hash(new uint4(
                (uint)System.Environment.TickCount,
                (uint)UnityEngine.Time.frameCount,
                (uint)entityCount,
                CATCH_UP_SEED_SALT));
            if (seed == 0) seed = 1;
            var random = new Unity.Mathematics.Random(seed);

            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<PrefabRef>>()
                .WithAll<Building, ElectricityConsumer>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                if (IsBackupProcessingKnown(entity, processedBuildingKeys))
                    continue;

                var result = DetermineBackupPowerWithLayer(entity, ref random);

                if (result.Backup.HasValue)
                {
                    if (!ecbCreated)
                    {
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        ecbCreated = true;
                    }
                    var backupData = result.Backup.Value;
                    backupData.Building = BuildingRef.FromEntity(entity);
                    var modEntity = ecb.CreateEntity();
                    ecb.AddComponent(modEntity, backupData);
                    ecb.AddComponent(modEntity, new BatteryLayerTag { Layer = result.Layer });
                    // Link via backupData.Building; RebuildLinkMap publishes it. No write on the building.
                    m_NoBackupBuildingKeys.Remove(BuildingRef.FromEntity(entity).Packed);
                    MarkBackupProcessingKnown(entity, processedBuildingKeys);
                    assigned++;
                }
                else
                {
                    // Mark as processed — prevents re-roll on future catch-ups
                    m_NoBackupBuildingKeys.Add(BuildingRef.FromEntity(entity).Packed);
                    MarkBackupProcessingKnown(entity, processedBuildingKeys);
                }
            }

            if (ecbCreated) m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            if (assigned > 0) Log.Info($"[CatchUp] Assigned backup power to {assigned} buildings missed by the Created scan");
        }

        [CompletesDependency("CatchUpMissedBuildings: total building count short-circuits empty case; called from OnThrottledUpdate (60-frame interval), sync amortised")]
        private int CountAllBuildingsForCatchUp()
            => m_UnprocessedBuildingsQuery.CalculateEntityCount();

        private HashSet<long> BuildProcessedBackupBuildingSet()
        {
            var processed = BuildAssignedBackupBuildingSet();

            foreach (long key in m_NoBackupBuildingKeys)
                processed.Add(key);

            return processed;
        }

        private HashSet<long> BuildAssignedBackupBuildingSet()
        {
            var assigned = m_AssignedBackupScratch;
            assigned.Clear();

            foreach (var backup in
                SystemAPI.Query<RefRO<BackupPower>>()
                .WithNone<Deleted>())
            {
                if (backup.ValueRO.Type == BackupPowerType.None)
                    continue;

                var building = backup.ValueRO.Building;
                if (!building.IsNull)
                    assigned.Add(building.Packed);
            }

            return assigned;
        }

        private BuildingRef[] BuildNoBackupBuildingRefsForSave()
        {
            var realBackupKeys = BuildAssignedBackupBuildingSet();
            var refs = new List<BuildingRef>(m_NoBackupBuildingKeys.Count);
            foreach (long key in m_NoBackupBuildingKeys)
            {
                if (realBackupKeys.Contains(key))
                    continue;

                var buildingRef = BuildingRef.FromPacked(key);
                var entity = buildingRef.ToEntity();
                if (entity == Entity.Null || !EntityManager.Exists(entity))
                    continue;

                refs.Add(buildingRef);
            }

            return refs.ToArray();
        }

        // A building is "known" iff it is in the per-tick processed set (live backup links found by
        // BuildAssignedBackupBuildingSet ∪ no-backup sentinels). The old BackupPowerRef-component
        // probe is gone — that set is exactly the buildings that used to carry the component.
        private static bool IsBackupProcessingKnown(Entity building, HashSet<long> processedBuildingKeys)
            => processedBuildingKeys.Contains(BuildingRef.FromEntity(building).Packed);

        private static void MarkBackupProcessingKnown(Entity building, HashSet<long> processedBuildingKeys)
            => processedBuildingKeys.Add(BuildingRef.FromEntity(building).Packed);

        protected override void OnDestroy()
        {
            Log.Info($"{nameof(BackupPowerDistributionSystem)} destroyed");
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IBackupPowerLinkReader>();
                ServiceRegistry.Instance.Unregister<IBackupPowerLinkWriter>();
            }
            m_LinkMap?.Dispose();
            base.OnDestroy();
        }
    }
}
