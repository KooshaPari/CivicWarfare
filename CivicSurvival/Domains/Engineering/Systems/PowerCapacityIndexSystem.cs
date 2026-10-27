using System;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Engineering.Pipelines.PowerCapacity;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Engineering.Systems
{
    [ActIndependent]
    public partial class PowerCapacityIndexSystem : ThrottledSystemBase, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PowerCapacityIndexSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_FIRST - 1;

        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private IVanillaWriteBarrier? m_VanillaWriteBarrier;
        private PrefabSystem m_PrefabSystem = null!;
        private ModSettings m_Settings = null!;

        private EntityQuery m_PlantProducerQuery;
        private EntityQuery m_OutsideConnectionQuery;
        private EntityQuery m_GridStressQuery;
        private EntityQuery m_UnderConstructionQuery;
        private EntityQuery m_CollapsedProducerQuery;
        private EntityQuery m_DisabledByDisasterQuery;
        private EntityQuery m_EquipmentWearQuery;
        private EntityQuery m_PowerPlantDamageQuery;
        private EntityQuery m_DistrictPowerQuery;
        private EntityQuery m_ExternalPowerInputQuery;
        private EntityQuery m_ShadowExportStateQuery;

        private ComponentLookup<ElectricityProducer> m_ProducerLookup;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<PowerPlantKind> m_PlantKindLookup;
        private ComponentLookup<PowerCapacityIndexState> m_IndexStateLookup;
        // A2: the dirty marker is a runtime side-set keyed by BuildingIdentityKey.Pack —
        // replaces the per-tick structural PowerCapacityIndexDirty tag add/remove on the
        // vanilla plant (that churn invalidated the vanilla render chunk-cache → Burst
        // null-chunk crash). No external readers existed, so the marker has zero presence
        // on the vanilla building. Managed set: touched only on the main thread (throttled
        // sweep), no job/Burst access, so it needs no native allocation or disposal.
        private readonly System.Collections.Generic.HashSet<long> m_DirtySet = new();
        // An unclassified producer (decorative / third-party prefab with no plant capacity) never
        // receives PowerCapacityIndexState, so the !HasComponent<PowerCapacityIndexState> reindex
        // gate would stay true forever and re-scan it (+ open an ECB) every 500 ms sweep. This
        // positive "already classified as unclassified" marker closes that gate after the first
        // sweep; it is cleared if the producer later gains capacity (IndexState appears) or drifts.
        // Managed set, main-thread only — same contract as m_DirtySet.
        private readonly System.Collections.Generic.HashSet<long> m_SeenUnclassified = new();
        private ComponentLookup<GridStressModifier> m_GridStressModifierLookup;
        private ComponentLookup<ConstructionModifier> m_ConstructionModifierLookup;
        private ComponentLookup<EquipmentWearModifier> m_WearModifierLookup;
        private ComponentLookup<OperationalDamageModifier> m_OperationalDamageModifierLookup;
        private ComponentLookup<DisasterDamageModifier> m_DisasterDamageModifierLookup;
        private ComponentLookup<SaturationModifier> m_SaturationModifierLookup;
        private ComponentLookup<ImportCapModifier> m_ImportCapModifierLookup;
        private ComponentLookup<Game.Net.OutsideConnection> m_OutsideConnectionLookup;
        private ComponentLookup<ElectricityBuildingConnection> m_ConnectionLookup;
        private ComponentLookup<ElectricityFlowEdge> m_FlowEdgeLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private BufferLookup<SubObject> m_SubObjectLookup;
        private BufferLookup<InstalledUpgrade> m_InstalledUpgradeLookup;
        private EntityStorageInfoLookup m_EntityStorageInfoLookup;
        private ComponentLookup<PowerPlantData> m_PowerPlantDataLookup;
        private ComponentLookup<EmergencyGeneratorData> m_EmergencyGeneratorDataLookup;
        private ComponentLookup<BatteryData> m_BatteryDataLookup;
        private ComponentLookup<WindPoweredData> m_WindPoweredDataLookup;
        private ComponentLookup<SolarPoweredData> m_SolarPoweredDataLookup;
        private ComponentLookup<GarbagePoweredData> m_GarbagePoweredDataLookup;
        private ComponentLookup<WaterPoweredData> m_WaterPoweredDataLookup;
        private ComponentLookup<Game.Buildings.WaterPowered> m_WaterPoweredLookup;
        private ComponentLookup<GroundWaterPoweredData> m_GroundWaterPoweredDataLookup;
        private ComponentLookup<EquipmentWear> m_EquipmentWearLookup;
        private ComponentLookup<PowerPlantDamage> m_PowerPlantDamageLookup;
        [System.NonSerialized] private EntityCommandBuffer m_ActiveEcb;
        [System.NonSerialized] private bool m_HasActiveEcb;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
#pragma warning disable CIVIC403 // Mod.OnLoad registers ModSettings before SystemRegistrar.RegisterAll; ValidateAfterLoad (post-load, before OnStartRunning) reads it via CreateContext, so it must be resolved in OnCreate.
            m_Settings = ServiceRegistry.Instance.Require<ModSettings>();
#pragma warning restore CIVIC403

            m_PlantProducerQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadWrite<ElectricityProducer>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Net.OutsideConnection>(),
                // Tool preview entities carry Building + ElectricityProducer + PrefabRef but are
                // ephemeral cursor ghosts. Without this exclusion they receive PlantBaseCapacity and
                // then surface downstream as phantom plants (spurious "New plant" construction
                // sidecars in ConstructionDelaySystem, bogus wear rows in EquipmentWearAssignSystem).
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());
            m_OutsideConnectionQuery = GetEntityQuery(
                ComponentType.ReadWrite<ElectricityProducer>(),
                ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                ComponentType.Exclude<Deleted>());
            m_GridStressQuery = GetEntityQuery(ComponentType.ReadOnly<GridStressData>());
            m_UnderConstructionQuery = GetEntityQuery(ComponentType.ReadOnly<UnderConstruction>(), ComponentType.Exclude<Deleted>());
            m_CollapsedProducerQuery = GetEntityQuery(ComponentType.ReadOnly<CollapsedProducer>(), ComponentType.Exclude<Deleted>());
            m_DisabledByDisasterQuery = GetEntityQuery(ComponentType.ReadOnly<DisabledByDisaster>(), ComponentType.Exclude<Deleted>());
            m_EquipmentWearQuery = GetEntityQuery(ComponentType.ReadWrite<EquipmentWear>(), ComponentType.Exclude<Deleted>());
            m_PowerPlantDamageQuery = GetEntityQuery(ComponentType.ReadOnly<PowerPlantDamage>(), ComponentType.Exclude<Deleted>());
            m_DistrictPowerQuery = GetEntityQuery(ComponentType.ReadOnly<DistrictPowerBufferSingleton>());
            m_ExternalPowerInputQuery = GetEntityQuery(ComponentType.ReadOnly<ExternalPowerInput>());
            m_ShadowExportStateQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowExportState>());

            m_ProducerLookup = GetComponentLookup<ElectricityProducer>(false);
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(false);
            m_PlantKindLookup = GetComponentLookup<PowerPlantKind>(false);
            m_IndexStateLookup = GetComponentLookup<PowerCapacityIndexState>(false);
            m_GridStressModifierLookup = GetComponentLookup<GridStressModifier>(false);
            m_ConstructionModifierLookup = GetComponentLookup<ConstructionModifier>(false);
            m_WearModifierLookup = GetComponentLookup<EquipmentWearModifier>(false);
            m_OperationalDamageModifierLookup = GetComponentLookup<OperationalDamageModifier>(false);
            m_DisasterDamageModifierLookup = GetComponentLookup<DisasterDamageModifier>(false);
            m_SaturationModifierLookup = GetComponentLookup<SaturationModifier>(true);
            m_ImportCapModifierLookup = GetComponentLookup<ImportCapModifier>(false);
            m_OutsideConnectionLookup = GetComponentLookup<Game.Net.OutsideConnection>(true);
            m_ConnectionLookup = GetComponentLookup<ElectricityBuildingConnection>(true);
            m_FlowEdgeLookup = GetComponentLookup<ElectricityFlowEdge>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_SubObjectLookup = GetBufferLookup<SubObject>(true);
            m_InstalledUpgradeLookup = GetBufferLookup<InstalledUpgrade>(true);
            m_EntityStorageInfoLookup = GetEntityStorageInfoLookup();
            m_PowerPlantDataLookup = GetComponentLookup<PowerPlantData>(true);
            m_EmergencyGeneratorDataLookup = GetComponentLookup<EmergencyGeneratorData>(true);
            m_BatteryDataLookup = GetComponentLookup<BatteryData>(true);
            m_WindPoweredDataLookup = GetComponentLookup<WindPoweredData>(true);
            m_SolarPoweredDataLookup = GetComponentLookup<SolarPoweredData>(true);
            m_GarbagePoweredDataLookup = GetComponentLookup<GarbagePoweredData>(true);
            m_WaterPoweredDataLookup = GetComponentLookup<WaterPoweredData>(true);
            m_WaterPoweredLookup = GetComponentLookup<Game.Buildings.WaterPowered>(true);
            m_GroundWaterPoweredDataLookup = GetComponentLookup<GroundWaterPoweredData>(true);
            m_EquipmentWearLookup = GetComponentLookup<EquipmentWear>(false);
            m_PowerPlantDamageLookup = GetComponentLookup<PowerPlantDamage>(true);

            Log.Info("Created");
        }

        protected override void OnThrottledUpdate()
        {
            RefreshLookups();
            var ctx = CreateContext();
            try
            {
                EntityCommandBuffer ecb = default;
                int reindexed = ReindexDirtyAndMissing(ref ctx, ecb);
                int dirtied = MarkDrifted(ref ctx);
                if (ctx.HasEcb)
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
                if ((reindexed > 0 || dirtied > 0) && Log.IsDebugEnabled)
                    Log.Debug($"Index pass: reindexed={reindexed}, dirtied={dirtied}");
            }
            finally
            {
                ctx.DisposeCachedResolvedPlantEntities();
            }
        }

        public void ValidateAfterLoad()
        {
            RefreshLookups();
            var ctx = CreateContext();
            try
            {
                int count = ReindexDirtyAndMissingImmediate(ref ctx);
                if (Log.IsDebugEnabled)
                    Log.Debug($"Post-load immediate index: {count} producer(s)");
            }
            finally
            {
                ctx.DisposeCachedResolvedPlantEntities();
            }
        }

        [CompletesDependency("PowerCapacityIndexSystem reindex: throttled 500 ms structural-index sweep materialises producer entities to add missing runtime index/modifier components; steady state reindexed count is near zero")]
        public int ReindexDirtyAndMissing(ref PowerCapacityPipelineContext ctx, EntityCommandBuffer ecb)
        {
            ctx.Ecb = ecb;
            int count = 0;
            try
            {
                using (var entities = m_PlantProducerQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        long key = BuildingIdentityKey.Pack(entity);
                        // PERF-LOCK: an unclassified producer never gains PowerCapacityIndexState, so
                        // without m_SeenUnclassified the !HasComponent gate stays true and re-scans it
                        // (+ opens an ECB) every sweep. The marker makes a producer that has already
                        // been classified-as-unclassified skip until it drifts or gains capacity.
                        bool needsReindex = (!m_IndexStateLookup.HasComponent(entity) && !m_SeenUnclassified.Contains(key))
                            || m_DirtySet.Contains(key);
                        if (!needsReindex)
                            continue;
                        EnsureActiveEcb(ref ctx);
                        if (IndexGridProducer(ref ctx, entity, immediate: false))
                            count++;
                    }
                }

                using (var entities = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        bool needsReindex = !m_IndexStateLookup.HasComponent(entity)
                            || m_DirtySet.Contains(BuildingIdentityKey.Pack(entity));
                        if (!needsReindex)
                            continue;
                        EnsureActiveEcb(ref ctx);
                        var vanillaTicket = ConsumeElectricityProducerTicket();
                        if (IndexOutsideConnection(vanillaTicket, entity, immediate: false))
                            count++;
                    }
                }
            }
            finally
            {
                m_HasActiveEcb = false;
                m_ActiveEcb = default;
            }

            return count;
        }

        public int ReindexDirtyAndMissingImmediate(ref PowerCapacityPipelineContext ctx)
        {
            int count = 0;
            using (var entities = m_PlantProducerQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (IndexGridProducer(ref ctx, entity, immediate: true))
                        count++;
                }
            }

            using (var entities = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp))
            {
                var vanillaTicket = ConsumeElectricityProducerTicket();
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (IndexOutsideConnection(vanillaTicket, entity, immediate: true))
                        count++;
                }
            }

            return count;
        }

        [CompletesDependency("PowerCapacityIndexSystem drift sweep: throttled cheap hash pass over indexed plants; ToEntityArray sync point completes producer writes before the hash comparison")]
        public int MarkDrifted(ref PowerCapacityPipelineContext ctx)
        {
            int count = 0;
            using var entities = m_PlantProducerQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                long key = BuildingIdentityKey.Pack(entity);
                if (!m_IndexStateLookup.TryGetComponent(entity, out var state)
                    || m_DirtySet.Contains(key))
                {
                    // A producer parked as seen-unclassified (no IndexState) may later gain capacity
                    // (e.g. a third-party prefab upgrade). The reindex gate skips it while the marker
                    // stands, so re-classify it cheaply here and drop the marker if it now qualifies —
                    // the next reindex sweep then materialises its index set.
                    if (m_SeenUnclassified.Contains(key) && !m_IndexStateLookup.HasComponent(entity))
                    {
                        PlantClassification recheck = PowerCapacityClassifier.ClassifyPlant(ref ctx, entity);
                        if (!(recheck.Kind == PlantKind.Unclassified && recheck.OriginalCapacityKW <= 0))
                            m_SeenUnclassified.Remove(key);
                    }
                    continue;
                }

                int upgradeHash = PowerCapacityClassifier.ComputeUpgradeHash(ref ctx, entity);
                int hydroShapeHash = PowerCapacityClassifier.ComputeHydroShapeHash(ref ctx, entity);
                if (state.UpgradeHash == upgradeHash && state.HydroShapeHash == hydroShapeHash)
                    continue;

                // A2: mark dirty in the runtime side-set — no structural tag add on the
                // vanilla plant. Picked up by ReindexDirtyAndMissing on the next sweep.
                m_DirtySet.Add(key);
                count++;
            }

            return count;
        }

        public bool IndexGridProducer(ref PowerCapacityPipelineContext ctx, Entity entity, bool immediate)
        {
            if (!m_EntityStorageInfoLookup.Exists(entity))
                return false;

            // B-split: in GameSimulation the first structural add of the index components on
            // a vanilla grid plant is routed to PowerIndexApplySystem (ModificationEnd) via a
            // PowerIndexIntent, so the archetype migration lands in phase with the vanilla
            // render batch pipeline. Only the load-time immediate pass adds directly here.
            if (!immediate)
                return IndexGridProducerDeferred(ref ctx, entity);

            PlantClassification classification = PowerCapacityClassifier.ClassifyPlant(ref ctx, entity);
            if (classification.Kind == PlantKind.Unclassified && classification.OriginalCapacityKW <= 0)
            {
                WriteKind(entity, PlantKind.Unclassified, immediate);
                RemoveDirty(entity);
                // Mark seen so the steady-state reindex gate does not re-scan this producer every
                // sweep (it never gains PowerCapacityIndexState). Symmetric with the deferred path.
                m_SeenUnclassified.Add(BuildingIdentityKey.Pack(entity));
                return false;
            }

            int upgradeHash = PowerCapacityClassifier.ComputeUpgradeHash(ref ctx, entity);
            int hydroShapeHash = PowerCapacityClassifier.ComputeHydroShapeHash(ref ctx, entity);
            Entity prefab = m_PrefabRefLookup.TryGetComponent(entity, out var prefabRef)
                ? prefabRef.m_Prefab
                : Entity.Null;

            // Classified on the immediate pass: ensure no stale unclassified marker survives.
            m_SeenUnclassified.Remove(BuildingIdentityKey.Pack(entity));

            WriteBaseCapacity(entity, classification.OriginalCapacityKW, immediate);
            WriteKind(entity, classification.Kind, immediate);
            WriteGridModifiers(entity, immediate);
            WriteIndexState(entity, new PowerCapacityIndexState
            {
                PrefabIndex = prefab.Index,
                PrefabVersion = prefab.Version,
                UpgradeHash = upgradeHash,
                HydroShapeHash = hydroShapeHash,
                Channel = classification.Channel
            }, immediate);
            RemoveDirty(entity);
            return true;
        }

        /// <summary>
        /// Steady-state (GameSimulation) grid-plant index path. A plant that already has the
        /// full index set is refreshed in place via lookups (no structural change). A new
        /// plant — or one missing any index component — has its first add routed to
        /// PowerIndexApplySystem (ModificationEnd) through a PowerIndexIntent, so the
        /// archetype migration lands in phase with the vanilla render batch pipeline.
        /// </summary>
        public bool IndexGridProducerDeferred(ref PowerCapacityPipelineContext ctx, Entity entity)
        {
            long key = BuildingIdentityKey.Pack(entity);
            PlantClassification classification = PowerCapacityClassifier.ClassifyPlant(ref ctx, entity);
            if (classification.Kind == PlantKind.Unclassified && classification.OriginalCapacityKW <= 0)
            {
                if (!m_PlantKindLookup.HasComponent(entity))
                {
                    // First sight of this producer: emit the Kind-only intent. Not marked seen yet —
                    // the marker is set on the next sweep once Kind exists and the classification is
                    // confirmed stable as unclassified.
                    EmitGridIntent(entity, PlantKind.Unclassified, 0, default, classified: false, constructionPending: false);
                    return false;
                }
                WriteKind(entity, classification.Kind, immediate: false);
                m_DirtySet.Remove(key);
                // Terminal unclassified state: Kind present, no capacity, no IndexState. Mark seen so
                // the reindex gate stops re-scanning this producer every sweep (PERF-LOCK above).
                m_SeenUnclassified.Add(key);
                return false;
            }

            // Classified now (has capacity): drop any stale unclassified marker so the gate treats it
            // normally — a producer can gain capacity later (e.g. a third-party prefab upgrade).
            m_SeenUnclassified.Remove(key);

            int upgradeHash = PowerCapacityClassifier.ComputeUpgradeHash(ref ctx, entity);
            int hydroShapeHash = PowerCapacityClassifier.ComputeHydroShapeHash(ref ctx, entity);
            Entity prefab = m_PrefabRefLookup.TryGetComponent(entity, out var prefabRef)
                ? prefabRef.m_Prefab
                : Entity.Null;
            var state = new PowerCapacityIndexState
            {
                PrefabIndex = prefab.Index,
                PrefabVersion = prefab.Version,
                UpgradeHash = upgradeHash,
                HydroShapeHash = hydroShapeHash,
                Channel = classification.Channel
            };

            // PowerCapacityIndexState is added atomically with the full modifier set (by the
            // consumer's ComponentTypeSet, or the load-time immediate pass), and nothing ever
            // removes an individual modifier, so its absence is the exact "new plant, needs
            // first structural add" marker.
            if (!m_IndexStateLookup.HasComponent(entity))
            {
                EmitGridIntent(entity, classification.Kind, classification.OriginalCapacityKW, state, classified: true, constructionPending: m_Settings.ConstructionDelayEnabled);
                return false;
            }

            // Index components present — refresh data via lookup set (no structural add, stays
            // in GameSimulation). An active ECB is guaranteed by the call site (EnsureActiveEcb
            // before IndexGridProducer); WriteGridModifiers' add branches are unreachable here
            // because every component is present.
            WriteBaseCapacity(entity, classification.OriginalCapacityKW, immediate: false);
            WriteKind(entity, classification.Kind, immediate: false);
            WriteGridModifiers(entity, immediate: false);
            WriteIndexState(entity, state, immediate: false);
            m_DirtySet.Remove(key);
            return true;
        }

        /// <summary>
        /// Producer half of the split: create a PowerIndexIntent entity (legal from
        /// GameSimulation — creating a new entity is not a structural change on the vanilla
        /// plant) carrying the producer-computed values. PowerIndexApplySystem performs the
        /// archetype migration in ModificationEnd. The intent rides the active
        /// GameSimulationEndBarrier ECB established by the call site.
        /// </summary>
        private void EmitGridIntent(Entity entity, PlantKind kind, int capacityKW, PowerCapacityIndexState state, bool classified, bool constructionPending)
        {
            var ecb = RequireActiveEcb();
            var intentEntity = ecb.CreateEntity();
            ecb.AddComponent(intentEntity, new PowerIndexIntent
            {
                Building = BuildingRef.FromEntity(entity),
                Kind = kind,
                OriginalCapacityKW = capacityKW,
                Classified = classified,
                ConstructionPending = constructionPending,
                IndexState = state
            });
        }

        public bool IndexOutsideConnection(VanillaWriteTicket vanillaTicket, Entity entity, bool immediate)
        {
            if (!vanillaTicket.Covers(VanillaWriteComponentMask.ElectricityProducer))
            {
                Log.Error("Outside-connection index requires a VanillaWriteTicket before reading ElectricityProducer; skipping index to avoid racing vanilla producer writes.");
                return false;
            }

            if (!m_ProducerLookup.TryGetComponent(entity, out var producer))
                return false;

            bool seededBaseCapacity = false;
            if (!m_BaseCapacityLookup.TryGetComponent(entity, out var baseCap))
            {
                if (!TrySeedOutsideConnectionBaseCapacity(vanillaTicket, entity, producer, immediate, out baseCap))
                    return false;
                seededBaseCapacity = true;
            }

            int originalCapacity = baseCap.OriginalCapacity;
            if (originalCapacity <= 0)
                return false;

            if (!seededBaseCapacity)
                WriteBaseCapacity(entity, originalCapacity, immediate);
            WriteKind(entity, PlantKind.Outside, immediate);
            WriteImportCap(entity, immediate);
            WriteIndexState(entity, new PowerCapacityIndexState
            {
                PrefabIndex = 0,
                PrefabVersion = 0,
                UpgradeHash = 0,
                HydroShapeHash = 0,
                Channel = CapacityChannel.OutsideConnection
            }, immediate);
            RemoveDirty(entity);
            return true;
        }

        private bool TrySeedOutsideConnectionBaseCapacity(
            VanillaWriteTicket vanillaTicket,
            Entity entity,
            ElectricityProducer producer,
            bool immediate,
            out PlantBaseCapacity baseCap)
        {
            baseCap = default;
            if (!vanillaTicket.Covers(VanillaWriteComponentMask.ElectricityProducer))
            {
                Log.Error("Outside-connection base-capacity seed requires a VanillaWriteTicket; skipping index to avoid racing vanilla producer writes.");
                return false;
            }

            // Outside connections are not grid plants: vanilla stores their import slot
            // capacity directly on ElectricityProducer, so this guarded method may seed
            // PlantBaseCapacity from the live slot capacity.
            int originalCapacity = producer.m_Capacity;
            if (originalCapacity <= 0)
                return false;

            baseCap = new PlantBaseCapacity { OriginalCapacity = originalCapacity };
            WriteBaseCapacity(entity, originalCapacity, immediate);
            return true;
        }

        private VanillaWriteTicket ConsumeElectricityProducerTicket()
        {
            if (m_VanillaWriteBarrier == null)
            {
                EntityManager.CompleteDependencyBeforeRO<ElectricityProducer>();
                return new VanillaWriteTicket(VanillaWriteComponentMask.ElectricityProducer);
            }

            return m_VanillaWriteBarrier.Consume(
                EntityManager,
                GetType(),
                VanillaWriteComponentMask.ElectricityProducer);
        }

        public void WriteBaseCapacity(Entity entity, int originalCapacity, bool immediate)
        {
            if (m_BaseCapacityLookup.TryGetComponent(entity, out var existing))
            {
                if (existing.OriginalCapacity == originalCapacity)
                    return;
                existing.OriginalCapacity = originalCapacity;
                if (immediate)
                {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses lookup/ECB path.
                    EntityManager.SetComponentData(entity, existing);
#pragma warning restore CIVIC051
                }
                else
                    m_BaseCapacityLookup[entity] = existing;
                return;
            }

            var value = new PlantBaseCapacity { OriginalCapacity = originalCapacity };
            if (immediate)
            {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses ECB path.
                EntityManager.AddComponentData(entity, value);
#pragma warning restore CIVIC051
            }
            else
                RequireActiveEcb().AddComponent(entity, value);
        }

        public void WriteKind(Entity entity, PlantKind kind, bool immediate)
        {
            if (m_PlantKindLookup.TryGetComponent(entity, out var existing))
            {
                if (existing.Value == kind)
                    return;
                existing.Value = kind;
                if (immediate)
                {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses lookup path.
                    EntityManager.SetComponentData(entity, existing);
#pragma warning restore CIVIC051
                }
                else
                    m_PlantKindLookup[entity] = existing;
                return;
            }

            var value = new PowerPlantKind { Value = kind };
            if (immediate)
            {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses ECB path.
                EntityManager.AddComponentData(entity, value);
#pragma warning restore CIVIC051
            }
            else
                RequireActiveEcb().AddComponent(entity, value);
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        public void WriteGridModifiers(Entity entity, bool immediate)
        {
            // IsCollapsed is hydrated solely by PowerCapacityResolverSystem.ApplyGridStressModifier
            // from the CollapsedProducer sidecar (the documented authority — see GridStressSystem).
            // This writer only guarantees the modifier component EXISTS with its default (false)
            // zero-state; it never sets IsCollapsed, so it cannot fight the resolver's reconciliation.
            // Construction default is feature-gated: IsUnderConstruction = ConstructionDelayEnabled.
            // Feature on ⇒ a freshly-materialised plant starts under-construction (0 MW) until
            // ConstructionDelaySystem classifies it and the resolver's classification gate
            // opens; feature off ⇒ false ⇒ full MW immediately, independent of CDS/the gate. Do NOT
            // re-default this to an unconditional false — that reopens the new-plant full-MW window.
            if (immediate)
            {
                AddIfMissing(entity, new GridStressModifier { IsCollapsed = false });
                AddIfMissing(entity, new ConstructionModifier { IsUnderConstruction = m_Settings.ConstructionDelayEnabled });
                AddIfMissing(entity, new EquipmentWearModifier { IsUnderRepair = false, ExplosionDamagePercent = 0f });
                AddIfMissing(entity, new OperationalDamageModifier { OperationalDamagePercent = 0f });
                AddIfMissing(entity, new DisasterDamageModifier { DisasterDamagePercent = 0f });
                // Starts at factor 1 (no penalty) until ApplySaturationInertia first runs — mirrors
                // OperationalDamageModifier starting at 0. LastUpdateGameHours seeds 0; the resolver's
                // afterLoad timestamp reconcile (and the first steady tick) bring it to now.
                AddIfMissing(entity, new SaturationModifier { SaturationFactor = 1f, LastUpdateGameHours = 0.0 });
                return;
            }

            var ecb = RequireActiveEcb();
            if (!m_GridStressModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new GridStressModifier { IsCollapsed = false });

            if (!m_ConstructionModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new ConstructionModifier { IsUnderConstruction = m_Settings.ConstructionDelayEnabled });
            if (!m_WearModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new EquipmentWearModifier { IsUnderRepair = false, ExplosionDamagePercent = 0f });
            if (!m_OperationalDamageModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new OperationalDamageModifier { OperationalDamagePercent = 0f });
            if (!m_DisasterDamageModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new DisasterDamageModifier { DisasterDamagePercent = 0f });
            if (!m_SaturationModifierLookup.HasComponent(entity))
                ecb.AddComponent(entity, new SaturationModifier { SaturationFactor = 1f, LastUpdateGameHours = 0.0 });
        }

        [PowerCapacityPipelinePhase(PowerCapacityPipelinePhase.ClassifyAndEnsureModifiers)]
        public void WriteImportCap(Entity entity, bool immediate)
        {
            if (!ImportCapRuntimeState.HasPublishedImportCap)
                return;

            int importCapKW = ImportCapRuntimeState.CurrentImportCapKW;
            var value = new ImportCapModifier { ImportCapLimitKW = importCapKW };
            if (immediate)
            {
                AddOrSet(entity, value);
                return;
            }

            if (m_ImportCapModifierLookup.TryGetComponent(entity, out var existing))
            {
                if (existing.ImportCapLimitKW != importCapKW)
                {
                    existing.ImportCapLimitKW = importCapKW;
                    m_ImportCapModifierLookup[entity] = existing;
                }
            }
            else
            {
                RequireActiveEcb().AddComponent(entity, value);
            }
        }

        public void WriteIndexState(Entity entity, PowerCapacityIndexState state, bool immediate)
        {
            if (m_IndexStateLookup.HasComponent(entity))
            {
                if (immediate)
                {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses lookup/ECB path.
                    EntityManager.SetComponentData(entity, state);
#pragma warning restore CIVIC051
                }
                else
                {
                    m_IndexStateLookup[entity] = state;
                }
                return;
            }

            if (immediate)
            {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses ECB path.
                EntityManager.AddComponentData(entity, state);
#pragma warning restore CIVIC051
            }
            else
            {
                RequireActiveEcb().AddComponent(entity, state);
            }
        }

        public void RemoveDirty(Entity entity)
        {
            // A2: clearing the dirty marker is a runtime side-set removal — no structural
            // change on the vanilla plant in any phase.
            m_DirtySet.Remove(BuildingIdentityKey.Pack(entity));
        }

        public EntityCommandBuffer RequireActiveEcb()
        {
            if (!m_HasActiveEcb)
                throw new InvalidOperationException("PowerCapacityIndexSystem requires an active ECB for deferred structural writes.");
            return m_ActiveEcb;
        }

        private void EnsureActiveEcb(ref PowerCapacityPipelineContext ctx)
        {
            ctx.EnsureEcb();
            m_ActiveEcb = ctx.Ecb;
            m_HasActiveEcb = true;
        }

        public void AddIfMissing<T>(Entity entity, T value)
            where T : unmanaged, IComponentData
        {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write helper.
            if (!EntityManager.HasComponent<T>(entity))
                EntityManager.AddComponentData(entity, value);
#pragma warning restore CIVIC051
        }

        public void AddOrSet<T>(Entity entity, T value)
            where T : unmanaged, IComponentData
        {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write helper.
            if (EntityManager.HasComponent<T>(entity))
                EntityManager.SetComponentData(entity, value);
            else
                EntityManager.AddComponentData(entity, value);
#pragma warning restore CIVIC051
        }

        private void RefreshLookups()
        {
            m_ProducerLookup.Update(this);
            m_BaseCapacityLookup.Update(this);
            m_PlantKindLookup.Update(this);
            m_IndexStateLookup.Update(this);
            m_GridStressModifierLookup.Update(this);
            m_ConstructionModifierLookup.Update(this);
            m_WearModifierLookup.Update(this);
            m_OperationalDamageModifierLookup.Update(this);
            m_DisasterDamageModifierLookup.Update(this);
            m_SaturationModifierLookup.Update(this);
            m_ImportCapModifierLookup.Update(this);
            m_OutsideConnectionLookup.Update(this);
            m_ConnectionLookup.Update(this);
            m_FlowEdgeLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_SubObjectLookup.Update(this);
            m_InstalledUpgradeLookup.Update(this);
            m_EntityStorageInfoLookup.Update(this);
            m_PowerPlantDataLookup.Update(this);
            m_EmergencyGeneratorDataLookup.Update(this);
            m_BatteryDataLookup.Update(this);
            m_WindPoweredDataLookup.Update(this);
            m_SolarPoweredDataLookup.Update(this);
            m_GarbagePoweredDataLookup.Update(this);
            m_WaterPoweredDataLookup.Update(this);
            m_WaterPoweredLookup.Update(this);
            m_GroundWaterPoweredDataLookup.Update(this);
            m_EquipmentWearLookup.Update(this);
            m_PowerPlantDamageLookup.Update(this);
        }

        private void ResolveVanillaWriteBarrier()
        {
            m_VanillaWriteBarrier ??= ServiceRegistry.Instance.Require<IVanillaWriteBarrier>();
        }

        private PowerCapacityPipelineContext CreateContext()
        {
            // Keep the lazy service resolve the retired context field used to trigger:
            // ConsumeElectricityProducerTicket's null fallback stays a hot-reload safety
            // net, not the steady-state path.
            ResolveVanillaWriteBarrier();
            return new PowerCapacityPipelineContext
            {
                EntityManager = EntityManager,
                EcbFactory = m_GameSimulationEndBarrier.CreateCommandBuffer,
                HasEcb = false,
                IsSafeFrame = true,
                ConstructionDelayEnabled = m_Settings.ConstructionDelayEnabled,
                PrefabSystem = m_PrefabSystem,
                PlantProducerQuery = m_PlantProducerQuery,
                OutsideConnectionQuery = m_OutsideConnectionQuery,
                ResolvedPlantQuery = m_PlantProducerQuery,
                GridStressQuery = m_GridStressQuery,
                UnderConstructionQuery = m_UnderConstructionQuery,
                DisabledByDisasterQuery = m_DisabledByDisasterQuery,
                EquipmentWearQuery = m_EquipmentWearQuery,
                PowerPlantDamageQuery = m_PowerPlantDamageQuery,
                CollapsedProducerQuery = m_CollapsedProducerQuery,
                DistrictPowerQuery = m_DistrictPowerQuery,
                ExternalPowerInputQuery = m_ExternalPowerInputQuery,
                ShadowExportStateQuery = m_ShadowExportStateQuery,
                ProducerLookup = m_ProducerLookup,
                BaseCapacityLookup = m_BaseCapacityLookup,
                PlantKindLookup = m_PlantKindLookup,
                GridStressModifierLookup = m_GridStressModifierLookup,
                ConstructionModifierLookup = m_ConstructionModifierLookup,
                WearModifierLookup = m_WearModifierLookup,
                OperationalDamageModifierLookup = m_OperationalDamageModifierLookup,
                DisasterDamageModifierLookup = m_DisasterDamageModifierLookup,
                ImportCapModifierLookup = m_ImportCapModifierLookup,
                OutsideConnectionLookup = m_OutsideConnectionLookup,
                ConnectionLookup = m_ConnectionLookup,
                FlowEdgeLookup = m_FlowEdgeLookup,
                PrefabRefLookup = m_PrefabRefLookup,
                SubObjectLookup = m_SubObjectLookup,
                InstalledUpgradeLookup = m_InstalledUpgradeLookup,
                EntityStorageInfoLookup = m_EntityStorageInfoLookup,
                PowerPlantDataLookup = m_PowerPlantDataLookup,
                EmergencyGeneratorDataLookup = m_EmergencyGeneratorDataLookup,
                BatteryDataLookup = m_BatteryDataLookup,
                WindPoweredDataLookup = m_WindPoweredDataLookup,
                SolarPoweredDataLookup = m_SolarPoweredDataLookup,
                GarbagePoweredDataLookup = m_GarbagePoweredDataLookup,
                WaterPoweredDataLookup = m_WaterPoweredDataLookup,
                WaterPoweredLookup = m_WaterPoweredLookup,
                GroundWaterPoweredDataLookup = m_GroundWaterPoweredDataLookup,
                EquipmentWearLookup = m_EquipmentWearLookup,
                PowerPlantDamageLookup = m_PowerPlantDamageLookup
            };
        }
    }
}
