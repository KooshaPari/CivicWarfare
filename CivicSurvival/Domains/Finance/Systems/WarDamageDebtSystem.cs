using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Finance.Systems
{
    /// <summary>
    /// Charges DamageCost (building destruction) when waves end.
    /// Deducts from budget if possible, otherwise adds to city debt (WarDamage category).
    ///
    /// Only charges evt.DamageCost — NOT evt.InfrastructureDamageCost.
    /// InfrastructureDamageCost is an estimate for debriefing UI; real infra repair payment
    /// happens via PlantRepairService when the player initiates repair.
    ///
    /// Subscribes to: WaveEndedEvent
    /// </summary>
    [ActIndependent]
    public partial class WarDamageDebtSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("WarDamageDebtSystem");
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private EntityQuery m_EmergencyFundQuery;
        private EntityQuery m_ResolvedChargeQuery;
        private EntityQuery m_OutstandingChargeQuery;
        [NonEntityIndex] private readonly HashSet<int> m_SettledWaveExceptions = new(); // wave numbers, not Entity.Index
        private int m_LastChargedWaveNumber = -1;
        private int m_LastSettledWaveNumber = -1;
        private int m_PendingWaveNumber = -1;
        private long m_PendingDamageCost;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_EmergencyFundQuery = GetEntityQuery(ComponentType.ReadOnly<EmergencyFundSingleton>());
            m_ResolvedChargeQuery = GetEntityQuery(
                ComponentType.ReadOnly<BudgetDeductRequest>(),
                ComponentType.ReadOnly<BudgetDeductResult>(),
                ComponentType.ReadOnly<WarDamageBudgetIntent>());
            // Context-free scan for the post-load / wave-ended paths (those run outside OnUpdate,
            // where SystemAPI.Query would bind to the wrong system).
            m_OutstandingChargeQuery = GetEntityQuery(
                ComponentType.ReadOnly<WarDamageBudgetIntent>(),
                ComponentType.ReadOnly<BudgetDeductRequest>());

            // Subscribe to wave ended event
            SubscribeRequired<WaveEndedEvent>(OnWaveEnded);

            Log.Info("Initialized");
        }

        protected override void OnUpdateImpl()
        {
            if (m_ResolvedChargeQuery.IsEmpty)
                return;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            DrainResolvedCharges(ref ecb, ref ecbCreated);
            if (ecbCreated)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        public void ValidateAfterLoad()
        {
            if (m_PendingWaveNumber < 0 || m_PendingDamageCost <= 0)
                return;
            if (HasOutstandingChargeContextFree(m_PendingWaveNumber))
                return;

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            IssueCharge(ecb, m_PendingWaveNumber, m_PendingDamageCost);
            Log.Info($"ValidateAfterLoad: re-issued pending wave damage charge for wave {m_PendingWaveNumber}");
        }

        protected override void OnDestroy()
        {
            // Unsubscribe to prevent memory leak
            UnsubscribeSafe<WaveEndedEvent>(OnWaveEnded);
            base.OnDestroy();
        }

        private void OnWaveEnded(WaveEndedEvent evt)
        {
            if (evt.DamageCost <= 0)
            {
                PublishWaveSettled(evt.WaveNumber);
                return;
            }
            if (evt.WaveNumber <= m_LastChargedWaveNumber)
            {
                if (Log.IsDebugEnabled) Log.Debug($"Skipping duplicate damage charge for wave {evt.WaveNumber}");
                PublishWaveSettled(evt.WaveNumber);
                return;
            }
            if (evt.WaveNumber == m_PendingWaveNumber || HasOutstandingChargeContextFree(evt.WaveNumber))
            {
                if (Log.IsDebugEnabled) Log.Debug($"Skipping duplicate pending damage charge for wave {evt.WaveNumber}");
                return;
            }

            float emergencyFundMultiplier = m_EmergencyFundQuery.TryGetSingleton<EmergencyFundSingleton>(out var emergencyFund)
                ? emergencyFund.DisasterPenaltyMultiplier
                : 1f;
            long damageCost = (long)System.Math.Ceiling(evt.DamageCost * System.Math.Max(1f, emergencyFundMultiplier));

            if (Log.IsDebugEnabled) Log.Debug($"Wave {evt.WaveNumber} damage cost: ${damageCost:N0}");

            m_PendingWaveNumber = evt.WaveNumber;
            m_PendingDamageCost = damageCost;

            // Create retained budget deduction request with debt fallback via ECB.
            // ACCOUNTING-INVARIANT: LastChargedWaveNumber advances only after BRS
            // confirms budget/debt mutation through BudgetDeductResult.
            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            IssueCharge(ecb, evt.WaveNumber, damageCost);

            // Main-thread ECB write — no job handle needed
            Log.Info($"Queued ${damageCost:N0} damage cost for wave {evt.WaveNumber}");
        }

        private void IssueCharge(EntityCommandBuffer ecb, int waveNumber, long damageCost)
        {
            BudgetEmitter.QueueDeductWithDebtFallback(
                ecb,
                damageCost,
                BudgetCategory.Repairs,
                BudgetPriority.Damage,
                "WarDamageDebtSystem",
                DebtCategory.WarDamage,
                out var budgetEntity,
                BudgetResultMode.RetainResult);
            ecb.AddComponent(budgetEntity, new WarDamageBudgetIntent
            {
                WaveNumber = waveNumber,
                Amount = damageCost
            });
        }

        private void DrainResolvedCharges(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            foreach (var (resultRef, intentRef, entity) in
                SystemAPI.Query<RefRO<BudgetDeductResult>, RefRW<WarDamageBudgetIntent>>()
                .WithAll<BudgetDeductRequest>()
                .WithEntityAccess())
            {
                var result = resultRef.ValueRO;
                var intent = intentRef.ValueRO;

                if (result.Succeeded)
                {
                    if (intent.WaveNumber >= m_LastChargedWaveNumber)
                        m_LastChargedWaveNumber = intent.WaveNumber;
                    if (intent.WaveNumber == m_PendingWaveNumber)
                    {
                        m_PendingWaveNumber = -1;
                        m_PendingDamageCost = 0;
                    }
                    PublishWaveSettled(intent.WaveNumber);
                    Log.Info($"Confirmed ${intent.Amount:N0} damage cost for wave {intent.WaveNumber}");
                }
                else if (intent.Amount > 0
                    && !intent.ReissueQueued
                    && !HasOutstandingChargeInUpdate(intent.WaveNumber, excludeEntity: entity))
                {
                    intent.ReissueQueued = true;
                    intentRef.ValueRW = intent;
                    EnsureEcb(ref ecb, ref ecbCreated);
                    IssueCharge(ecb, intent.WaveNumber, intent.Amount);
                    Log.Warn($"Re-issued failed damage cost for wave {intent.WaveNumber}");
                }

                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(entity);
            }
        }

        private void PublishWaveSettled(int waveNumber)
        {
            if (waveNumber <= m_LastSettledWaveNumber)
                return;

            if (waveNumber == m_LastSettledWaveNumber + 1)
            {
                EventBus?.SafePublish(new WaveSettledEvent(waveNumber), nameof(WarDamageDebtSystem));
                AdvanceSettledThreshold(waveNumber);
                if (Log.IsDebugEnabled)
                    Log.Debug($"Published WaveSettledEvent for wave {waveNumber}");
                return;
            }

            if (!m_SettledWaveExceptions.Add(waveNumber))
                return;

            EventBus?.SafePublish(new WaveSettledEvent(waveNumber), nameof(WarDamageDebtSystem));
            if (Log.IsDebugEnabled)
                Log.Debug($"Published WaveSettledEvent for wave {waveNumber}");
        }

        private void AdvanceSettledThreshold(int waveNumber)
        {
            if (waveNumber > m_LastSettledWaveNumber)
                m_LastSettledWaveNumber = waveNumber;
            while (m_SettledWaveExceptions.Remove(m_LastSettledWaveNumber + 1))
                m_LastSettledWaveNumber++;
        }

        // Called from ValidateAfterLoad and the WaveEndedEvent handler — both run outside this
        // system's OnUpdate, so the scan must be context-free (cached query), not SystemAPI.Query.
        private bool HasOutstandingChargeContextFree(int waveNumber)
        {
            using var intents = m_OutstandingChargeQuery.ToComponentDataArray<WarDamageBudgetIntent>(Allocator.Temp);
            for (int i = 0; i < intents.Length; i++)
            {
                if (intents[i].WaveNumber == waveNumber)
                    return true;
            }
            return false;
        }

        // Called only from the OnUpdate drain, where SystemAPI.Query binds to this system correctly
        // and ToComponentDataArray would be a CIVIC218 sync point.
        private bool HasOutstandingChargeInUpdate(int waveNumber, Entity excludeEntity)
        {
            foreach (var (intentRef, entity) in
                SystemAPI.Query<RefRO<WarDamageBudgetIntent>>()
                .WithAll<BudgetDeductRequest>()
                .WithEntityAccess())
            {
                if (entity == excludeEntity)
                    continue;
                if (intentRef.ValueRO.WaveNumber == waveNumber)
                    return true;
            }
            return false;
        }

        private void EnsureEcb(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            if (ecbCreated)
                return;

            ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            ecbCreated = true;
        }
    }
}
