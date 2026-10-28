using System;
using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Services.City
{
    /// <summary>
    /// Central budget request queue — the arbiter for competing simulation-driven
    /// money flows. All mutations go through <see cref="BudgetTransactionResolver"/>
    /// (main-thread); this system is the queue front-end that adds what a raw sync
    /// call cannot: priority arbitration between same-frame deducts, income-first
    /// ordering, and debt fallback.
    ///
    /// Two sanctioned payment patterns share the resolver:
    /// - Queued (this system): simulation flows — damage accounting, war debt,
    ///   resupply, monthly debt billing — competing bulk deducts that need the
    ///   priority sort and can tolerate a one-frame result latency.
    /// - Direct sync (BudgetTransactionResolver.Deduct at the call site): atomic
    ///   single player actions that need an immediate, pause-safe outcome —
    ///   building placement, plant/civilian repairs, maintenance contracts.
    ///
    /// Processing order per frame:
    /// 1. AddFunds requests (income first, maximizes budget for deductions)
    /// 2. Deduct requests sorted by Priority (ascending: PlayerAction → Damage)
    /// 3. DebtPayment requests (monthly billing cycle)
    ///
    /// Deduct failure modes:
    /// - DebtFallbackCategory set: pay available + debt remainder (always "succeeds")
    /// - DebtFallbackCategory empty: deduction fails, outcome written to BudgetDeductResult (RetainResult) or entity destroyed (FireAndForget)
    ///
    /// Registered in the late band after post-load validation; GameSimulationEndBarrier
    /// drains producer ECB work at the start of the next simulation sub-tick.
    /// </summary>
    [ActIndependent]
    public partial class BudgetResolutionSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("BudgetResolutionSystem");

        private EntityQuery m_AddFundsQuery;
        private EntityQuery m_DeductQuery;
        private EntityQuery m_DebtPaymentQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private IShadowWalletService m_WalletService = NullShadowWalletService.Instance;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_AddFundsQuery = GetEntityQuery(
                ComponentType.ReadWrite<BudgetAddFundsRequest>(),
                ComponentType.Exclude<BudgetAddFundsResult>());
            // F-15 (ACC-07): ValidateAfterLoad zeroes BudgetDeductRequest.ReservationAmount
            // on expired entities — the query is a writer now, must be ReadWrite (CIVIC342).
            m_DeductQuery = GetEntityQuery(
                ComponentType.ReadWrite<BudgetDeductRequest>(),
                ComponentType.Exclude<BudgetDeductResult>());
            m_DebtPaymentQuery = GetEntityQuery(ComponentType.ReadOnly<BudgetDebtPaymentRequest>());

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            Log.Info("Created (central budget resolution)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // Axiom 15: service resolution belongs in OnStartRunning — registration may not be
            // complete in OnCreate (depends on FeatureRegistry ordering).
            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
        }

        public void ValidateAfterLoad()
        {
            // Destroy stale FireAndForget request entities that survived save/load.
            // Without this, deserialized requests are re-processed → double budget deduction/income.
            // RetainResult requests expire to failed results instead of re-deducting:
            // domain drains own rollback/terminal behaviour from BudgetDeductResult.
            //
            // Selective purge (doctrine Invariant 3): only entity TYPES this system owns are
            // purged. BudgetDebtPaymentRequest is owned by this system too (consumed in
            // ProcessDebtPaymentRequests) and previously slipped through (W1 S042).
            int destroyed = 0;
            int expired = 0;
            CityBudgetService.ResetPendingDeductions();
            m_WalletService.ResetPendingDeductions();

            var toDestroy = new NativeList<Entity>(Allocator.Temp);
            var toExpire = new NativeList<ExpiredDeductRequest>(Allocator.Temp);
            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<BudgetDeductRequest>>()
                .WithNone<BudgetDeductResult>()
                .WithEntityAccess())
            {
                if (request.ValueRO.ResultMode == BudgetResultMode.FireAndForget)
                {
                    toDestroy.Add(entity);
                }
                else if (request.ValueRO.ResultMode == BudgetResultMode.RetainResult)
                {
                    ReleasePendingReservation(request.ValueRO);
                    toExpire.Add(new ExpiredDeductRequest(entity, request.ValueRO.Amount));
                }
            }
            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<BudgetAddFundsRequest>>()
                .WithNone<BudgetAddFundsResult>()
                .WithEntityAccess())
            {
                if (request.ValueRO.ResultMode == BudgetResultMode.FireAndForget)
                    toDestroy.Add(entity);
            }
            if (toDestroy.Length > 0)
            {
                destroyed = toDestroy.Length;
                EntityManager.DestroyEntity(toDestroy.AsArray());
            }
            if (toExpire.Length > 0)
            {
                expired = toExpire.Length;
                for (int i = 0; i < toExpire.Length; i++)
                {
                    var item = toExpire[i];
                    if (EntityManager.Exists(item.Entity) && !EntityManager.HasComponent<BudgetDeductResult>(item.Entity))
                    {
                        EntityManager.AddComponentData(item.Entity, new BudgetDeductResult
                        {
                            Succeeded = false,
                            Amount = item.Amount
                        });
                        EntityManager.AddComponentData(item.Entity, new BudgetDeductExpiredOnLoad());

                        // F-15 (ACC-07): structurally clear the per-entity
                        // reservation on expiry. The global pending counters were
                        // reset above; zeroing ReservationAmount here makes the
                        // "no stale per-request reservation survives load" contract
                        // explicit rather than relying on the <=0 rollback guard
                        // plus post-load gate-flag ordering.
                        if (EntityManager.HasComponent<BudgetDeductRequest>(item.Entity))
                        {
                            var req = EntityManager.GetComponentData<BudgetDeductRequest>(item.Entity);
                            if (req.ReservationAmount != 0)
                            {
                                req.ReservationAmount = 0;
                                EntityManager.SetComponentData(item.Entity, req);
                            }
                        }
                    }
                }
            }
            toDestroy.Dispose();
            toExpire.Dispose();
            if (destroyed > 0)
                Log.Info($"ValidateAfterLoad: destroyed {destroyed} stale budget request entities");
            if (expired > 0)
                Log.Info($"ValidateAfterLoad: expired {expired} retained budget request entities to failed results");

        }

        protected override void OnUpdateImpl()
        {
            bool hasWork = !m_AddFundsQuery.IsEmpty
                || !m_DeductQuery.IsEmpty
                || !m_DebtPaymentQuery.IsEmpty;

            if (!hasWork)
                return;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            ProcessAddFundsRequests(ref ecb, ref ecbCreated);
            // R9-M11 FIX: Deduct requests first (sorted by priority), debt payment last
            // DebtPayment = priority 200 (lowest), but was processed before all deducts
            ProcessDeductRequests(ref ecb, ref ecbCreated);
            ProcessDebtPaymentRequests(ref ecb, ref ecbCreated);

            if (ecbCreated)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

#pragma warning disable CIVIC145 // Lazy helper: every call site writes immediately after EnsureEcb returns.
        private void EnsureEcb(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            if (ecbCreated)
                return;

            ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            ecbCreated = true;
        }
#pragma warning restore CIVIC145

        private void ProcessAddFundsRequests(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            var requests = new NativeList<AddFundsEntry>(16, Allocator.Temp);
            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<BudgetAddFundsRequest>>()
                .WithNone<BudgetAddFundsResult>()
                .WithEntityAccess())
            {
                requests.Add(new AddFundsEntry(entity, request.ValueRO));
            }

            for (int i = 0; i < requests.Length; i++)
            {
                var entry = requests[i];
                var r = entry.Request;
                bool hadBalance = CityBudgetService.TryGetBalance(World, out long beforeBalance);
                var addResult = CityBudgetService.AddFunds(World, r.Amount, r.Source.ToString(), r.IncomeKind);
                long appliedAmount = 0;
                if ((addResult == BudgetResult.Ok || addResult == BudgetResult.Capped)
                    && hadBalance
                    && CityBudgetService.TryGetBalance(World, out long afterBalance))
                {
                    appliedAmount = System.Math.Max(0, afterBalance - beforeBalance);
                }

                if (addResult == BudgetResult.Capped)
                    Log.Warn($"AddFunds capped ${r.Amount:N0} from {r.Source}");
                else if (addResult == BudgetResult.SystemUnavailable || addResult == BudgetResult.BudgetEntityMissing)
                {
                    Log.Warn($"AddFunds retry retained ${r.Amount:N0} from {r.Source} — reason: {addResult}");
                    continue;
                }
                else if (addResult != BudgetResult.Ok)
                    Log.Error($"AddFunds dropped ${r.Amount:N0} from {r.Source} — reason: {addResult}");
                else
                {
                    // Ok: request fully applied.
                }
                EnsureEcb(ref ecb, ref ecbCreated);
                if (r.ResultMode == BudgetResultMode.RetainResult)
                {
                    // SAVE-LOAD-ATOMIC: retained credits/refunds are terminal only after BRS has
                    // observed and classified the budget mutation. Write the result synchronously,
                    // not through ECB playback: otherwise a save can observe "money already mutated,
                    // result absent" and replay/expire the retained request incorrectly. Iteration is
                    // over the materialized `requests` list, so this structural write is enumerator-safe.
                    EntityManager.AddComponentData(entry.Entity, new BudgetAddFundsResult
                    {
                        Succeeded = addResult == BudgetResult.Ok || addResult == BudgetResult.Capped,
                        Amount = r.Amount,
                        AppliedAmount = appliedAmount,
                        Result = addResult,
                        OperationKey = r.OperationKey
                    });
                }
                else
                {
                    ecb.DestroyEntity(entry.Entity);
                }
            }

            if (requests.IsCreated) requests.Dispose();
        }

        private void ProcessDebtPaymentRequests(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            bool hasRequest = false;
            BudgetDebtPaymentRequest latest = default;
            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<BudgetDebtPaymentRequest>>()
                .WithEntityAccess())
            {
                var r = request.ValueRO;
                if (!hasRequest || r.BillingDay >= latest.BillingDay)
                {
                    latest = r;
                    hasRequest = true;
                }
                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(entity);
            }

            if (!hasRequest)
                return;

            if (CityDebtService.IsBillingDayApplied(latest.BillingDay))
                return;

            CityDebtService.ProcessMonthlyPayment(
                World, latest.Rate, latest.Minimum, latest.InterestRate,
                latest.WarningRatio, latest.RestructureRatio, latest.RestructuredRate,
                latest.DecisionDebt, latest.PeriodIncome);
            CityDebtService.SnapshotPeriodIncome();
            CityDebtService.SetLastAppliedBillingDay(latest.BillingDay);
            EventBus?.SafePublish(new DebtPaymentAppliedEvent(latest.BillingDay), "BudgetResolutionSystem");
        }

        private void ProcessDeductRequests(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            // Collect all deduct requests into a sorted list
            if (m_DeductQuery.IsEmpty) return;

            var requests = new NativeList<DeductEntry>(16, Allocator.Temp);

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<BudgetDeductRequest>>()
                .WithNone<BudgetDeductResult>()
                .WithEntityAccess())
            {
                requests.Add(new DeductEntry
                {
                    Entity = entity,
                    Amount = request.ValueRO.Amount,
                    Category = request.ValueRO.Category,
                    Priority = request.ValueRO.Priority,
                    Source = request.ValueRO.Source,
                    DebtFallbackCategory = request.ValueRO.DebtFallbackCategory,
                    ResultMode = request.ValueRO.ResultMode,
                    ReservationAmount = request.ValueRO.ReservationAmount
                });
            }

            // Sort by priority (ascending — lower value = higher priority)
            requests.Sort(new PriorityComparer());

            for (int i = 0; i < requests.Length; i++)
            {
                var entry = requests[i];
                try
                {
                    string category = entry.Category.ToString();
                    string source = entry.Source.ToString();

                    var result = BudgetTransactionResolver.Deduct(
                        World,
                        m_WalletService,
                        entry.Amount,
                        category,
                        source,
                        entry.DebtFallbackCategory.ToString());

                    if (entry.ResultMode == BudgetResultMode.RetainResult)
                    {
                        // SAVE-LOAD-ATOMIC: write synchronously so a save cannot land after the
                        // budget/debt/wallet mutation but before the retained result exists.
                        // Iteration is over a materialized list, so this structural write is enumerator-safe.
                        EntityManager.AddComponentData(entry.Entity, result);
                    }
                    else
                    {
                        EnsureEcb(ref ecb, ref ecbCreated);
                        ecb.DestroyEntity(entry.Entity);
                    }
                }
                finally
                {
                    ReleasePendingReservation(entry);
                    if (entry.ResultMode == BudgetResultMode.RetainResult && entry.ReservationAmount > 0)
                    {
                        EnsureEcb(ref ecb, ref ecbCreated);
                        ecb.SetComponent(entry.Entity, entry.ToRequestWithReleasedReservation());
                    }
                }
            }

            if (requests.IsCreated) requests.Dispose();
        }

        /// <summary>
        /// Entry for sorting deduct requests by priority.
        /// </summary>
        private struct DeductEntry
        {
            public Entity Entity;
            public long Amount;
            public FixedString32Bytes Category;
            public byte Priority;
            public FixedString64Bytes Source;
            public FixedString32Bytes DebtFallbackCategory;
            public BudgetResultMode ResultMode;
            public long ReservationAmount;

            public BudgetDeductRequest ToRequestWithReleasedReservation() => new()
            {
                Amount = Amount,
                Category = Category,
                Priority = Priority,
                Source = Source,
                DebtFallbackCategory = DebtFallbackCategory,
                ResultMode = ResultMode,
                ReservationAmount = 0
            };
        }

        private readonly struct ExpiredDeductRequest
        {
            public readonly Entity Entity;
            public readonly long Amount;

            public ExpiredDeductRequest(Entity entity, long amount)
            {
                Entity = entity;
                Amount = amount;
            }
        }

        private void ReleasePendingReservation(DeductEntry entry)
        {
            if (entry.ReservationAmount <= 0)
                return;

            if (entry.Category.ToString() == BudgetCategory.ShadowOps)
                m_WalletService.RollbackPendingDeduction(entry.ReservationAmount);
            else
                CityBudgetService.RollbackPendingDeduction(entry.ReservationAmount);
        }

        private void ReleasePendingReservation(in BudgetDeductRequest request)
        {
            if (request.ReservationAmount <= 0)
                return;

            if (request.Category.ToString() == BudgetCategory.ShadowOps)
                m_WalletService.RollbackPendingDeduction(request.ReservationAmount);
            else
                CityBudgetService.RollbackPendingDeduction(request.ReservationAmount);
        }

        private readonly struct AddFundsEntry
        {
            public readonly Entity Entity;
            public readonly BudgetAddFundsRequest Request;

            public AddFundsEntry(Entity entity, BudgetAddFundsRequest request)
            {
                Entity = entity;
                Request = request;
            }
        }

        private struct PriorityComparer : IComparer<DeductEntry>
        {
            public int Compare(DeductEntry a, DeductEntry b)
            {
                int cmp = a.Priority.CompareTo(b.Priority);
                // R9-M06 FIX: Deterministic tiebreaker — prevents flickering when budget is tight
                return cmp != 0 ? cmp : a.Entity.Index.CompareTo(b.Entity.Index);
            }
        }
    }
}
