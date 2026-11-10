using Game;
using Game.Simulation;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Core.Features.CrossDomain.DamageAccounting
{
    /// <summary>
    /// Central damage accounting system. Consumes DamageAppliedEvent entities
    /// and handles financial consequences (debriefing accumulation or immediate payment).
    ///
    /// Two paths based on IsWaveDamage:
    /// - IsWaveDamage=true: accumulate EstimatedRepairCost in DebriefingInfraStats for debriefing UI.
    ///   NO budget deduction here — real payment happens when player initiates repair via PlantRepairService.
    /// - IsWaveDamage=false: immediate BudgetDeductRequest with Infrastructure debt fallback.
    ///
    /// WARNING: Do NOT charge InfrastructureDamageCost in WarDamageDebtSystem — it would double-charge
    /// (once here at wave end, again when player repairs).
    ///
    /// Also cleans up RepairCompletedEvent entities after all consumers have read them
    /// (deterministic owner — runs after PowerCapacityWriterGroup, which contains the
    /// consumers OperationalDamageSystem and PowerPlantDisasterSystem).
    ///
    /// Scheduling: UpdateAfter(PowerCapacityWriterGroup) — runs after ALL damage producers
    /// have spawned their event entities and ECB playback has occurred.
    /// </summary>
    // Drains retained-result BudgetDeductResult — must run after the post-load
    // purge/expire pass so it never consumes a stale pre-purge result (CIVIC415).
    [ActIndependent]
    // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5 — exemplar of the W2 row 3 migration.
    // RepairCompletedEvent: this system is only the deterministic cleanup owner
    // (destroys it after PowerCapacityWriterGroup consumers ran) — no durable
    // side-effect of its own.
    [TransientConsumerReconcile(typeof(RepairCompletedEvent), ReconcileMode.NoDurableSideEffect)]
    // DamageAppliedEvent: real-money charges no longer ride this transient event,
    // and the charge is settled ONLY when a RetainResult BudgetDeductResult is
    // confirmed — never on mere request creation (a FireAndForget request is
    // destroyed on load by BudgetResolutionSystem.ValidateAfterLoad if a save
    // lands before it resolves; RetainResult expires it to Succeeded=false and
    // the drain re-issues).
    //  - Explosion (PlantExplosionService opens, this system settles): durable marker
    //    EquipmentWear.HasExploded && !ExplosionChargeSettled; reconcile (re)issues
    //    a RetainResult charge, DrainResolvedDamageCharges stamps settled only on
    //    a confirmed success.
    //  - Fire (CounterfeitBatteryFireSystem): durable
    //    PendingOperation<DamageChargeRequest>; phase Queued→Applied on issue,
    //    Confirmed+destroyed only on a confirmed success, reverted to Queued
    //    (re-issued) on an expired/failed result. Invariant 1 / OwnsDurableOutbox.
    //  - Wave damage (IsWaveDamage=true): a display-only DebriefingInfraStats
    //    accumulation, reset per wave; the real payment is the already-durable
    //    PlantRepairService path. Losing one increment across the in-flight
    //    window is harmless.
    [TransientConsumerReconcile(typeof(DamageAppliedEvent), ReconcileMode.ReconcilesFrom, DurableState = typeof(EquipmentWear))]
    [SingletonOwner(typeof(EquipmentWear))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.None,
        DisposePhase = SingletonLifecyclePhase.None,
        AllowAsymmetry = true,
        Justification = "DamageAccountingSystem is the settle-side co-owner of the two-phase ExplosionChargeSettled lifecycle on EquipmentWear: PlantExplosionService opens the charge (writes false on explosion), DamageAccountingSystem settles it (writes true after billing succeeds or on a zero-cost no-op reconcile). Neither side is a single writer in isolation; both attributes record the joint ownership.")]
