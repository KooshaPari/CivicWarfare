using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Collections;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Assigns EquipmentWear sidecar entities to pipeline-classified electricity producers.
    /// Separated from PlantWearSimulation: independent concern, own query, no shared async state.
    /// Creates SEPARATE mod entity (not on vanilla building) to avoid archetype changes.
    /// </summary>
    [ActIndependent]
    public partial class EquipmentWearAssignSystem : ThrottledSystemBase, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("EquipmentWearAssignSystem");

        protected override int UpdateInterval => BalanceConfig.Current.EquipmentWear.UpdateIntervalFrames;

        private EntityQuery m_NewProducerQuery;
        private EntityQuery m_EquipmentWearQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private PlantWearSimulation m_WearSim = null!;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentTypeHandle<EquipmentWear> m_EquipmentWearTypeHandle;
        private ComponentTypeHandle<PrefabRef> m_PrefabRefTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;

        // Persistent Index→StablePlantId registry (prefab-guarded). Survives the disappearance of an
        // EquipmentWear sidecar (the orphan cleanup destroys sidecars on a grid-rebuild liveness blip),
        // so when a sidecar must be re-created the SAME plant reuses its original id instead of minting
        // a fresh one — that runaway re-mint was the StablePlantId avalanche after a grid-node loss.
        // Transient (Entity.Index is not portable across load): re-seeded from the persisted sidecars
        // in ValidateAfterLoad before the assign pass.
        private StablePlantIdentityRegistry m_PlantRegistry;

        protected override void OnCreate()
        {
            base.OnCreate();
            Log.Info("Created");

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            // NOTE: Can't use ComponentType.Exclude<EquipmentWear> since it's on separate mod entity.
            // Mirrors the inline SystemAPI.Query filters in OnThrottledUpdate — used only for IsEmpty early-out.
            m_NewProducerQuery = GetEntityQuery(
                ComponentType.ReadOnly<ElectricityProducer>(),
                ComponentType.ReadOnly<PlantBaseCapacity>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Game.Net.OutsideConnection>(),
                ComponentType.Exclude<Deleted>()
            );
            m_EquipmentWearQuery = GetEntityQuery(
                ComponentType.ReadOnly<EquipmentWear>(),
                ComponentType.Exclude<Deleted>()
            );
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_EquipmentWearTypeHandle = GetComponentTypeHandle<EquipmentWear>(true);
            m_PrefabRefTypeHandle = GetComponentTypeHandle<PrefabRef>(true);
            m_EntityTypeHandle = GetEntityTypeHandle();

            m_PlantRegistry = new StablePlantIdentityRegistry(64, Allocator.Persistent);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_WearSim ??= FeatureRegistry.Instance.Require<PlantWearSimulation>();
        }

        protected override void OnThrottledUpdate()
        {
            // Steady-state: structural changes go through the GameSimulationEndBarrier ECB.
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            ProcessProducers((in EquipmentWear wear) =>
            {
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                var wearEntity = ecb.CreateEntity();
                ecb.AddComponent(wearEntity, wear);
            });
        }

        /// <summary>
        /// Sink for a freshly-formed EquipmentWear sidecar. Two implementations — ECB (steady-state)
        /// and EntityManager (afterLoad seed) — keep the structural write physically separate so the
        /// EntityManager path is reachable only from <see cref="ValidateAfterLoad"/>, never OnUpdate.
        /// </summary>
        private delegate void SidecarSink(in EquipmentWear wear);

        /// <summary>
        /// Shared producer scan: builds frame-local occupancy of live sidecars, then for each producer
        /// without a live sidecar decides reuse-vs-new against the persistent <see cref="m_PlantRegistry"/>
        /// and hands the formed sidecar to <paramref name="sink"/>. Identity is the stable building Index
        /// (prefab-guarded). A re-created sidecar reuses the plant's retained StablePlantId; only a
        /// genuinely new (or slot-reused) plant mints one via AllocateNextPlantId.
        /// </summary>
        private void ProcessProducers(SidecarSink sink)
        {
            if (m_NewProducerQuery.IsEmpty)
                return;

            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider == null)
                return;

            float gameHour = timeProvider.Current.TotalGameHours;
            if (float.IsNaN(gameHour) || float.IsInfinity(gameHour) || gameHour < 0f)
                return;

            // Frame-local occupancy: which Index slots carry a LIVE EquipmentWear sidecar right now.
            var liveSidecarIndices = new NativeHashSet<int>(64, Allocator.Temp);
            var currentProducerIndices = new NativeHashSet<int>(64, Allocator.Temp);
            try
            {
                m_EquipmentWearTypeHandle.Update(this);
                using (var wearChunks = m_EquipmentWearQuery.ToArchetypeChunkArray(Allocator.Temp))
                {
                    for (int ci = 0; ci < wearChunks.Length; ci++)
                    {
                        var wears = wearChunks[ci].GetNativeArray(ref m_EquipmentWearTypeHandle);
                        for (int i = 0; i < wearChunks[ci].Count; i++)
                            liveSidecarIndices.Add(wears[i].Building.Index);
                    }
                }

                m_BaseCapacityLookup.Update(this);
                m_PrefabRefTypeHandle.Update(this);
                m_EntityTypeHandle.Update(this);

                int assigned = 0;
                using var producerChunks = m_NewProducerQuery.ToArchetypeChunkArray(Allocator.Temp);
                for (int ci = 0; ci < producerChunks.Length; ci++)
                {
                    var entities = producerChunks[ci].GetNativeArray(m_EntityTypeHandle);
                    var prefabRefs = producerChunks[ci].GetNativeArray(ref m_PrefabRefTypeHandle);
                    for (int i = 0; i < producerChunks[ci].Count; i++)
                    {
                        var entity = entities[i];
                        currentProducerIndices.Add(entity.Index);

                        // Register/classify the live producer in the persistent registry (prefab guard).
                        // ReusedSlot overwrites a demolished plant's record so it cannot inherit its id.
                        var identity = m_PlantRegistry.ClassifyAndRegister(entity, prefabRefs[i].m_Prefab);

                        // A live sidecar already exists for this slot ⇒ nothing to assign.
                        if (liveSidecarIndices.Contains(entity.Index))
                            continue;

                        if (!m_BaseCapacityLookup.TryGetComponent(entity, out var baseCapacity)
                            || baseCapacity.OriginalCapacity <= 0)
                            continue;

                        var wear = EquipmentWear.CreateDefault();
                        wear.Building = BuildingRef.FromEntity(entity);
                        wear.LastMaintenanceHour = gameHour;
                        wear.OriginalCapacity = baseCapacity.OriginalCapacity;

                        // Reuse the retained id when this is the same plant whose sidecar was destroyed
                        // (Known + an id was previously minted). Mint a new id only for a genuinely new
                        // plant or a slot reused by a different building.
                        if (identity == PlantIdentityClass.Known
                            && m_PlantRegistry.TryGetStablePlantId(entity, out int retainedId))
                        {
                            wear.StablePlantId = retainedId;
                        }
                        else
                        {
                            wear.StablePlantId = m_WearSim.AllocateNextPlantId();
                            m_PlantRegistry.SetStablePlantId(entity, wear.StablePlantId);
                        }

                        sink(in wear);
                        assigned++;
                        if (Log.IsDebugEnabled) Log.Debug($"Assigned StablePlantId {wear.StablePlantId} to power plant (mod entity)");
                    }
                }

                // Prune confirmed demolitions: a registry entry whose Index is absent from the live
                // producer query was demolished. Drop it so a reused Index slot does not inherit the
                // old plant's retained id. A churned-but-live plant keeps its Index, so it stays.
                PruneDemolishedPlants(currentProducerIndices);

                if (assigned > 0 && Log.IsDebugEnabled)
                    Log.Debug($"Assigned {assigned} EquipmentWear sidecar(s) this pass");
            }
            finally
            {
                if (liveSidecarIndices.IsCreated) liveSidecarIndices.Dispose();
                if (currentProducerIndices.IsCreated) currentProducerIndices.Dispose();
            }
        }

        private void PruneDemolishedPlants(NativeHashSet<int> liveProducerIndices)
        {
            using var trackedIndices = m_PlantRegistry.GetTrackedIndices(Allocator.Temp);
            for (int i = 0; i < trackedIndices.Length; i++)
            {
                int idx = trackedIndices[i];
                if (!liveProducerIndices.Contains(idx))
                    m_PlantRegistry.Remove(idx);
            }
        }

        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_WEAR + 1;

        // Vanilla save (or one upgraded mid-session) ships zero EquipmentWear sidecars,
        // and the steady-state assign runs on the EquipmentWear throttle (UpdateIntervalFrames=120
        // → ~2 minutes at 60 FPS). Until the first throttle tick fires, every reader gates on
        // StablePlantId != 0 and the InfrastructureContent panel shows "0 plants / 0 MW" even
        // though Production already aggregates the real plants. Mirror the immediate-seed pattern
        // PowerCapacityIndexSystem.ValidateAfterLoad uses: scan producers once with EntityManager
        // direct writes so the UI snapshot has stable plant ids by the first publish frame.
        public void ValidateAfterLoad()
        {
            m_WearSim ??= FeatureRegistry.Instance.Require<PlantWearSimulation>();
            ReseedRegistryFromSidecars();
            SeedSidecarsImmediate();
        }

        /// <summary>
        /// Re-populate the transient persistent registry from the persisted EquipmentWear sidecars so
        /// post-load assignment reuses the saved StablePlantId for each slot instead of re-minting.
        /// The sidecar carries the persisted id; the building prefab is resolved from the live building.
        /// </summary>
        private void ReseedRegistryFromSidecars()
        {
            m_PlantRegistry.Clear();
            if (m_EquipmentWearQuery.IsEmpty)
                return;

            m_PrefabRefLookup.Update(this);
            foreach (var wearRef in SystemAPI.Query<RefRO<EquipmentWear>>().WithNone<Deleted>())
            {
                var wear = wearRef.ValueRO;
                if (wear.Building.IsNull)
                    continue;
                Entity building = wear.Building.ToEntity();
                Entity prefab = m_PrefabRefLookup.TryGetComponent(building, out var prefabRef)
                    ? prefabRef.m_Prefab
                    : Entity.Null;
                m_PlantRegistry.SeedFromSidecar(building, prefab, wear.StablePlantId);
            }
        }

        /// <summary>
        /// afterLoad seed: write missing sidecars immediately via EntityManager so the first UI publish
        /// frame already has stable plant ids. Reachable ONLY from <see cref="ValidateAfterLoad"/> — not
        /// from any OnUpdate path — so it is not a steady-state structural change (the steady-state path
        /// uses the ECB sink in OnThrottledUpdate).
        /// </summary>
        private void SeedSidecarsImmediate()
        {
            ProcessProducers((in EquipmentWear wear) =>
            {
#pragma warning disable CIVIC051 // ValidateAfterLoad-only immediate write; steady-state uses the ECB sink.
                var wearEntity = EntityManager.CreateEntity();
                EntityManager.AddComponentData(wearEntity, wear);
#pragma warning restore CIVIC051
            });
        }

        protected override void OnDestroy()
        {
            m_PlantRegistry.Dispose();
            base.OnDestroy();
        }

    }
}
