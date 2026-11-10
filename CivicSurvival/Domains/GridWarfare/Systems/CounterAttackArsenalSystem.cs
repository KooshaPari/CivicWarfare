using System;
using System.Threading;
using Colossal.Serialization.Entities;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.GridWarfare;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.GridWarfare;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.GridWarfare.Events;

namespace CivicSurvival.Domains.GridWarfare.Systems
{
    /// <summary>
    /// Owns the <see cref="CounterAttackArsenal"/> singleton — the player's stock of
    /// outbound drones and ballistic rockets. Two responsibilities:
    ///
    /// 1. <see cref="ICounterAttackArsenalService"/> — the cross-domain spend/replenish
    ///    surface. Spend is synchronous (main-thread state write) so the launch gate
    ///    can decide pause-safely on the UI/sync path. Stock never goes
    ///    negative.
    ///
    /// 2. Paid procurement pipeline — drains retained budget results for
    ///    <see cref="ArsenalProcurementBatchIntent"/> batches and grants stock on
    ///    success, mirroring <c>AAResupplyPipelineSystem</c>. The shadow-import channel
    ///    routes its budget through <c>BudgetCategory.ShadowOps</c> so SanctionsMarkup
    ///    and the shadow-wallet pending reservation are applied inside
    ///    <c>BudgetEmitter</c> — no direct wallet access here.
    ///
    /// State-bearing singleton → persisted through <see cref="CounterAttackArsenalCodec"/>
    /// (NOT IEmptySerializable; dropping stock on load would lose every purchased
    /// munition). Restore follows the MobilizationSystem owner pattern (Deserialize →
    /// buffer; OnLoadRestore recreates the singleton; ValidateAfterLoad writes the
    /// restored stock onto it).
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(CounterAttackArsenal))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class CounterAttackArsenalSystem : CivicSystemBase,
        ICivicSingletonOwner<CounterAttackArsenal>,
        ICounterAttackArsenalService,
        IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CounterAttackArsenal");


        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private EntityQuery m_ArsenalQuery;
        private EntityQuery m_BatchQuery;
        private EntityQuery m_ResolvedRequestQuery;
        private EntityQuery m_ResolvedRefundQuery;
        private EntityQuery m_BudgetRequestQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ComponentLookup<CounterAttackArsenal> m_ArsenalLookup;
        // Service-boundary lookup refresh (CIVIC433): sync spend/replenish refresh the
        // full lookup set through this bundle, never a partial hand .Update(this).
        [System.NonSerialized] private CivicServiceLookups m_ServiceLookups = null!;

        // Monotonic procurement batch id. Persisted through the codec (alongside the
        // stock) so it stays session-stable across save/load — without it the counter
        // restarts at 1 after a load with no surviving batches, colliding with ids
        // already minted before the save (arena correlation, telemetry, replay).
        private long m_NextBatchId = 1;

        // Restore buffer (EnemyState owner pattern): Deserialize writes here, the
        // post-load bridge applies it via OnLoadRestore.
#pragma warning disable CIVIC221 // Restore buffer written in Deserialize, applied by ValidateAfterLoad (PLVS); Serialize reads the live singleton.
        [System.NonSerialized] private CounterAttackArsenal m_RestoredArsenal;