#pragma warning disable CIVIC442 // CommandRequestCleanupSystem runs in PostSimulation; DamageAccountingSystem runs in GameSimulation via DamageAccountingFeature, so the cleanup order is phase-ordered by registration.
    public partial class DamageAccountingSystem : CivicSystemBase, IPostLoadValidation, IRequestPurger
#pragma warning restore CIVIC442
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;

        private static readonly LogContext Log = new("DamageAccountingSystem");

        private EntityQuery m_DamageEventQuery;
        private EntityQuery m_RepairEventQuery;
        private EntityQuery m_PendingChargeQuery;
        private EntityQuery m_ExplosionReconcileQuery;
        // Resolved RetainResult charges (BudgetDeductResult written, or expired
        // to Succeeded=false on load) — drained to settle/destroy the origin.
        private EntityQuery m_ResolvedChargeQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        // Pre-allocated drain-local dedup set (CIVIC050: no per-frame allocation).
        // Cleared at the start of each DrainResolvedDamageCharges pass.
        private readonly HashSet<long> m_ReissuedExplosions = new HashSet<long>();

        protected override void OnCreate()
        {
            base.OnCreate();

            m_DamageEventQuery = GetEntityQuery(ComponentType.ReadOnly<DamageAppliedEvent>());
            m_RepairEventQuery = GetEntityQuery(ComponentType.ReadOnly<RepairCompletedEvent>());
            m_PendingChargeQuery = GetEntityQuery(
                ComponentType.ReadOnly<DamageChargeRequest>(),
                ComponentType.ReadWrite<PendingPhase>(),
                ComponentType.ReadOnly<PendingOperationTag>());
            m_ExplosionReconcileQuery = GetEntityQuery(ComponentType.ReadWrite<EquipmentWear>());
            m_ResolvedChargeQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<BudgetDeductResult>(),
                ComponentType.ReadOnly<DamageChargeBudgetIntent>());
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            // PERF: Skip when no event/charge entities exist (checked in OnUpdateImpl)

            Log.Info("Created (central damage accounting + repair event cleanup)");
        }

        /// <summary>
        /// RECONCILE pass (SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5). Re-issues any
        /// explosion charge whose transient DamageAppliedEvent was lost in the
        /// in-flight window, driven purely by the durable EquipmentWear marker —
        /// independent of the event. The stale-event purge moved to PurgeAfterLoad.
        /// </summary>
        public void ValidateAfterLoad()
        {
            ReconcileUnsettledExplosionCharges();
        }

        /// <summary>
        /// PURGE pass — runs strictly after every validator's reconcile pass, so
        /// destroying these shared transient events cannot drop an entity another
        /// validator still reconcile-reads (SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5,
        /// "Phase split").
        /// </summary>
        [CompletesDependency("PurgeAfterLoad: one-shot post-load purge of stale damage / repair event entities; CalculateEntityCount is diagnostic-only, sync amortised against the DestroyEntity passes that follow")]
        public void PurgeAfterLoad()
        {
            int destroyed = 0;
            if (!m_DamageEventQuery.IsEmptyIgnoreFilter)
            {
                destroyed += m_DamageEventQuery.CalculateEntityCount();
                EntityManager.DestroyEntity(m_DamageEventQuery);
            }
            if (!m_RepairEventQuery.IsEmptyIgnoreFilter)
            {
                destroyed += m_RepairEventQuery.CalculateEntityCount();
                EntityManager.DestroyEntity(m_RepairEventQuery);
            }

            if (destroyed > 0)
                Log.Info($"PurgeAfterLoad: destroyed {destroyed} stale damage event entities");
        }

        /// <summary>
        /// W2 Invariant 5 (ReconcilesFrom EquipmentWear). The durable marker is
        /// HasExploded &amp;&amp; !ExplosionChargeSettled. On load, (re)issue a
        /// RetainResult charge for any unsettled exploded plant that has no
        /// in-flight charge — the marker is settled ONLY when
        /// DrainResolvedDamageCharges observes the BudgetDeductResult succeed, so
        /// the charge is never lost (a request expired on load resolves to
        /// Succeeded=false and is re-issued) and never doubled (the in-flight
        /// guard + the unsettled marker serialise it).
        /// </summary>
        private void ReconcileUnsettledExplosionCharges()
        {
            if (m_ExplosionReconcileQuery.IsEmptyIgnoreFilter)
                return;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            int reconciled = 0;

            foreach (var wearRW in SystemAPI.Query<RefRW<EquipmentWear>>())
            {
                ref var wear = ref wearRW.ValueRW;
                if (!wear.HasExploded || wear.ExplosionChargeSettled)
                    continue;

                // F-16 (ACC-08): use the explosion repair cost resolved at
                // TriggerExplosion time and persisted on EquipmentWear, so a
                // config drift between save and load cannot change what the
                // player is charged. Fallback recompute for pre-fix saves where
                // SavedExplosionRepairCost is 0 (the v1 EquipmentWear block).
                long cost = wear.SavedExplosionRepairCost > 0
                    ? wear.SavedExplosionRepairCost
                    : (long)System.Math.Round(wear.SavedExplosionDamage * 100)
                      * BalanceConfig.Current.EquipmentWear.RepairCostPerPercent;
                if (cost <= 0)
                {
                    // Nothing owed — safe to settle synchronously.
                    wear.ExplosionChargeSettled = true;
                    reconciled++;
                    continue;
                }

                if (HasOutstandingCharge(wear.Building, DamageType.Explosion))
                    continue; // already issued, awaiting result — don't double-issue

                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                BudgetEmitter.QueueDeductWithDebtFallback(
                    ecb, cost, BudgetCategory.Repairs, BudgetPriority.Damage,
                    "DamageAccountingSystem", DebtCategory.Infrastructure,
                    out var beExpl, BudgetResultMode.RetainResult);
                ecb.AddComponent(beExpl, new DamageChargeBudgetIntent
                {
                    Building = wear.Building,
                    Type = DamageType.Explosion
                });
                reconciled++;
            }

            if (reconciled > 0)
                Log.Info($"ValidateAfterLoad: re-issued {reconciled} unsettled explosion charge(s) (RetainResult — settled on confirmed result)");
        }

        protected override void OnUpdateImpl()
        {
            if (m_DamageEventQuery.IsEmpty && m_RepairEventQuery.IsEmpty
                && m_PendingChargeQuery.IsEmpty && m_ResolvedChargeQuery.IsEmpty)
                return;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            ProcessDamageEvents(ref ecb, ref ecbCreated);
            ProcessPendingDamageCharges(ref ecb, ref ecbCreated);
            DrainResolvedDamageCharges(ref ecb, ref ecbCreated);
            CleanupRepairEvents(ref ecb, ref ecbCreated);
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

        /// <summary>
        /// Drain the durable PendingOperation&lt;DamageChargeRequest&gt; outbox
        /// (fire charges — no pre-existing durable damage marker to reconcile
        /// from). Survives save/load by the PendingOperation guarantee; charged
        /// exactly once then destroyed. SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5
        /// (OwnsDurableOutbox) / Invariant 1.
        /// </summary>
        private void ProcessPendingDamageCharges(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            if (m_PendingChargeQuery.IsEmpty)
                return;

            foreach (var (chargeRef, phaseRW, entity) in
                SystemAPI.Query<RefRO<DamageChargeRequest>, RefRW<PendingPhase>>()
                .WithAll<PendingOperationTag>()
                .WithEntityAccess())
            {
                // Queued = not yet issued; Applied = RetainResult deduct in flight,
                // awaiting DrainResolvedDamageCharges. Only act on Queued — the
                // phase IS the in-flight idempotency guard (it persists, so a save
                // mid-flight resumes correctly).
                if (phaseRW.ValueRO.Value != PendingPhaseValue.Queued)
                    continue;

                var charge = chargeRef.ValueRO;
                if (charge.EstimatedRepairCost <= 0)
                {
                    // Nothing owed — close the durable op directly.
                    EnsureEcb(ref ecb, ref ecbCreated);
                    ecb.SetComponent(entity, new PendingPhase { Value = PendingPhaseValue.Confirmed });
                    ecb.DestroyEntity(entity);
                    continue;
                }

                // W2 HIGH: RetainResult (not FireAndForget). A FireAndForget
                // request created but unprocessed at save is destroyed on load by
                // BudgetResolutionSystem.ValidateAfterLoad → charge lost while the
                // PendingOp was already gone. RetainResult instead expires to
                // Succeeded=false on load; the PendingOp stays alive (durable) and
                // DrainResolvedDamageCharges re-issues until a real success.
                EnsureEcb(ref ecb, ref ecbCreated);
                BudgetEmitter.QueueDeductWithDebtFallback(
                    ecb,
                    charge.EstimatedRepairCost,
                    BudgetCategory.Repairs,
                    BudgetPriority.Damage,
                    "DamageAccountingSystem",
                    DebtCategory.Infrastructure,
                    out var budgetEntity,
                    BudgetResultMode.RetainResult);
                ecb.AddComponent(budgetEntity, new DamageChargeBudgetIntent
                {
                    Building = charge.Building,
                    Type = charge.Type,
                    OpRef = EntityRef.FromEntity(entity)
                });

                if (Log.IsDebugEnabled)
                    Log.Debug($"Issued durable {charge.Type} charge (RetainResult): building {charge.Building.Index}, cost ${charge.EstimatedRepairCost:N0}");

                // Advance to Applied — settle/destroy happens only when the
                // BudgetDeductResult is observed (DrainResolvedDamageCharges).
                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.SetComponent(entity, new PendingPhase { Value = PendingPhaseValue.Applied });
            }
        }

        private void ProcessDamageEvents(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (dataRef, entity) in
                SystemAPI.Query<RefRO<DamageAppliedEvent>>()
                .WithEntityAccess())
            {
                var d = dataRef.ValueRO;

                if (d.EstimatedRepairCost > 0)
                {
                    if (d.IsWaveDamage)
                    {
                        // Display only — accumulate estimate for debriefing UI. No deduction here.
                        // Real payment via PlantRepairService when player initiates repair.
                        // Invariant 5 ExplicitlyLossyAndSafe: a lost increment only
                        // skews the per-wave debrief total; money is the durable
                        // PlantRepairService path.
                        if (SystemAPI.TryGetSingletonRW<DebriefingInfraStats>(out var infraStats))
                        {
                            infraStats.ValueRW.InfrastructureDamageCost += d.EstimatedRepairCost;
                        }
                        else
                        {
                            Log.Warn($"DebriefingInfraStats singleton absent — wave damage cost ${d.EstimatedRepairCost:N0} not accumulated");
                        }
                    }
                    else if (d.Type == DamageType.Explosion)
                    {
                        // W2 HIGH: explosion is ReconcilesFrom EquipmentWear. Issue
                        // the charge as RetainResult + correlation intent; the
                        // durable EquipmentWear.ExplosionChargeSettled marker is
                        // stamped ONLY when DrainResolvedDamageCharges sees the
                        // result succeed. The in-flight guard prevents a second
                        // issue while one is unresolved (the post-load reconcile
                        // also issues if still unsettled).
                        if (!HasInFlightCharge(d.Building, DamageType.Explosion))
                        {
                            EnsureEcb(ref ecb, ref ecbCreated);
                            BudgetEmitter.QueueDeductWithDebtFallback(
                                ecb, d.EstimatedRepairCost, BudgetCategory.Repairs,
                                BudgetPriority.Damage, "DamageAccountingSystem",
                                DebtCategory.Infrastructure,
                                out var beExpl, BudgetResultMode.RetainResult);
                            ecb.AddComponent(beExpl, new DamageChargeBudgetIntent
                            {
                                Building = d.Building,
                                Type = DamageType.Explosion
                            });
                        }
                    }
                    else
                    {
                        // No pre-existing durable marker to reconcile from, so this
                        // rides the durable PendingOperation<DamageChargeRequest>
                        // outbox (DamageChargeRequest's stated purpose) — exactly-once
                        // via serialized PendingPhase + the OpRef correlation, never
                        // lost (W2 Invariant 5, OwnsDurableOutbox). No current
                        // producer should reach here (Operational is
                        // wave-display-only, Disaster cost=0, Explosion handled
                        // above, Fire emits its own outbox). The path stays durable
                        // if one does — but log loudly instead of asserting a
                        // comment, so a new paid producer is given an explicit
                        // reconcile owner rather than silently inheriting this one.
                        Log.Error(
                            $"Unexpected paid non-wave {d.Type} damage on building " +
                            $"{d.Building.Index} (${d.EstimatedRepairCost:N0}) took the generic " +
                            $"durable charge path — wire an explicit reconcile owner for {d.Type}.");
                        EnsureEcb(ref ecb, ref ecbCreated);
                        ecb.QueuePendingOperation(new DamageChargeRequest
                        {
                            Building = d.Building,
                            EstimatedRepairCost = d.EstimatedRepairCost,
                            Type = d.Type
                        });
                    }
                }

                if (Log.IsDebugEnabled)
                    Log.Debug($"Processed {d.Type} damage: building {d.Building.Index}, " +
                              $"cost ${d.EstimatedRepairCost:N0}, wave={d.IsWaveDamage}");

                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(entity);
            }
        }

        /// <summary>
        /// True if a RetainResult damage charge for (building, type) is already
        /// in flight (issued, no BudgetDeductResult yet). The phase/marker stays
        /// unsettled until the result lands, so this guard is what prevents a
        /// second issue from the in-session path racing the post-load reconcile.
        /// </summary>
        private bool HasInFlightCharge(in BuildingRef building, DamageType type)
        {
            foreach (var intentRef in
                SystemAPI.Query<RefRO<DamageChargeBudgetIntent>>()
                .WithNone<BudgetDeductResult>())
            {
                var it = intentRef.ValueRO;
                if (it.Type == type
                    && it.Building.Index == building.Index
                    && it.Building.Version == building.Version)
                    return true;
            }
            return false;
        }

        private bool HasOutstandingCharge(in BuildingRef building, DamageType type)
        {
            foreach (var intentRef in
                SystemAPI.Query<RefRO<DamageChargeBudgetIntent>>()
                .WithAll<BudgetDeductRequest>())
            {
                var it = intentRef.ValueRO;
                if (it.Type == type
                    && it.Building.Index == building.Index
                    && it.Building.Version == building.Version)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// W2 HIGH drain. RetainResult charges resolve to BudgetDeductResult
        /// (success in-session via debt fallback; expired to Succeeded=false on
        /// load by BudgetResolutionSystem.ValidateAfterLoad for any request not
        /// processed before the save). Settle the durable origin ONLY on success;
        /// on failure leave it unsettled so it is re-issued — never lost, never
        /// double (the failed request never deducted). Always destroy the
        /// resolved budget entity (RetainResult = consumer owns cleanup).
        /// </summary>
        private void DrainResolvedDamageCharges(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            if (m_ResolvedChargeQuery.IsEmpty)
                return;

            m_ReissuedExplosions.Clear();
            foreach (var (resultRef, intentRef, budgetEntity) in
                SystemAPI.Query<RefRO<BudgetDeductResult>, RefRO<DamageChargeBudgetIntent>>()
                .WithAll<BudgetDeductRequest>()
                .WithEntityAccess())
            {
                var intent = intentRef.ValueRO;
                bool ok = resultRef.ValueRO.Succeeded;

                if (intent.Type == DamageType.Explosion)
                {
                    if (ok)
                        SettleExplosionCharge(intent.Building);
                    else
                    {
                        // Expired/failed (typically purged-then-expired on load).
                        // ReconcileUnsettledExplosionCharges runs only once per
                        // load and may have already skipped (in-flight at the
                        // time), so the drain must self-heal here regardless of
                        // validator ordering — re-issue now (idempotent: guarded
                        // by !settled + !in-flight). Same-pass ECB reissues are
                        // invisible to HasInFlightCharge until playback, so this
                        // drain-local key prevents duplicate failed result entities
                        // from queuing more than one replacement.
                        long key = PackBuildingKey(intent.Building);
                        if (m_ReissuedExplosions.Add(key))
                            ReissueExplosionCharge(ref ecb, ref ecbCreated, intent.Building);
                    }
                }
                else
                {
                    // Fire / generic durable charge: resolve the EXACT originating
                    // PendingOperation via the serialized OpRef (vanilla-remapped
                    // on load) — never a (building,type) FIFO scan, so concurrent
                    // charges on the same building resolve to their own op.
                    if (!ResolvePendingOpCharge(ref ecb, ref ecbCreated, intent, ok))
                    {
                        Log.Warn(
                            $"Retained v1 damage charge result for {intent.Type} on building " +
                            $"{intent.Building.Index}:{intent.Building.Version} has no unambiguous pending op; preserving result entity for retry/quarantine");
                        continue;
                    }
                }

                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(budgetEntity);
            }
        }

        private static long PackBuildingKey(in BuildingRef building)
            => ((long)building.Index << 32) ^ (uint)building.Version;

        /// <summary>
        /// Re-issue an explosion charge from the durable EquipmentWear marker
        /// after a failed/expired result. Mirrors ReconcileUnsettledExplosionCharges
        /// for a single building; this is the per-result self-heal so the charge
        /// survives any validator ordering on load.
        /// </summary>
        private void ReissueExplosionCharge(ref EntityCommandBuffer ecb, ref bool ecbCreated, in BuildingRef building)
        {
            foreach (var wearRW in SystemAPI.Query<RefRW<EquipmentWear>>())
            {
                ref var wear = ref wearRW.ValueRW;
                if (wear.Building.Index != building.Index
                    || wear.Building.Version != building.Version)
                    continue;
                if (!wear.HasExploded || wear.ExplosionChargeSettled)
                    return;

                // F-16 (ACC-08): use the explosion repair cost resolved at
                // TriggerExplosion time and persisted on EquipmentWear, so a
                // config drift between save and load cannot change what the
                // player is charged. Fallback recompute for pre-fix saves where
                // SavedExplosionRepairCost is 0 (the v1 EquipmentWear block).
                long cost = wear.SavedExplosionRepairCost > 0
                    ? wear.SavedExplosionRepairCost
                    : (long)System.Math.Round(wear.SavedExplosionDamage * 100)
                      * BalanceConfig.Current.EquipmentWear.RepairCostPerPercent;
                if (cost <= 0)
                {
                    wear.ExplosionChargeSettled = true; // nothing owed
                    return;
                }
                if (HasInFlightCharge(wear.Building, DamageType.Explosion))
                    return; // a fresh charge is already pending

                EnsureEcb(ref ecb, ref ecbCreated);
                BudgetEmitter.QueueDeductWithDebtFallback(
                    ecb, cost, BudgetCategory.Repairs, BudgetPriority.Damage,
                    "DamageAccountingSystem", DebtCategory.Infrastructure,
                    out var beExpl, BudgetResultMode.RetainResult);
                ecb.AddComponent(beExpl, new DamageChargeBudgetIntent
                {
                    Building = wear.Building,
                    Type = DamageType.Explosion
                });
                return;
            }
        }

        private void SettleExplosionCharge(in BuildingRef building)
        {
            foreach (var wearRW in SystemAPI.Query<RefRW<EquipmentWear>>())
            {
                ref var wear = ref wearRW.ValueRW;
                if (!wear.HasExploded || wear.ExplosionChargeSettled)
                    continue;
                if (wear.Building.Index == building.Index
                    && wear.Building.Version == building.Version)
                {
                    // Deduction is durably committed (BRS wrote the result), so a
                    // synchronous settle is now correct and idempotent (re-drain
                    // on load finds it already settled and skips).
                    wear.ExplosionChargeSettled = true;
                    return;
                }
            }
        }

        /// <summary>
        /// F-14 (ACC-05): resolve the EXACT originating PendingOperation by its
        /// serialized OpRef (vanilla-remapped on load), not a (building,type) FIFO
        /// scan. Two concurrent charges on the same building now resolve to their
        /// own op — never confirm/destroy the wrong one.
        /// </summary>
        private bool ResolvePendingOpCharge(ref EntityCommandBuffer ecb, ref bool ecbCreated, in DamageChargeBudgetIntent intent, bool succeeded)
        {
            Entity opEntity = intent.OpRef.ToEntity();
            if (opEntity == Entity.Null
                && !TryFindLegacyPendingOp(intent.Building, intent.Type, out opEntity))
            {
                return false;
            }

            if (opEntity == Entity.Null
                || !SystemAPI.HasComponent<DamageChargeRequest>(opEntity)
                || !SystemAPI.HasComponent<PendingOperationTag>(opEntity)
                || !SystemAPI.HasComponent<PendingPhase>(opEntity))
                return false;

            if (SystemAPI.GetComponent<PendingPhase>(opEntity).Value != PendingPhaseValue.Applied)
                return false;

            if (succeeded)
            {
                // Charge durably done — close the outbox.
                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.SetComponent(opEntity, new PendingPhase { Value = PendingPhaseValue.Confirmed });
                ecb.DestroyEntity(opEntity);
            }
            else
            {
                // Expired on load / failed — re-issue. PendingOp is durable, so
                // this guarantees exactly-once eventually (the failed request
                // never deducted). Immediate write so the same-frame issue loop
                // re-issues without a one-frame stall.
                SystemAPI.SetComponent(opEntity, new PendingPhase { Value = PendingPhaseValue.Queued });
            }

            return true;
        }

        private bool TryFindLegacyPendingOp(in BuildingRef building, DamageType type, out Entity opEntity)
        {
            opEntity = Entity.Null;
            int matches = 0;

            foreach (var (chargeRef, phaseRef, entity) in
                SystemAPI.Query<RefRO<DamageChargeRequest>, RefRO<PendingPhase>>()
                    .WithAll<PendingOperationTag>()
                    .WithEntityAccess())
            {
                var charge = chargeRef.ValueRO;
                if (phaseRef.ValueRO.Value != PendingPhaseValue.Applied
                    || charge.Type != type
                    || charge.Building.Index != building.Index
                    || charge.Building.Version != building.Version)
                {
                    continue;
                }

                opEntity = entity;
                matches++;
                if (matches > 1)
                {
                    opEntity = Entity.Null;
                    return false;
                }
            }

            return matches == 1;
        }

        /// <summary>
        /// Destroy RepairCompletedEvent entities AFTER damage systems have consumed them.
        /// DamageAccountingSystem runs after PowerCapacityWriterGroup (which contains
        /// OperationalDamageSystem and PowerPlantDisasterSystem), so repair events
        /// have already been processed by all consumers. This is the single
        /// deterministic owner — the TTL sweeper (CommandRequestCleanupSystem) is
        /// only the true-orphan safety net, no longer the primary destroyer.
        /// </summary>
        private void CleanupRepairEvents(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<RepairCompletedEvent>>()
                .WithEntityAccess())
            {
                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(entity);
            }
        }

    }
}
