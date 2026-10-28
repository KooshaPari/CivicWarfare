using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Finance.Systems
{
    /// <summary>
    /// System responsible for:
    /// 1. Processing monthly debt payments on day change
    /// 2. Serializing CityDebtService tracking data
    ///
    /// Uses config values for payment rate, minimum, and interest.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(InterestRateRegistry))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class CityDebtTrackingSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CityDebtTrackingSystem");
        private const int DAYS_PER_BILLING_CYCLE = 30;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private EntityQuery m_InterestRateRegistryQuery;
        private DayChangedDedup m_DayDedup = default;
        private int m_PendingBillingDay;
        private long m_PendingBillingDecisionDebt;
        private long m_PendingBillingPeriodIncome;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_InterestRateRegistryQuery = GetEntityQuery(ComponentType.ReadWrite<InterestRateRegistry>());

            InterestRateRegistry.EnsureExists(EntityManager);

            // Subscribe to day change for monthly payment processing
            SubscribeRequired<DayChangedEvent>(OnDayChanged, DayEventPriority.Cost);
            SubscribeRequired<DebtPaymentAppliedEvent>(OnDebtPaymentApplied);

            Log.Info(" Initialized");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            InterestRateRegistry.EnsureExists(EntityManager);
        }

        protected override void OnUpdateImpl()
        {
            // Event-driven, no per-frame logic
        }

        public void ValidateAfterLoad()
        {
            InterestRateRegistry.EnsureExists(EntityManager);
            CityDebtService.RestoreLastAppliedBillingDayFromLoad(m_DayDedup.LastProcessedDay);
            if (m_PendingBillingDay > 0 && !CityDebtService.IsBillingDayApplied(m_PendingBillingDay))
                QueueDebtPaymentRequest(m_PendingBillingDay, m_PendingBillingDecisionDebt, m_PendingBillingPeriodIncome);
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<DayChangedEvent>(OnDayChanged);
            UnsubscribeSafe<DebtPaymentAppliedEvent>(OnDebtPaymentApplied);
            base.OnDestroy();
        }

        private void OnDayChanged(DayChangedEvent evt)
        {
            if (m_DayDedup.IsProcessed(evt.DayNumber) || CityDebtService.IsBillingDayApplied(evt.DayNumber)) return;

            using var _ = PerformanceProfiler.Measure("CityDebt.OnDayChanged");

            // ProcessMonthlyPayment is designed for monthly frequency (interest, rates).
            // DayChangedEvent fires daily — only process on day 1 of each 30-day cycle.
            if (evt.DayNumber % DAYS_PER_BILLING_CYCLE != 0)
                return;

            long periodIncome = CityDebtService.GetRawPeriodIncome();
            long decisionDebt = CityDebtService.GetTotalDebt();

            // LOAD-INVARIANT: persist the billing decision before ECB playback.
            // The day marker advances only from DebtPaymentAppliedEvent.
            m_PendingBillingDay = evt.DayNumber;
            m_PendingBillingDecisionDebt = decisionDebt;
            m_PendingBillingPeriodIncome = periodIncome;
            QueueDebtPaymentRequest(evt.DayNumber, decisionDebt, periodIncome);

        }

        private void OnDebtPaymentApplied(DebtPaymentAppliedEvent evt)
        {
            CityDebtService.SetLastAppliedBillingDay(evt.BillingDay);
            m_DayDedup.MarkProcessed(evt.BillingDay);
            if (evt.BillingDay >= m_PendingBillingDay)
            {
                m_PendingBillingDay = 0;
                m_PendingBillingDecisionDebt = 0;
                m_PendingBillingPeriodIncome = 0;
            }
        }

        private static float ClampInterestRate(float rate)
            => System.Math.Min(System.Math.Max(rate, 0f), InterestRateRegistry.MAX_INTEREST_RATE);

        private void QueueDebtPaymentRequest(int billingDay, long decisionDebt, long periodIncome)
        {
            var debtConfig = BalanceConfig.Current.Debt;
            float resolvedInterestRate = ClampInterestRate(debtConfig.InterestRate);
            float resolvedRestructuredRate = ClampInterestRate(debtConfig.RestructuredRate);
#pragma warning disable CIVIC070 // Registry is transient computed state
            if (m_InterestRateRegistryQuery.TryGetSingletonRW<InterestRateRegistry>(out var regRW))
#pragma warning restore CIVIC070
            {
                regRW.ValueRW.Rate.Set(InterestRateRegistry.Source.Config, 1.0f);
                resolvedInterestRate = regRW.ValueRW.Rate.Resolve(debtConfig.InterestRate);
                resolvedRestructuredRate = regRW.ValueRW.Rate.Resolve(debtConfig.RestructuredRate);
            }

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new BudgetDebtPaymentRequest
            {
                Rate = debtConfig.MonthlyRate,
                Minimum = debtConfig.MinimumPayment,
                InterestRate = resolvedInterestRate,
                WarningRatio = debtConfig.WarningRatio,
                RestructureRatio = debtConfig.RestructureRatio,
                RestructuredRate = resolvedRestructuredRate,
                BillingDay = billingDay,
                DecisionDebt = decisionDebt,
                PeriodIncome = periodIncome
            });
            RequestMetaWriter.AddInternal(ecb, e, nameof(BudgetDebtPaymentRequest), billingDay.ToString());
        }
    }
}