#pragma warning restore CIVIC221
        [System.NonSerialized] private bool m_HasRestoredArsenal;
        // Persisted next-batch-id staged by Deserialize, applied in ValidateAfterLoad
        // (0 = old save without the field → keep the live-scan reseed only).
        [System.NonSerialized] private long m_RestoredNextBatchId;

        protected override void OnCreate()
        {
            base.OnCreate();

            CounterAttackArsenal.EnsureExists(EntityManager);

            m_ArsenalQuery = GetEntityQuery(ComponentType.ReadWrite<CounterAttackArsenal>());
            m_BatchQuery = GetEntityQuery(ComponentType.ReadWrite<ArsenalProcurementBatchIntent>());
            m_ResolvedRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<BudgetDeductResult>(),
                ComponentType.ReadOnly<ArsenalProcurementBudgetLink>());
            m_ResolvedRefundQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetAddFundsRequest>(),
                ComponentType.ReadOnly<BudgetAddFundsResult>(),
                ComponentType.ReadOnly<ArsenalProcurementRefundIntent>());
            m_BudgetRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<ArsenalProcurementBudgetLink>());

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_ArsenalLookup = GetComponentLookup<CounterAttackArsenal>(false);
            // Service-boundary refresh bundle (CIVIC433): the lambda updates the full
            // lookup set a sync service method reads, so callers refresh through one seam.
            m_ServiceLookups = new CivicServiceLookups(() => m_ArsenalLookup.Update(this));

            // Producer-side service registration in OnCreate (mirrors AirDefenseStateSystem):
            // OnStartRunning fires only on first Update, which never arrives if a consumer
            // hits us first from a non-ticking phase.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<ICounterAttackArsenalService>(this);

            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            // Mirror AirDefenseStateSystem: a registered service must be unregistered so a
            // disposed system is not handed out to cross-domain consumers after world teardown.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<ICounterAttackArsenalService>(this);
            base.OnDestroy();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE: OnCreate does not re-run on new-game.
            CounterAttackArsenal.EnsureExists(EntityManager);
        }

        // ====================================================================
        // ICounterAttackArsenalService — sync spend/replenish (main-thread)
        // ====================================================================

        public bool IsAvailable => !m_ArsenalQuery.IsEmptyIgnoreFilter;

        public int StockOf(ArsenalKind kind)
        {
            return m_ArsenalQuery.TryGetSingleton<CounterAttackArsenal>(out var arsenal)
                ? arsenal.StockOf(kind)
                : 0;
        }

        public bool HasStock(ArsenalKind kind, int count = 1)
        {
            if (count <= 0) return true;
            return StockOf(kind) >= count;
        }

        public bool TrySpend(ArsenalKind kind, int count = 1)
        {
            if (count <= 0) return true;
            if (!m_ArsenalQuery.TryGetSingletonEntity<CounterAttackArsenal>(out var entity))
                return false;

            m_ServiceLookups.RefreshIfStale();
            if (!m_ArsenalLookup.HasComponent(entity))
                return false;

            var arsenal = m_ArsenalLookup[entity];
            int current = arsenal.StockOf(kind);
            if (current < count)
                return false;

            ApplyStockDelta(ref arsenal, kind, -count);
            m_ArsenalLookup[entity] = arsenal;
            Log.Info($"Spent {count}x {kind}; {kind} stock {current} -> {arsenal.StockOf(kind)}");
            return true;
        }

        public void Replenish(ArsenalKind kind, int count)
        {
            if (count <= 0) return;
            if (!m_ArsenalQuery.TryGetSingletonEntity<CounterAttackArsenal>(out var entity))
            {
                Log.Warn($"Replenish {count}x {kind} dropped — arsenal singleton missing");
                return;
            }

            m_ServiceLookups.RefreshIfStale();
            if (!m_ArsenalLookup.HasComponent(entity))
                return;

            var arsenal = m_ArsenalLookup[entity];
            int before = arsenal.StockOf(kind);
            ApplyStockDelta(ref arsenal, kind, count);
            m_ArsenalLookup[entity] = arsenal;
            Log.Info($"Replenished {count}x {kind}; {kind} stock {before} -> {arsenal.StockOf(kind)}");
        }

        private static void ApplyStockDelta(ref CounterAttackArsenal arsenal, ArsenalKind kind, int delta)
        {
            int cap = BalanceConfig.Current.GridWarfare.ArsenalStockCap;
            if (kind == ArsenalKind.Ballistic)
                arsenal.BallisticStock = math.clamp(arsenal.BallisticStock + delta, 0, cap);
            else
                arsenal.DroneStock = math.clamp(arsenal.DroneStock + delta, 0, cap);
        }

        // ====================================================================
        // Paid procurement pipeline (mirror of AAResupplyPipelineSystem)
        // ====================================================================

        protected override void OnUpdateImpl()
        {
            bool hasResolved = !m_ResolvedRequestQuery.IsEmpty;
            bool hasRefunds = !m_ResolvedRefundQuery.IsEmpty;
            bool hasBatches = !m_BatchQuery.IsEmpty;
            if (!hasResolved && !hasRefunds && !hasBatches) return;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            if (hasResolved)
                ResolveBudgetResults(ref ecb, ref ecbCreated);
            if (hasRefunds)
                DrainResolvedRefunds(ref ecb, ref ecbCreated);
            if (!m_BatchQuery.IsEmpty)
                FinalizeReadyBatches(ref ecb, ref ecbCreated);

            if (ecbCreated)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        private void ResolveBudgetResults(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (result, link, requestEntity) in
                SystemAPI.Query<RefRO<BudgetDeductResult>, RefRW<ArsenalProcurementBudgetLink>>()
                .WithEntityAccess())
            {
                if (link.ValueRO.Retired) continue;
                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }

                long batchId = link.ValueRO.BatchId;
                link.ValueRW.Retired = true;

                if (TryFindBatchRW(batchId, out var batchRef))
                {
                    batchRef.ValueRW.BudgetResolved = true;
                    batchRef.ValueRW.BudgetSucceeded = result.ValueRO.Succeeded;
                }
                else if (result.ValueRO.Succeeded && result.ValueRO.Amount > 0)
                {
                    // Charged but the batch vanished — return the money.
                    Refund(ecb, result.ValueRO.Amount, $"ArsenalRefund:OrphanBudget:{batchId}:{result.ValueRO.Amount}");
                    Log.Warn($"Arsenal budget result BatchId={batchId} has no batch — refunded ${result.ValueRO.Amount:N0}");
                }
                else
                {
                    Log.Warn($"Arsenal budget result BatchId={batchId} has no batch");
                }

                ecb.DestroyEntity(requestEntity);
                IncrementEcbCount();
            }
        }

        private void FinalizeReadyBatches(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (batchRef, batchEntity) in
                SystemAPI.Query<RefRW<ArsenalProcurementBatchIntent>>()
                .WithEntityAccess())
            {
                var batch = batchRef.ValueRO;

                // Terminal guard (AAResupply doctrine): the destroy below is a deferred
                // ECB command played back only after ALL sim ticks. At 2x-3x this batch
                // is still alive on later ticks of the same frame — skip once decided so
                // stock + budget are not re-applied.
                if (batch.Applied) continue;

                if (batch.RequiresBudget && !batch.BudgetResolved && !HasBudgetRequestForBatch(batch.BatchId))
                {
                    if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                    batchRef.ValueRW.Applied = true;
                    ecb.DestroyEntity(batchEntity);
                    IncrementEcbCount();
                    Publish(ArsenalProcurementResult.Dropped, batch);
                    Log.Warn($"Arsenal batch {batch.BatchId}: budget request missing, dropped unresolved batch");
                    continue;
                }

                bool ready = !batch.RequiresBudget || batch.BudgetResolved;
                if (!ready) continue;

                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }

                if (batch.RequiresBudget && !batch.BudgetSucceeded)
                {
                    batchRef.ValueRW.Applied = true;
                    ecb.DestroyEntity(batchEntity);
                    IncrementEcbCount();
                    Publish(ArsenalProcurementResult.BudgetFailed, batch);
                    Log.Info($"Arsenal batch {batch.BatchId}: budget failed, dropped");
                    continue;
                }

                // Budget succeeded (or none required) → grant the stock.
                Replenish(batch.Kind, batch.Count);

                batchRef.ValueRW.Applied = true;
                ecb.DestroyEntity(batchEntity);
                IncrementEcbCount();
                Publish(ArsenalProcurementResult.Granted, batch);
            }
        }

        private void Publish(ArsenalProcurementResult result, in ArsenalProcurementBatchIntent batch)
        {
            EventBus?.SafePublish(new ArsenalProcurementEvent(
                result,
                batch.Kind,
                result == ArsenalProcurementResult.Granted ? batch.Count : 0,
                batch.TotalCost
            ), nameof(CounterAttackArsenalSystem));
        }

        private void DrainResolvedRefunds(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (result, intent, entity) in
                SystemAPI.Query<RefRO<BudgetAddFundsResult>, RefRO<ArsenalProcurementRefundIntent>>()
                .WithAll<BudgetAddFundsRequest>()
                .WithEntityAccess())
            {
                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                if (!result.ValueRO.Succeeded)
                    Log.Warn($"Arsenal retained refund failed: op={intent.ValueRO.OperationKey.ToString()} amount=${intent.ValueRO.Amount:N0}");
                ecb.DestroyEntity(entity);
                IncrementEcbCount();
            }
        }

        private void Refund(EntityCommandBuffer ecb, long amount, string operationKey)
        {
            if (amount <= 0) return;
            if (HasRefundRequest(operationKey)) return;

            if (BudgetEmitter.TryQueueAddFunds(
                    ecb,
                    amount,
                    BudgetSource.ResupplyRefund,
                    BudgetIncomeKind.Refund,
                    operationKey,
                    out var refundEntity,
                    BudgetResultMode.RetainResult))
            {
                ecb.AddComponent(refundEntity, new ArsenalProcurementRefundIntent
                {
                    Amount = amount,
                    OperationKey = new Unity.Collections.FixedString128Bytes(operationKey)
                });
                IncrementEcbCount();
            }
        }

        private bool HasRefundRequest(string operationKey)
        {
            foreach (var requestRef in SystemAPI.Query<RefRO<BudgetAddFundsRequest>>())
            {
                if (requestRef.ValueRO.OperationKey.ToString() == operationKey)
                    return true;
            }

            return false;
        }

        private bool HasBudgetRequestForBatch(long batchId)
        {
            if (m_BudgetRequestQuery.IsEmpty) return false;

            foreach (var link in SystemAPI.Query<RefRO<ArsenalProcurementBudgetLink>>())
            {
                if (link.ValueRO.BatchId == batchId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// <see cref="ICounterAttackArsenalService.AllocateProcurementBatchId"/>.
        /// Allocate a stable non-zero procurement batch id above any in-flight batch.
        /// Producers (UI import trigger, donor integration) call this on the main thread
        /// before <c>ArsenalProcurementEmitter.QueuePaidProcurement</c>. The owner
        /// system holds the monotonic counter so two producers in the same frame cannot
        /// collide.
        /// </summary>
        public long AllocateProcurementBatchId()
        {
            // CIVIC281: cached EntityQuery, not SystemAPI.Query — this is an external
            // entry point (producers call it from their own system context), where the
            // source-generated SystemAPI query would belong to the wrong system.
            long maxExisting = 0;
            if (!m_BatchQuery.IsEmptyIgnoreFilter)
            {
                using var batches = m_BatchQuery.ToComponentDataArray<ArsenalProcurementBatchIntent>(Allocator.Temp);
                for (int i = 0; i < batches.Length; i++)
                    maxExisting = Math.Max(maxExisting, batches[i].BatchId);
            }

            if (m_NextBatchId <= maxExisting)
                m_NextBatchId = maxExisting + 1;

            return m_NextBatchId++;
        }

        private bool TryFindBatchRW(long batchId, out RefRW<ArsenalProcurementBatchIntent> result)
        {
            foreach (var batchRef in SystemAPI.Query<RefRW<ArsenalProcurementBatchIntent>>())
            {
                if (batchRef.ValueRO.BatchId == batchId)
                {
                    result = batchRef;
                    return true;
                }
            }

            result = default;
            return false;
        }

        // ====================================================================
        // Save / Load (CounterAttackArsenalCodec; MobilizationSystem owner pattern)
        // ====================================================================

        public void ResetToBootDefaults(ResetReason reason)
        {
            m_RestoredArsenal = default;
            m_HasRestoredArsenal = false;
            m_RestoredNextBatchId = 0;
            m_NextBatchId = 1;
        }

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            if (m_ArsenalQuery.TryGetSingletonEntity<CounterAttackArsenal>(out var entity))
                EntityManager.SetComponentData(entity, CounterAttackArsenal.Default);
            m_RestoredArsenal = default;
            m_HasRestoredArsenal = false;
            m_RestoredNextBatchId = 0;
            m_NextBatchId = 1;
            Log.Info("SetDefaults: empty arsenal");
        }

        /// <summary>
        /// IPostLoadValidation: PLVS runs after Deserialize and after the singleton is
        /// recreated, so this is the safe place to write the restored stock onto the live
        /// singleton (the entity may not exist mid-Deserialize). Mirrors how
        /// MobilizationSystem writes its restored payload here, not in Deserialize.
        /// </summary>
        public void ValidateAfterLoad()
        {
            CounterAttackArsenal.EnsureExists(EntityManager);
            if (m_ArsenalQuery.TryGetSingletonEntity<CounterAttackArsenal>(out var entity))
            {
                EntityManager.SetComponentData(entity, m_HasRestoredArsenal
                    ? m_RestoredArsenal
                    : CounterAttackArsenal.Default);
            }
            m_HasRestoredArsenal = false;

            // Restore the monotonic counter so minted ids stay session-stable across
            // load. Never lower it below where it already sits (a surviving in-flight
            // batch may have reseeded the live counter above the persisted value).
            if (m_RestoredNextBatchId > m_NextBatchId)
                m_NextBatchId = m_RestoredNextBatchId;
            m_RestoredNextBatchId = 0;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                if (!m_ArsenalQuery.TryGetSingleton<CounterAttackArsenal>(out var state))
                    state = CounterAttackArsenal.Default;
                CounterAttackArsenalCodec.Write(state, m_NextBatchId, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(CounterAttackArsenalSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(CounterAttackArsenalSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                CounterAttackArsenalCodec.Read(reader, out var state, out var nextBatchId);
                m_RestoredArsenal = state;
                m_HasRestoredArsenal = true;
                m_RestoredNextBatchId = nextBatchId;
                Log.Info($"Deserialized: Drone={state.DroneStock}, Ballistic={state.BallisticStock}, NextBatchId={nextBatchId}");
            }
            catch (System.Exception ex)
            {
                Log.Error($"Deserialize failed: {ex}");
                ResetToBootDefaults(ResetReason.DeserializeFailed);
            }
            finally
            {
                SerializationGuard.EndBlock(reader, block);
            }
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            // Recreate the singleton so it exists before PLVS; the restored stock is
            // written authoritatively in ValidateAfterLoad (the singleton-writer seam
            // Deserialize defers to via the m_RestoredArsenal buffer).
            CounterAttackArsenal.EnsureExists(entityManager);
        }
    }
}
