using System;
using Game.Buildings;
using Game.Common;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Engineering.Services;
using Unity.Mathematics;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Pause-safe plant-repair intake. Converts transient UI requests into
    /// durable RepairTransactionIntent entities in ModificationEnd.
    /// </summary>
    [ActIndependent]
    [HandlesRequestKind(RequestKind.PlantRepair)]
    [TransientConsumerReconcile(typeof(PlantRepairRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Transient UI command: durable RepairTransactionIntent is created by this ModificationEnd consumer, so pre-consume load loss is reissuable.")]
    public partial class PlantRepairIntakeSystem : CivicSystemBase, IPlantRepairScheduler, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PlantRepairIntakeSystem");

        private EntityQuery m_RequestQuery;
        private EntityQuery m_WaveStateQuery;
        private EntityQuery m_IntentScanQuery;
        private ModificationEndBarrier m_ModificationEndBarrier = null!;
        private ComponentLookup<EquipmentWear> m_EquipmentWearLookup;
        private ComponentLookup<ElectricityProducer> m_ElectricityProducerLookup;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        [System.NonSerialized] private CivicServiceLookups m_RepairLookups = null!;
#pragma warning disable CIVIC241, CIVIC312 // Ephemeral id cursor; AllocateIntentId rescans surviving intents before issuing a new id.
        [System.NonSerialized] private int m_NextIntentId = 1;
#pragma warning restore CIVIC241, CIVIC312

#pragma warning disable CIVIC229 // System references — this system calls owner APIs, it does not own their state.
        [System.NonSerialized] private PlantWearSimulation m_WearSim = null!;
        [System.NonSerialized] private PlantRepairRequestProcessor m_Processor = null!;
#pragma warning restore CIVIC229
        private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;
        private IShadowWalletService m_WalletService = NullShadowWalletService.Instance;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ModificationEndBarrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_RequestQuery = GetEntityQuery(ComponentType.ReadOnly<PlantRepairRequest>());
            m_IntentScanQuery = GetEntityQuery(ComponentType.ReadOnly<RepairTransactionIntent>());
            m_WaveStateQuery = GetEntityQuery(ComponentType.ReadOnly<WaveStateSingleton>());
            m_EquipmentWearLookup = GetComponentLookup<EquipmentWear>(false);
            m_ElectricityProducerLookup = GetComponentLookup<ElectricityProducer>(true);
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_RepairLookups = new CivicServiceLookups(() =>
            {
                m_EquipmentWearLookup.Update(this);
                m_ElectricityProducerLookup.Update(this);
                m_BaseCapacityLookup.Update(this);
                m_StorageInfoLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
            });

            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IPlantRepairScheduler>(this);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_WearSim ??= FeatureRegistry.Instance.Require<PlantWearSimulation>();
            m_Processor ??= FeatureRegistry.Instance.Require<PlantRepairRequestProcessor>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
        }

        protected override void OnUpdateImpl()
        {
            if (m_RequestQuery.IsEmpty)
                return;

            m_WearSim ??= FeatureRegistry.Instance.Require<PlantWearSimulation>();
            m_Processor ??= FeatureRegistry.Instance.Require<PlantRepairRequestProcessor>();
            m_RepairLookups.RefreshIfStale();
            m_WearSim.RefreshPlantIdMap();

            // Request entity is destroyed synchronously via EntityManager so duplicate
            // ticks at >1x sim speed see an empty query (no per-frame dedup state needed).
            // The ModificationEndBarrier ECB stays for the *intent* entity creation and
            // result emission (intent flows into PlantRepairCommitSystem on a later tick).
            // Audit-verified: no scheduled jobs read PlantRepairRequest, so the sync
            // destroy creates no sync point.
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<PlantRepairRequest>>()
                .WithEntityAccess())
            {
                if (!ecbCreated)
                {
                    ecb = m_ModificationEndBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }

                bool hasMeta = SystemAPI.HasComponent<RequestMeta>(entity);
                var meta = hasMeta ? SystemAPI.GetComponent<RequestMeta>(entity) : default;
                var status = TryCreateIntent(
                    ecb,
                    request.ValueRO.StablePlantId,
                    request.ValueRO.RepairType,
                    meta,
                    out var reasonId);

                if (status == RequestStatus.Failed && hasMeta)
                    EmitFailure(ecb, meta.RequestId, ReasonId.FromRuntime(reasonId));

#pragma warning disable CIVIC006, CIVIC208 // Single-shot UI command consumer: no scheduled jobs read PlantRepairRequest (audit-verified), so synchronous destroy creates no sync point.
                EntityManager.DestroyEntity(entity);
#pragma warning restore CIVIC006, CIVIC208
            }

            if (ecbCreated)
                m_ModificationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        (RequestStatus status, string reasonId) IPlantRepairScheduler.ScheduleRepair(
            int stablePlantId,
            RepairType repairType,
            RequestMeta requestMeta)
        {
            m_WearSim ??= FeatureRegistry.Instance.Require<PlantWearSimulation>();
            m_Processor ??= FeatureRegistry.Instance.Require<PlantRepairRequestProcessor>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            m_RepairLookups.RefreshIfStale();
            m_WearSim.RefreshPlantIdMap();
            var modEndEcb = m_ModificationEndBarrier.CreateCommandBuffer();
            var status = TryCreateIntent(modEndEcb, stablePlantId, repairType, requestMeta, out var reasonId);
            return (status, reasonId);
        }

        private RequestStatus TryCreateIntent(
            EntityCommandBuffer ecb,
            int plantId,
            RepairType repairType,
            RequestMeta requestMeta,
            out string reasonId)
        {
            var ctx = BuildRepairContext(ecb);
            var (targetEntity, wear) = PlantRepairService.FindPlantByStableId(ref ctx, plantId);
            bool foundPlant = targetEntity != Entity.Null;
            bool canApplyRepairState = foundPlant && CanApplyRepairState(wear.GetBuildingEntity());
            int billableRepairPercent = foundPlant ? GetBillableRepairPercent(wear) : 0;
            bool hasPendingRepair = ((IPlantRepairIntentReader)m_Processor).HasPendingRepairIntent(plantId);

            if (!PlantRepairEligibility.CanRepairPlant(
                    ctx.CurrentPhase,
                    hasPendingRepair,
                    foundPlant,
                    canApplyRepairState,
                    foundPlant && wear.IsUnderRepair,
                    billableRepairPercent,
                    repairType,
                    World,
                    out reasonId))
            {
                if (!foundPlant)
                    Log.Warn($"Repair rejected for plant {plantId}: {reasonId} | {PlantRepairService.DiagnoseResolve(ref ctx, plantId)}");
                else
                    Log.Warn($"Repair rejected for plant {plantId}: {reasonId}");
                return RequestStatus.Failed;
            }

            var repairParams = RepairPaymentHelper.CalculateRepairParams(billableRepairPercent, repairType);
            int kickback = ResolveKickback(repairType, repairParams.Kickback, plantId);
            int intentId = AllocateIntentId();
            var intentEntity = ecb.CreateEntity();
            ecb.AddComponent(intentEntity, new RepairTransactionIntent
            {
                IntentId = intentId,
                PlantId = plantId,
                Building = wear.Building,
                RepairTypeByte = (byte)repairType,
                Cost = repairParams.Cost,
                KickbackAmount = kickback,
                DurationHours = repairParams.DurationHours,
                RequestId = requestMeta.RequestId
            });

            m_Processor.MarkPending(plantId);
            Log.Info($"Intent created plantId={plantId}, intentId={intentId}, cost={repairParams.Cost}");
            reasonId = "";
            return RequestStatus.Pending;
        }

        private PlantRepairContext BuildRepairContext(EntityCommandBuffer ecb)
        {
            var wavePhase = (m_WaveStateQuery.TryGetSingleton<WaveStateSingleton>(out var ws)
                    ? ws : WaveStateSingleton.Default)
                .CurrentPhase;

            double gameHour = GameTimeSystem.Instance != null
                ? GameTimeSystem.Instance.Current.TotalGameHours
                : 0f;

            return new PlantRepairContext
            {
                GameHour = (float)gameHour,
                World = World,
                EventBus = EventBus,
                PlantIdToEntity = m_WearSim.PlantIdToEntity,
                WearLookup = m_EquipmentWearLookup,
                ProducerLookup = m_ElectricityProducerLookup,
                BaseCapacityLookup = m_BaseCapacityLookup,
                StorageInfoLookup = m_StorageInfoLookup,
                DeletedLookup = m_DeletedLookup,
                DestroyedLookup = m_DestroyedLookup,
                PowerCapacitySnapshotReader = m_PowerCapacitySnapshotReader,
                Ecb = ecb,
                CurrentPhase = wavePhase
            };
        }

        private bool CanApplyRepairState(Entity buildingEntity)
        {
            if (!IsLiveBuilding(buildingEntity))
                return false;
            return m_BaseCapacityLookup.HasComponent(buildingEntity)
                || m_ElectricityProducerLookup.HasComponent(buildingEntity);
        }

        private int GetBillableRepairPercent(EquipmentWear wear)
        {
            float explosionPercent = wear.HasExploded ? wear.SavedExplosionDamage : 0f;
            float operationalPercent = 0f;
            float disasterPercent = 0f;
            if (TryGetCapacitySnapshot(wear.GetBuildingEntity(), out var snapshot))
            {
                explosionPercent = math.max(explosionPercent, snapshot.ExplosionDamagePercent);
                operationalPercent = snapshot.OperationalDamagePercent;
                disasterPercent = snapshot.DisasterDamagePercent;
            }
            return RepairPaymentHelper.BillableRepairPercent(
                wear.WearPercent, explosionPercent, operationalPercent, disasterPercent);
        }

        private bool TryGetCapacitySnapshot(Entity buildingEntity, out PowerCapacityPlantSnapshot plantSnapshot)
        {
            plantSnapshot = default;
            if (m_PowerCapacitySnapshotReader == null
                || !m_PowerCapacitySnapshotReader.TryGetSnapshot(out var snapshot))
            {
                return false;
            }

            var plants = snapshot.Plants;
            for (int i = 0; i < plants.Count; i++)
            {
                if (plants[i].Plant == buildingEntity)
                {
                    plantSnapshot = plants[i];
                    return true;
                }
            }

            return false;
        }

        private int ResolveKickback(RepairType repairType, int kickback, int plantId)
        {
            if (repairType != RepairType.MunicipalWithKickback || kickback <= 0)
                return 0;
            if (!m_WalletService.HasWallet || !m_WalletService.IsOperational || m_WalletService.IsFrozen)
            {
                Log.Info($"Kickback blocked - shadow wallet unavailable (plant {plantId})");
                return 0;
            }
            return kickback;
        }

        private bool IsLiveBuilding(Entity buildingEntity)
        {
            if (buildingEntity == Entity.Null || !m_StorageInfoLookup.Exists(buildingEntity))
                return false;
            return !m_DeletedLookup.HasComponent(buildingEntity)
                && !m_DestroyedLookup.HasComponent(buildingEntity);
        }

        /// <summary>
        /// Reseed the ephemeral id cursor past any intent that survived load. Runs post-load (not
        /// OnUpdate), so the scan uses a context-free cached query — AllocateIntentId can then issue
        /// ids without rescanning, which kept it reachable from both the sync and OnUpdate paths.
        /// </summary>
        public void ValidateAfterLoad()
        {
            using var intents = m_IntentScanQuery.ToComponentDataArray<RepairTransactionIntent>(Allocator.Temp);
            int maxExisting = 0;
            for (int i = 0; i < intents.Length; i++)
                maxExisting = Math.Max(maxExisting, intents[i].IntentId);

            if (maxExisting == int.MaxValue)
            {
                m_NextIntentId = int.MaxValue;
                Log.Error("Plant repair intent id space exhausted after load; using saturated id");
                return;
            }

            if (m_NextIntentId <= maxExisting)
                m_NextIntentId = maxExisting + 1;
            if (m_NextIntentId <= 0)
                m_NextIntentId = 1;
        }

        private int AllocateIntentId()
        {
            if (m_NextIntentId <= 0)
                m_NextIntentId = 1;

            if (m_NextIntentId == int.MaxValue)
            {
                Log.Error("Plant repair intent id space exhausted; using saturated id");
                return int.MaxValue;
            }

            return m_NextIntentId++;
        }

        private void EmitFailure(EntityCommandBuffer ecb, int requestId, ReasonId reasonId)
        {
            if (requestId <= 0)
                return;
            var resultEntity = RequestResultEmitter.Emit(
                ecb,
                requestId,
                RequestKind.PlantRepair,
                RequestStatus.Failed,
                reasonId,
                SystemAPI.Time.ElapsedTime);
            ecb.AddComponent<Reported>(resultEntity);
            RequestResultBridge.PublishTerminalForBegun(
                RequestResultBridge.PlantRepair,
                requestId,
                RequestStatus.Failed,
                reasonId.ToString());
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IPlantRepairScheduler>(this);
            base.OnDestroy();
        }
    }
}
