using System;
using System.Linq;
using Game;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Core.Systems.Requests
{
    /// <summary>
    /// Cleanup for internal fire-and-forget command entities.
    ///
    /// These types are created by producers via ECB and consumed per-frame by dedicated systems.
    /// If a consumer is gated, disabled, or skips processing, command entities persist forever.
    /// This system destroys any command entity that has survived longer than TTL.
    ///
    /// Findings: S3-03, S6-01 (command entities outliving their consumer).
    /// </summary>
#pragma warning disable CIVIC442 // Reads RequestResultEvent request ids for terminal-retained TTL; RequestResultCleanupSystem owns RequestResultEvent destruction.
    [ActIndependent]
    public partial class CommandRequestCleanupSystem : ThrottledSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;

        private static readonly LogContext Log = new("CommandRequestCleanup");

        private ModCleanupBarrier m_ModCleanupBarrier = null!;
        private SimulationSystem m_SimulationSystem = null!;
        private ComponentLookup<RequestMeta> m_RequestMetaLookup;
        private ComponentLookup<BudgetDeductRequest> m_BudgetDeductLookup;
        private ComponentLookup<ShadowWalletDeductRequest> m_ShadowDeductLookup;
        private IShadowWalletService m_WalletService = NullShadowWalletService.Instance;
        private EntityQuery m_RequestMetaQuery;

        /// <summary>
        /// Tracks first-seen simulation frame for each retained command entity.
        /// Entities consumed by their normal consumer are pruned on next pass.
        /// </summary>
        private NativeHashMap<Entity, uint> m_FirstSeenFrame;

        private EntityQuery[] m_RetainedQueries = null!;
        private string[] m_RetainedTypeNames = null!;
        private RetainedRequestTtlPolicy[] m_RetainedTtlPolicies = null!;
        private int[] m_RetainedTtlFrames = null!;
        private EntityQuery[] m_TransientQueries = null!;
        private EntityQuery[] m_ReconciledOutcomeQueries = null!;
        private string[] m_ReconciledOutcomeTypeNames = null!;
        private bool m_HasUntilTerminalResultPolicy;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_2500_MS;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ModCleanupBarrier = World.GetOrCreateSystemManaged<ModCleanupBarrier>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_RequestMetaLookup = GetComponentLookup<RequestMeta>(true);
            m_BudgetDeductLookup = GetComponentLookup<BudgetDeductRequest>(true);
            m_ShadowDeductLookup = GetComponentLookup<ShadowWalletDeductRequest>(true);
            m_RequestMetaQuery = GetEntityQuery(ComponentType.ReadOnly<RequestMeta>());
            m_FirstSeenFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);

            var retainedClassifications = RequestClassificationManifest.All
                .Where(c => c.Kind == RequestPersistenceKind.RetainedInput)
                .OrderBy(c => c.RequestType.FullName)
                .ToArray();
            var transientClassifications = RequestClassificationManifest.All
                .Where(c => c.Kind == RequestPersistenceKind.TransientInput)
                .Where(c => c.PurgeOwner == typeof(CommandRequestCleanupSystem))
                .OrderBy(c => c.RequestType.FullName)
                .ToArray();
            var reconciledOutcomeClassifications = RequestClassificationManifest.All
                .Where(c => c.Kind == RequestPersistenceKind.ReconciledOutcome)
                .OrderBy(c => c.RequestType.FullName)
                .ToArray();

            m_RetainedQueries = new EntityQuery[retainedClassifications.Length];
            m_RetainedTypeNames = new string[retainedClassifications.Length];
            m_RetainedTtlPolicies = new RetainedRequestTtlPolicy[retainedClassifications.Length];
            m_RetainedTtlFrames = new int[retainedClassifications.Length];
            m_TransientQueries = new EntityQuery[transientClassifications.Length];
            m_ReconciledOutcomeQueries = new EntityQuery[reconciledOutcomeClassifications.Length];
            m_ReconciledOutcomeTypeNames = new string[reconciledOutcomeClassifications.Length];

            // Cache the generic ComponentType.ReadOnly<>() method for dynamic invocation
            var readOnlyMethod = typeof(ComponentType)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .First(m => m.Name == "ReadOnly" && m.IsGenericMethod && m.GetParameters().Length == 0);

            for (int i = 0; i < retainedClassifications.Length; i++)
            {
                var classification = retainedClassifications[i];
                var genericMethod = readOnlyMethod.MakeGenericMethod(classification.RequestType);
                var componentType = (ComponentType)genericMethod.Invoke(null, null);
                m_RetainedQueries[i] = GetEntityQuery(componentType);
                m_RetainedTypeNames[i] = classification.RequestType.Name;
                m_RetainedTtlPolicies[i] = classification.TtlPolicy;
                m_RetainedTtlFrames[i] = classification.TtlFrames;
                m_HasUntilTerminalResultPolicy |= classification.TtlPolicy == RetainedRequestTtlPolicy.UntilTerminalResult;
            }

            for (int i = 0; i < transientClassifications.Length; i++)
            {
                var classification = transientClassifications[i];
                var genericMethod = readOnlyMethod.MakeGenericMethod(classification.RequestType);
                var componentType = (ComponentType)genericMethod.Invoke(null, null);
                m_TransientQueries[i] = GetEntityQuery(componentType);
            }

            for (int i = 0; i < reconciledOutcomeClassifications.Length; i++)
            {
                var classification = reconciledOutcomeClassifications[i];
                var genericMethod = readOnlyMethod.MakeGenericMethod(classification.RequestType);
                var componentType = (ComponentType)genericMethod.Invoke(null, null);
                m_ReconciledOutcomeQueries[i] = GetEntityQuery(componentType);
                m_ReconciledOutcomeTypeNames[i] = classification.RequestType.Name;
            }

            Log.Info($"Created — classified {m_RetainedQueries.Length} retained input type(s), {m_TransientQueries.Length} owned transient input type(s), {m_ReconciledOutcomeQueries.Length} reconciled outcome type(s)");
        }

        [CompletesDependency("OnThrottledUpdate: throttled 2500 ms cleanup pass; dynamic EntityQuery cannot be source-generated, so retained / reconciled-outcome buffers are materialised via ToEntityArray for TTL evaluation")]
        protected override void OnThrottledUpdate()
        {
            uint frame = m_SimulationSystem.frameIndex;
            m_RequestMetaLookup.Update(this);
            m_BudgetDeductLookup.Update(this);
            m_ShadowDeductLookup.Update(this);
            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            int cleanupCount = 0;

            // Phase 1: Scan retained command queries only. Transient input is purged
            // after load through PurgeAfterLoad, never by in-session TTL.
            var currentRetainedEntities = new NativeHashSet<Entity>(64, Allocator.Temp);
            var currentOutcomeEntities = CollectReconciledOutcomeEntities();
            var terminalRequestIds = new NativeHashSet<int>(16, Allocator.Temp);
            if (m_HasUntilTerminalResultPolicy)
            {
                foreach (var result in SystemAPI.Query<RefRO<RequestResultEvent>>())
                {
                    if (result.ValueRO.RequestId != 0)
                        terminalRequestIds.Add(result.ValueRO.RequestId);
                }
            }

            for (int q = 0; q < m_RetainedQueries.Length; q++)
            {
                if (m_RetainedQueries[q].IsEmptyIgnoreFilter) continue;

                var entities = m_RetainedQueries[q].ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    currentRetainedEntities.Add(e);

                    if (!m_FirstSeenFrame.ContainsKey(e))
                    {
                        m_FirstSeenFrame[e] = frame;
                    }

                    bool hasMeta = m_RequestMetaLookup.TryGetComponent(e, out var meta);
                    bool hasTerminalResult = hasMeta && terminalRequestIds.Contains(meta.RequestId);
                    uint ageFrames;
                    if (RetainedRequestCleanupPolicy.ShouldExpire(
                            m_RetainedTtlPolicies[q],
                            frame,
                            m_FirstSeenFrame[e],
                            hasMeta ? ResolveMetaCreatedFrame(meta, m_FirstSeenFrame[e]) : 0u,
                            m_RetainedTtlFrames[q],
                            hasMeta,
                            hasTerminalResult,
                            out ageFrames))
                    {
                        if (!ecbCreated)
                        {
                            ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                            ecbCreated = true;
                        }
                        ReleasePendingReservation(e);
                        ecb.DestroyEntity(e);
                        m_FirstSeenFrame.Remove(e);
                        cleanupCount++;
                        float ageSeconds = ageFrames / Engine.Timing.SIMULATION_FPS;
                        RequestLogger.LogCommandCleanup(m_RetainedTypeNames[q], ageSeconds);
                        EventBus?.SafePublish(new CommandRequestOrphanedEvent(m_RetainedTypeNames[q], ageSeconds, "ttl_expired"), "CommandRequestCleanup");
                    }
                }
                if (entities.IsCreated) entities.Dispose();
            }

            // Phase 2: Reconciled outcomes must stay attached to a retained request.
            var orphanedOutcomeEntities = new NativeHashSet<Entity>(16, Allocator.Temp);
            for (int q = 0; q < m_ReconciledOutcomeQueries.Length; q++)
            {
                if (m_ReconciledOutcomeQueries[q].IsEmptyIgnoreFilter) continue;

                var entities = m_ReconciledOutcomeQueries[q].ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    if (currentRetainedEntities.Contains(e))
                        continue;
                    if (!orphanedOutcomeEntities.Add(e))
                        continue;

                    if (!ecbCreated)
                    {
                        ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                        ecbCreated = true;
                    }

                    ecb.DestroyEntity(e);
                    cleanupCount++;
                    RequestLogger.LogCommandCleanup(m_ReconciledOutcomeTypeNames[q], 0);
                    EventBus?.SafePublish(new CommandRequestOrphanedEvent(m_ReconciledOutcomeTypeNames[q], 0f, "outcome_orphan"), "CommandRequestCleanup");
                }
                if (entities.IsCreated) entities.Dispose();
            }
            if (orphanedOutcomeEntities.IsCreated) orphanedOutcomeEntities.Dispose();

            // Phase 3: Prune m_FirstSeenFrame for entities consumed by their normal consumer
            var keysToRemove = new NativeList<Entity>(16, Allocator.Temp);
            foreach (var kv in m_FirstSeenFrame)
            {
                if (!currentRetainedEntities.Contains(kv.Key))
                    keysToRemove.Add(kv.Key);
            }
            for (int i = 0; i < keysToRemove.Length; i++)
                m_FirstSeenFrame.Remove(keysToRemove[i]);
            if (keysToRemove.IsCreated) keysToRemove.Dispose();
            if (currentRetainedEntities.IsCreated) currentRetainedEntities.Dispose();
            if (currentOutcomeEntities.IsCreated) currentOutcomeEntities.Dispose();
            if (terminalRequestIds.IsCreated) terminalRequestIds.Dispose();

            if (cleanupCount > 0)
            {
                Log.Warn($"Destroyed {cleanupCount} orphaned command request(s)");
            }

            if (ecbCreated)
                m_ModCleanupBarrier.AddJobHandleForProducer(Dependency);
        }

        private static uint ResolveMetaCreatedFrame(in RequestMeta meta, uint fallbackFrame)
        {
            return RetainedRequestCleanupPolicy.ResolveMetaCreatedFrame(meta.CreatedFrame, fallbackFrame);
        }

        [CompletesDependency("Helper of the throttled (2500 ms) cleanup pass; dynamic reconciled-outcome EntityQuery cannot be source-generated, so entities are materialised via ToEntityArray for TTL evaluation")]
        private NativeHashSet<Entity> CollectReconciledOutcomeEntities()
        {
            var outcomeEntities = new NativeHashSet<Entity>(16, Allocator.Temp);
            for (int q = 0; q < m_ReconciledOutcomeQueries.Length; q++)
            {
                if (m_ReconciledOutcomeQueries[q].IsEmptyIgnoreFilter)
                    continue;

                var entities = m_ReconciledOutcomeQueries[q].ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                    outcomeEntities.Add(entities[i]);
                if (entities.IsCreated) entities.Dispose();
            }

            return outcomeEntities;
        }

        private void ReleasePendingReservation(Entity entity)
        {
            if (m_ShadowDeductLookup.TryGetComponent(entity, out var shadowRequest))
            {
                long reservationAmount = shadowRequest.ReservationAmount > 0
                    ? shadowRequest.ReservationAmount
                    : shadowRequest.Amount;
                if (reservationAmount > 0)
                    m_WalletService.RollbackPendingDeduction(reservationAmount);
                return;
            }

            if (!m_BudgetDeductLookup.TryGetComponent(entity, out var request)
                || request.ReservationAmount <= 0)
                return;

            if (request.Category.ToString() == BudgetCategory.ShadowOps)
                m_WalletService.RollbackPendingDeduction(request.ReservationAmount);
            else
                CityBudgetService.RollbackPendingDeduction(request.ReservationAmount);
        }

        [CompletesDependency("ValidateAfterLoad: one-shot scan of retained request entities and RequestMeta values to reset frame-base TTL bookkeeping and rebase the static request-id allocator")]
        public void ValidateAfterLoad()
        {
            // LOAD-INVARIANT: cleanup may run before paused GameSimulation consumers, so load-restored retained inputs must survive that first pass.
            uint frame = m_SimulationSystem.frameIndex;
            if (m_FirstSeenFrame.IsCreated)
                m_FirstSeenFrame.Clear();

            var retainedEntities = CollectRetainedEntities();
            foreach (var entity in retainedEntities)
                m_FirstSeenFrame[entity] = frame;
            if (retainedEntities.IsCreated)
                retainedEntities.Dispose();

            int maxRestoredRequestId = GetMaxLiveRequestId();
            RequestRegistrar.RebaseAfterLoad(maxRestoredRequestId);
        }
#pragma warning restore CIVIC442

        [CompletesDependency("PurgeAfterLoad: one-shot post-load purge of orphaned reconciled-outcome entities; dynamic EntityQuery cannot be source-generated, so ToEntityArray materialises the orphan-detection set")]
        public void PurgeAfterLoad()
        {
            var retainedEntities = CollectRetainedEntities();
            int purged = 0;
            for (int q = 0; q < m_TransientQueries.Length; q++)
            {
                if (m_TransientQueries[q].IsEmptyIgnoreFilter)
                    continue;

                purged += m_TransientQueries[q].CalculateEntityCount();
                EntityManager.DestroyEntity(m_TransientQueries[q]);
            }

            int orphanedOutcomes = 0;
            var destroyedOutcomes = new NativeHashSet<Entity>(16, Allocator.Temp);
            for (int q = 0; q < m_ReconciledOutcomeQueries.Length; q++)
            {
                if (m_ReconciledOutcomeQueries[q].IsEmptyIgnoreFilter)
                    continue;

                var entities = m_ReconciledOutcomeQueries[q].ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (retainedEntities.Contains(entity))
                        continue;
                    if (!destroyedOutcomes.Add(entity))
                        continue;
                    if (!EntityManager.Exists(entity))
                        continue;

                    EntityManager.DestroyEntity(entity);
                    orphanedOutcomes++;
                }
                if (entities.IsCreated) entities.Dispose();
            }

            if (retainedEntities.IsCreated)
                retainedEntities.Dispose();
            if (destroyedOutcomes.IsCreated)
                destroyedOutcomes.Dispose();

            if (purged > 0)
                Log.Info($"PurgeAfterLoad: destroyed {purged} owned transient request input entity/entities");
            if (orphanedOutcomes > 0)
                Log.Info($"PurgeAfterLoad: destroyed {orphanedOutcomes} orphaned reconciled outcome entity/entities");
        }

        [CompletesDependency("CollectRetainedEntities: cleanup bookkeeping helper called from OnThrottledUpdate/PurgeAfterLoad paths; dynamic EntityQuery cannot be source-generated, so ToEntityArray materialises the live-retained set for orphan diff")]
        private NativeHashSet<Entity> CollectRetainedEntities()
        {
            var retainedEntities = new NativeHashSet<Entity>(64, Allocator.Temp);
            for (int q = 0; q < m_RetainedQueries.Length; q++)
            {
                if (m_RetainedQueries[q].IsEmptyIgnoreFilter)
                    continue;

                var entities = m_RetainedQueries[q].ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                    retainedEntities.Add(entities[i]);
                if (entities.IsCreated) entities.Dispose();
            }

            return retainedEntities;
        }

        private int GetMaxLiveRequestId()
        {
            int maxRequestId = 0;
            if (m_RequestMetaQuery.IsEmptyIgnoreFilter)
                return maxRequestId;

            var metas = m_RequestMetaQuery.ToComponentDataArray<RequestMeta>(Allocator.Temp);
            for (int i = 0; i < metas.Length; i++)
            {
                if (metas[i].RequestId > maxRequestId)
                    maxRequestId = metas[i].RequestId;
            }
            if (metas.IsCreated)
                metas.Dispose();

            return maxRequestId;
        }

        protected override void OnDestroy()
        {
            if (m_FirstSeenFrame.IsCreated)
                m_FirstSeenFrame.Dispose();
            base.OnDestroy();
        }
    }
}
