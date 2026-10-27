using System;
using Colossal.Logging;
using CivicSurvival.Core.Features.Wellbeing;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.City;
using Game.Simulation;
using Game.UI;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Debug;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Domains.Economics;
using CivicSurvival.Domains.Economics.Jobs;
using B = CivicSurvival.Core.UI.B;

namespace CivicSurvival.Domains.Economics.Systems
{
    /// <summary>
    /// Applies economic effects during Crisis act AND based on cognitive integrity.
    /// Subscribes to ActChangedEvent from Scenario domain.
    ///
    /// Effects:
    /// - Tax income: Crisis base (20%) × Integrity multiplier (100%→0%)
    /// - Commerce: Crisis base (30%) × Integrity multiplier (100%→10%)
    /// - Tourism: 5% of normal (via CityModifier.Attractiveness)
    /// - Loans: Blocked by setting Creditworthiness = 0 (direct ECS)
    ///
    /// Escalation Matrix (The Gradient):
    /// | Integrity | State       | Tax  | Consumption |
    /// |-----------|-------------|------|-------------|
    /// | 80-100%   | Loyal       | 100% | 100%        |
    /// | 50-80%    | Anxious     | 90%  | 120%        | ← Panic buying!
    /// | 30-50%    | Rebellious  | 60%  | 80%         | ← Tax strike
    /// | 10-30%    | Brainwashed | 20%  | 50%         | ← Collapse
    /// | <10%      | Zombie      | 0%   | 10%         | ← Failed state
    ///
    /// Architecture:
    /// - Pure reactive system — does NOT decide when acts change
    /// - Subscribes to ActChangedEvent via EventBus
    /// - Reads CognitiveIntegrityBuffer for city-wide integrity average
    /// - Exposes UI bindings for React components
    ///
    /// Update Order:
    /// EconomyDomain.Register orders this system after ScenarioStateMachine so
    /// scenario coordination is complete before this system reads it.
    /// </summary>
    [SingletonOwner(typeof(CommerceRateRegistry))]
    [SingletonOwner(typeof(EconomySingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class CrisisEconomicsSystem : CivicUISystemBase, IDefaultSerializable, IResettable, IPostLoadValidation, ICivicSingletonOwner<EconomySingleton>, ICivicSingletonOwner<CommerceRateRegistry>, IEconomyDebugMutator
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CrisisEconomics");

        // ===== Integrity Gradient Thresholds =====
        private const float LOYAL_THRESHOLD = 0.8f;
        private const float REBELLIOUS_THRESHOLD = 0.3f;

        // ===== Tax Multipliers (per integrity band) =====
        private const float ANXIOUS_TAX_MULT = 0.9f;
        private const float REBELLIOUS_TAX_MULT = 0.6f;
        private const float BRAINWASHED_TAX_MULT = 0.2f;

        // ===== Consumption Multipliers (per integrity band) =====
        private const float ANXIOUS_CONSUMPTION_MULT = 1.2f;
        private const float REBELLIOUS_CONSUMPTION_MULT = 0.8f;

        // ===== State (field order must match binary serialization order) =====
        private bool m_CrisisActive;
        private int m_CrisisStartDay;
        private bool m_LoansBlocked;
        private int m_SavedCreditworthiness;
        private bool m_HasSavedCreditworthiness;
        private bool m_TourismPenaltyApplied;
        private float m_PreWarCommercePenalty;
        private bool m_PreWarLoansBlocked;

        // ===== Dependencies (initialized in OnCreate) =====
        private GameTimeSystem? m_TimeProvider;

        // ===== Loan Blocking =====
        private CitySystem m_CitySystem = null!;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ComponentLookup<Creditworthiness> m_CreditworthinessLookup;

        // ===== Cognitive Integrity (The Gradient) =====
        private EntityQuery m_IntegrityQuery;
        private BufferLookup<CognitiveIntegrityBuffer> m_CogIntegrityBufferLookup;
        [NonSerialized] private float m_CityIntegrity = 1f;  // Cached, updated every frame
        [NonSerialized] private PopulationState m_PopulationState = PopulationState.Loyal;

        // ===== Cross-domain singleton for donor sanctions (Diplomacy) =====
        private EntityQuery m_SanctionsQuery;

        // ===== Cross-domain singleton for PowerBackup economy modulation =====
        private EntityQuery m_EconomyStateQuery;
        private ComponentLookup<EconomySingleton> m_EconomyWriteLookup;
        [NonSerialized] private CivicSingletonHandle<EconomySingleton> m_EconomyState;

        // ===== Throttle (UISystemBase can't use ThrottledSystemBase — manual via ThrottleHelper) =====
        private ThrottleHelper m_Throttle;

        // ===== UI Bindings (initialized in OnCreate) =====
        private ProfiledBinding<bool> m_ShockActActiveBinding = null!;
        private ProfiledBinding<float> m_TaxMultiplierBinding = null!;
        private ProfiledBinding<bool> m_LoansAvailableBinding = null!;
        private ProfiledBinding<int> m_CrisisDayBinding = null!;
        // ===== Crisis Multipliers (used by CrisisEconomicsAdapter) =====
        // Tax = CrisisBase × IntegrityMultiplier
        public float TaxMultiplier
        {
            get
            {
                float crisisBase = m_CrisisActive ? BalanceConfig.Current.Scenario.ShockTaxMultiplier : 1f;
                float integrityMult = GetIntegrityTaxMultiplier();
                return crisisBase * integrityMult;
            }
        }

        // Commerce multiplier: resolved through CommerceRateRegistry with floor/ceiling enforcement.
        // Factors: Crisis base, Integrity (consumption pattern), Sanctions, Internet mode, PreWar tension.
        public float CommerceMultiplier => m_CachedCommerceMultiplier;
        private float m_CachedCommerceMultiplier = 1f;

        /// <summary>
        /// Recompute all commerce factors and write them to the registry.
        /// Called from OnUpdateImpl() — once per throttled tick.
        /// </summary>
        private void UpdateCommerceRegistry()
        {
            float crisisBase = m_CrisisActive ? BalanceConfig.Current.Scenario.ShockCommerceMultiplier : 1f;
            float integrityMult = GetIntegrityConsumptionMultiplier();

            var sanctions = m_SanctionsQuery.TryGetSingleton<DonorSanctionsSingleton>(out var ds)
                ? ds : DonorSanctionsSingleton.Default;
            float sanctionMult = 1f - sanctions.TradePenalty;

            float internetPenalty = GetInternetCommercePenalty();
            float internetMult = 1f - internetPenalty;

            float preWarMult = 1f - m_PreWarCommercePenalty;

            // The rumor resource-run's economy bite. A panic-buying run on Food
            // drags city-wide commerce down (< 1) — the real economic harm, separate from the
            // belief/infection vector so the harm is not counted twice. Derived from
            // the Core ResourceRunAdapter (published by the Cognitive launcher); 1 = no run.
            float resourceRunMult = GetResourceRunCommerceMult();

#pragma warning disable CIVIC070 // Registry is transient computed state
            if (SystemAPI.TryGetSingletonRW<CommerceRateRegistry>(out var regRW))
#pragma warning restore CIVIC070
            {
                ref var reg = ref regRW.ValueRW;
                reg.Rate.Set(CommerceRateRegistry.Source.Crisis, crisisBase);
                reg.Rate.Set(CommerceRateRegistry.Source.Integrity, integrityMult);
                reg.Rate.Set(CommerceRateRegistry.Source.Sanctions, sanctionMult);
                reg.Rate.Set(CommerceRateRegistry.Source.Internet, internetMult);
                reg.Rate.Set(CommerceRateRegistry.Source.PreWar, preWarMult);
                reg.Rate.Set(CommerceRateRegistry.Source.ResourceRun, resourceRunMult);
                m_CachedCommerceMultiplier = reg.Rate.Resolve(1.0f);
            }
            else
            {
                // Fallback if registry not available
                m_CachedCommerceMultiplier = math.max(0.01f,
                    crisisBase * integrityMult * sanctionMult * internetMult * preWarMult * resourceRunMult);
            }

            CrisisEconomicsAdapter.PublishCommerceMultiplier(World, m_CachedCommerceMultiplier);
        }

        public float TourismMultiplier => m_CrisisActive ? BalanceConfig.Current.Scenario.ShockTourismMultiplier : 1f;
        // FIX S6-08: Also check m_LoansBlocked (defense-in-depth against future code paths)
        public bool LoansAvailable => !m_CrisisActive && !m_PreWarLoansBlocked && !m_LoansBlocked;
        public PopulationState CurrentPopulationState => m_PopulationState;
        public float CityIntegrity => m_CityIntegrity;

        // ===== Integrity → Tax Multiplier (The Gradient) =====
        private float GetIntegrityTaxMultiplier()
        {
            // | Integrity | State       | Tax  |
            // | 80-100%   | Loyal       | 100% |
            // | 50-80%    | Anxious     | 90%  |
            // | 30-50%    | Rebellious  | 60%  |
            // | 10-30%    | Brainwashed | 20%  |
            // | <10%      | Zombie      | 0%   |
            if (m_CityIntegrity >= LOYAL_THRESHOLD) return 1.0f;
            if (m_CityIntegrity >= 0.5f) return ANXIOUS_TAX_MULT;
            if (m_CityIntegrity >= REBELLIOUS_THRESHOLD) return REBELLIOUS_TAX_MULT;
            if (m_CityIntegrity >= 0.1f) return BRAINWASHED_TAX_MULT;
            return 0f;  // Tax Strike complete
        }

        // ===== Integrity → Consumption Multiplier (The Gradient) =====
        private float GetIntegrityConsumptionMultiplier()
        {
            // | Integrity | State       | Consumption |
            // | 80-100%   | Loyal       | 100%        |
            // | 50-80%    | Anxious     | 120%        | ← Panic buying!
            // | 30-50%    | Rebellious  | 80%         |
            // | 10-30%    | Brainwashed | 50%         |
            // | <10%      | Zombie      | 10%         |
            if (m_CityIntegrity >= LOYAL_THRESHOLD) return 1.0f;
            if (m_CityIntegrity >= 0.5f) return ANXIOUS_CONSUMPTION_MULT;  // Panic buying!
            if (m_CityIntegrity >= REBELLIOUS_THRESHOLD) return REBELLIOUS_CONSUMPTION_MULT;
            if (m_CityIntegrity >= 0.1f) return 0.5f;
            return 0.1f;  // Economy collapsed
        }

        // ===== BUG-5 FIX: Internet Mode → Commerce Penalty =====
        // NOTE: EntityQuery.TryGetSingleton calls CompleteDependencyBeforeRO — latent sync point
        // if any future Burst job writes CognitiveState. Convert to ComponentLookup if that happens.
        private float GetInternetCommercePenalty()
        {
            // Firewall: 10% penalty, Blackout: 25% penalty, Open: 0%
            var cogState = m_IntegrityQuery.TryGetSingleton<CognitiveState>(out var cs)
                ? cs : CognitiveState.Default;
            return cogState.CurrentCommercePenalty;
        }

        // ===== Rumor resource-run → Commerce bite =====
        // The active Food run's aggregate intensity (0-1) becomes a bounded commerce penalty:
        // mult = 1 - clamp(intensity * weight, 0, maxPenalty). The run intensity is derived and
        // republished each tick by the Cognitive launcher (single writer); this only reads it.
        private float GetResourceRunCommerceMult()
        {
            float intensity = ResourceRunAdapter.GetFoodRunIntensity(World);
            if (intensity <= 0f)
                return 1f;

            var cwCfg = BalanceConfig.Current?.Cognitive;
            if (cwCfg == null)
                return 1f;

            float penalty = math.clamp(intensity * cwCfg.ResourceRunCommerceWeight, 0f, cwCfg.ResourceRunCommerceMaxPenalty);
            return 1f - penalty;
        }

        // ===== Integrity → Population State =====
        private static PopulationState GetPopulationState(float integrity)
        {
            if (integrity >= LOYAL_THRESHOLD) return PopulationState.Loyal;
            if (integrity >= 0.5f) return PopulationState.Anxious;
            if (integrity >= REBELLIOUS_THRESHOLD) return PopulationState.Rebellious;
            if (integrity >= 0.1f) return PopulationState.Brainwashed;
            return PopulationState.Zombie;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Get dependencies
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_TimeProvider = GameTimeSystem.Instance;
            if (m_TimeProvider == null)
            {
                Log.Warn("[CrisisEconomics] GameTimeSystem not registered yet - time-dependent crisis actions will defer");
            }

            // Query for CognitiveState (to read integrity buffer)
            m_IntegrityQuery = GetEntityQuery(ComponentType.ReadOnly<CognitiveState>());
            m_CogIntegrityBufferLookup = GetBufferLookup<CognitiveIntegrityBuffer>(true);
            m_SanctionsQuery = GetEntityQuery(ComponentType.ReadOnly<DonorSanctionsSingleton>());

            // Create cross-domain singletons
            EconomySingleton.EnsureExists(EntityManager);
            CommerceRateRegistry.EnsureExists(EntityManager);
            m_EconomyStateQuery = GetEntityQuery(ComponentType.ReadWrite<EconomySingleton>());
            m_EconomyState = CreateSingletonHandle<EconomySingleton>(m_EconomyStateQuery);
            EnsureSingleton(ref m_EconomyState, EconomySingleton.Default);
            m_EconomyWriteLookup = GetComponentLookup<EconomySingleton>(false);
            m_CreditworthinessLookup = GetComponentLookup<Creditworthiness>(false);

            // Create UI bindings
            m_ShockActActiveBinding = new ProfiledBinding<bool>(B.Group, B.ShockActActive, false);
            m_TaxMultiplierBinding = new ProfiledBinding<float>(B.Group, B.TaxMultiplier, 1f);
            m_LoansAvailableBinding = new ProfiledBinding<bool>(B.Group, B.LoansAvailable, true);
            m_CrisisDayBinding = new ProfiledBinding<int>(B.Group, B.CrisisDayNumber, 0);
            AddBinding(m_ShockActActiveBinding.Binding);
            AddBinding(m_TaxMultiplierBinding.Binding);
            AddBinding(m_LoansAvailableBinding.Binding);
            AddBinding(m_CrisisDayBinding.Binding);
            // Throttle: economic values change slowly, 500ms is fine for UI
            m_Throttle = new ThrottleHelper(Engine.Timing.UPDATE_INTERVAL_500_MS);

            // BUG-E-003 FIX: Push initial state to UI immediately
            UpdateBindings();

            // Subscribe to orchestration
            SubscribeRequired<ActChangedEvent>(OnActChanged);

            // A2 FIX 2c: DonorEvent subscription removed — sanctions read from DonorSanctionsSingleton

            // S4-03 FIX: Subscribe to pre-war tension events (commerce penalty + loans disable)
            SubscribeRequired<PreWarTensionEvent>(OnPreWarTension);

            // S7-06 FIX: Post-load validation for stranded Creditworthiness
#if DEBUG
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IEconomyDebugMutator>(this);
#endif

            Log.Info("[CrisisEconomics] Created with UI bindings (The Gradient enabled)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            RestoreCrossDomainSingletons();
            ResyncCurrentActState();
        }

        protected override void OnDestroy()
        {
            CrisisEconomicsAdapter.ClearCommerceMultiplier(World);

            if (ServiceRegistry.IsInitialized)
            {
                UnsubscribeSafe<ActChangedEvent>(OnActChanged);
                // A2 FIX 2c: DonorEvent unsubscribe removed — sanctions read from singleton
                UnsubscribeSafe<PreWarTensionEvent>(OnPreWarTension);
#if DEBUG
                ServiceRegistry.Instance.Unregister<IEconomyDebugMutator>(this);
#endif
            }

            base.OnDestroy();
        }

        protected override void OnStopRunning()
        {
            CrisisEconomicsAdapter.ClearCommerceMultiplier(World);
            base.OnStopRunning();
        }

        /// <summary>
        /// React to act changes — activate/deactivate crisis economics.
        /// NOTE: Checks Enabled to respect UI toggle (EventBus handlers fire regardless of Enabled).
        /// </summary>
        private void OnActChanged(ActChangedEvent evt)
        {
            // BUG FIX: Respect UI toggle — if system is disabled, ignore event
            if (!Enabled)
            {
                Log.Info("[CrisisEconomics] Received ActChangedEvent but system is DISABLED - ignoring");
                return;
            }

            bool wasCrisis = m_CrisisActive;
            m_CrisisActive = (evt.NewAct == Act.Crisis);

            if (m_CrisisActive && !wasCrisis)
            {
                m_TimeProvider ??= GameTimeSystem.Instance;
                if (evt.CrisisStartDay <= 0 && m_TimeProvider == null)
                {
                    Log.Error("[CrisisEconomics] TimeProvider unavailable — cannot activate crisis mode (CrisisStartDay would be invalid)");
                    m_CrisisActive = false;
                    return;
                }
                // FIX S7-08: Use CrisisStartDay from event if available (post-load re-publish),
                // otherwise calculate from current day (fresh transition)
                m_CrisisStartDay = evt.CrisisStartDay > 0
                    ? evt.CrisisStartDay
                    : m_TimeProvider!.Current.CurrentDay;
                var scenario = BalanceConfig.Current.Scenario;
                Log.Warn($"[CrisisEconomics] CRISIS MODE ACTIVATED: Tax {scenario.ShockTaxMultiplier:P0}, Commerce {scenario.ShockCommerceMultiplier:P0}, Tourism {scenario.ShockTourismMultiplier:P0}, Loans BLOCKED (Day {m_CrisisStartDay})");
                BlockLoans();
                ApplyTourismPenalty();

                // S6-07 FIX: One-time emergency budget injection on fresh Crisis start (not post-load re-publish)
                if (evt.CrisisStartDay == 0)
                {
                    long injection = scenario.CrisisEmergencyInjection;
                    if (injection > 0)
                    {
                        var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        BudgetEmitter.QueueAddFunds(ecb, injection, BudgetSource.EmergencyFunding, BudgetIncomeKind.DonorOrEmergencyCredit);
                        // Main-thread ECB write — no job handle needed
                        Log.Warn($"[CrisisEconomics] S6-07: Emergency injection ${injection:N0} requested");
                    }
                }
            }
            else if (!m_CrisisActive && wasCrisis)
            {
                Log.Info("[CrisisEconomics] Normal economy restored");
                if (!m_PreWarLoansBlocked)
                    RestoreLoans();
                RemoveTourismPenalty();
            }

            // Update UI bindings
            UpdateBindings();
        }

        private void ResyncCurrentActState()
        {
            if (!SystemAPI.TryGetSingleton<CurrentActSingleton>(out var currentAct))
                return;

            m_TimeProvider ??= GameTimeSystem.Instance;
            int crisisStartDay = 0;
            double timestamp = 0.0;
            if (m_TimeProvider != null)
            {
                timestamp = m_TimeProvider.Current.TotalGameHours;
                if (currentAct.CurrentAct == Act.Crisis)
                    crisisStartDay = m_TimeProvider.Current.CurrentDay;
            }
            else if (currentAct.CurrentAct == Act.Crisis)
            {
                crisisStartDay = m_CrisisStartDay;
            }

            OnActChanged(new ActChangedEvent(m_CrisisActive ? Act.Crisis : Act.PreWar, currentAct.CurrentAct, timestamp, crisisStartDay));
        }

        // A2 FIX 2c: OnDonorEvent removed — sanctions read from DonorSanctionsSingleton

        /// <summary>
        /// S4-03 FIX: React to pre-war tension events — commerce penalty and loans disable.
        /// NOTE: Checks Enabled to respect UI toggle.
        /// </summary>
        private void OnPreWarTension(PreWarTensionEvent evt)
        {
            if (!Enabled)
                return;

            switch (evt.Effect)
            {
                case PreWarEffect.CommercePenalty:
                    m_PreWarCommercePenalty = evt.Value;
                    Log.Info($"[CrisisEconomics] Pre-war commerce penalty: -{evt.Value:P0}");
                    break;

                case PreWarEffect.LoansDisabled:
                    BlockLoans();
                    m_PreWarLoansBlocked = true;
                    Log.Info("[CrisisEconomics] Pre-war loans BLOCKED");
                    break;

                case PreWarEffect.WarStarted:
                    m_PreWarCommercePenalty = 0f;
                    // Only restore loans if pre-war blocked them AND crisis hasn't also blocked them
                    if (m_PreWarLoansBlocked && !m_CrisisActive)
                        RestoreLoans();
                    m_PreWarLoansBlocked = false;
                    Log.Info("[CrisisEconomics] Pre-war effects cleared (war started)");
                    break;

                default:
                    break; // HappinessPenalty handled by DistrictPenaltySystem
            }

            UpdateBindings();
        }

        // ===== Loan Blocking =====

        /// <summary>
        /// Block loans by setting Creditworthiness to 0.
        /// Stores original value for restoration when crisis ends.
        /// </summary>
        private void BlockLoans()
        {
            if (m_LoansBlocked)
                return;

            try
            {
                var city = m_CitySystem.City;
                if (city == Entity.Null || !EntityManager.Exists(city))
                {
                    Log.Warn("[CrisisEconomics] Cannot block loans: City entity not found");
                    return;
                }

                if (!EntityManager.HasComponent<Creditworthiness>(city))
                {
                    Log.Warn("[CrisisEconomics] Cannot block loans: City has no Creditworthiness component");
                    return;
                }

                var credit = EntityManager.GetComponentData<Creditworthiness>(city);
                m_SavedCreditworthiness = math.max(m_SavedCreditworthiness, credit.m_Amount);
                m_HasSavedCreditworthiness = true;
                credit.m_Amount = 0;
                EntityManager.SetComponentData(city, credit);
                m_LoansBlocked = true;

                Log.Info($"[CrisisEconomics] Loans BLOCKED (saved creditworthiness: ${m_SavedCreditworthiness:N0})");
            }
            catch (System.Exception ex)
            {
                Log.Exception("[CrisisEconomics] Error blocking loans", ex);
            }
        }

        /// <summary>
        /// Restore loans by setting Creditworthiness back to the last pre-block value.
        /// </summary>
        private void RestoreLoans()
        {
            if (!m_LoansBlocked)
                return;

            try
            {
                var city = m_CitySystem.City;
                if (city == Entity.Null || !EntityManager.Exists(city))
                {
                    Log.Warn("[CrisisEconomics] Cannot restore loans: City entity not found");
                    m_LoansBlocked = false;
                    m_SavedCreditworthiness = 0;
                    m_HasSavedCreditworthiness = false;
                    return;
                }

                if (!EntityManager.HasComponent<Creditworthiness>(city))
                {
                    Log.Warn("[CrisisEconomics] Cannot restore loans: City has no Creditworthiness component");
                    m_LoansBlocked = false;
                    m_SavedCreditworthiness = 0;
                    m_HasSavedCreditworthiness = false;
                    return;
                }

                var credit = EntityManager.GetComponentData<Creditworthiness>(city);
                int restoredCreditworthiness = m_HasSavedCreditworthiness
                    ? math.max(math.max(1, m_SavedCreditworthiness), credit.m_Amount)
                    : math.max(1, credit.m_Amount);
                credit.m_Amount = restoredCreditworthiness;
                EntityManager.SetComponentData(city, credit);
                m_LoansBlocked = false;
                m_SavedCreditworthiness = 0;
                m_HasSavedCreditworthiness = false;

                Log.Info($"[CrisisEconomics] Loans RESTORED (creditworthiness set to {credit.m_Amount:N0})");
            }
            catch (System.Exception ex)
            {
                Log.Exception("[CrisisEconomics] Error restoring loans", ex);
            }
        }

        // ===== Tourism Penalty =====
        // NOTE: We do NOT modify CityModifier.Attractiveness.
        // Attractiveness only affects TOURISM (TourismSystem), not emigration.
        // Emigration is controlled by Citizen.Happiness → HouseholdBehaviorSystem.
        // Tourism reduction handled via TourismMultiplier property.

        /// <summary>
        /// Mark tourism penalty as applied (no actual CityModifier change).
        /// Tourism effect is handled via TourismMultiplier property.
        /// </summary>
        private void ApplyTourismPenalty()
        {
            if (m_TourismPenaltyApplied)
                return;

            m_TourismPenaltyApplied = true;
            Log.Info($"[CrisisEconomics] Tourism penalty ACTIVE (multiplier: {TourismMultiplier:P0}) - NO attractiveness modifier (prevents mass emigration)");
        }

        /// <summary>
        /// Mark tourism penalty as removed.
        /// </summary>
        private void RemoveTourismPenalty()
        {
            if (!m_TourismPenaltyApplied)
                return;

            m_TourismPenaltyApplied = false;
            Log.Info("[CrisisEconomics] Tourism penalty REMOVED");
        }

        protected override void OnUpdateImpl()
        {
            if (!m_Throttle.ShouldUpdate())
                return;

            m_CogIntegrityBufferLookup.Update(this);

            // Update city integrity from CognitiveState
            UpdateCityIntegrity();

            // Update commerce registry with current factors (before UI bindings read it)
            UpdateCommerceRegistry();
            EnforceLoanCreditworthiness();

            // Update crisis day binding if crisis is active
            if (m_CrisisActive)
            {
                m_TimeProvider ??= GameTimeSystem.Instance;
                if (m_TimeProvider != null)
                {
                    int currentDay = m_TimeProvider.Current.CurrentDay;
                    int crisisDay = math.max(1, currentDay - m_CrisisStartDay + 1);
                    m_CrisisDayBinding.Update(crisisDay);
                }
            }

            // Log state changes
            var publishedState = GetPopulationState(m_CityIntegrity);
            if (publishedState != m_PopulationState)
            {
                Log.Warn($"[CrisisEconomics] Population state changed: {m_PopulationState} → {publishedState} (Integrity: {m_CityIntegrity:P0}, Tax: {TaxMultiplier:P0}, Consumption: {GetIntegrityConsumptionMultiplier():P0})");
                m_PopulationState = publishedState;
            }

            // Update integrity-based bindings (after PopulationState updated)
            UpdateBindings();

            m_EconomyWriteLookup.Update(this);
            var economyStateEntity = ResolveSingletonReadOnly(ref m_EconomyState);

            if (!m_EconomyStateQuery.IsEmptyIgnoreFilter
                && economyStateEntity != Entity.Null
                && m_EconomyWriteLookup.HasComponent(economyStateEntity))
            {
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre WriteEconomyStateJob.Schedule singleton={economyStateEntity} state={publishedState} cityIntegrity={m_CityIntegrity:P0}");
                Dependency = new WriteEconomyStateJob
                {
                    Lookup = m_EconomyWriteLookup,
                    SingletonEntity = economyStateEntity,
                    State = publishedState,
                    CityIntegrity = m_CityIntegrity
                }.Schedule(Dependency);
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post WriteEconomyStateJob.Schedule singleton={economyStateEntity} state={publishedState} cityIntegrity={m_CityIntegrity:P0}");
            }
        }

        private void EnforceLoanCreditworthiness()
        {
            var city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            m_CreditworthinessLookup.Update(this);
            if (!m_CreditworthinessLookup.HasComponent(city))
                return;

            var credit = m_CreditworthinessLookup[city];
            bool shouldBlock = m_LoansBlocked || m_PreWarLoansBlocked;
            if (shouldBlock && credit.m_Amount != 0)
            {
                // Crisis owns the temporary zeroing, not the absolute vanilla
                // creditworthiness value. Preserve progress earned while loans
                // are blocked so restore cannot roll it back to an older snapshot.
                m_SavedCreditworthiness = math.max(m_SavedCreditworthiness, math.max(1, credit.m_Amount));
                m_HasSavedCreditworthiness = true;
                credit.m_Amount = 0;
                m_CreditworthinessLookup[city] = credit;
            }
            else if (!shouldBlock && !m_CrisisActive && credit.m_Amount == 0)
            {
                credit.m_Amount = m_HasSavedCreditworthiness
                    ? math.max(1, m_SavedCreditworthiness)
                    : 1;
                m_CreditworthinessLookup[city] = credit;
                m_SavedCreditworthiness = 0;
                m_HasSavedCreditworthiness = false;
            }
        }

        /// <summary>
        /// Calculate city-wide average integrity from CognitiveIntegrityBuffer.
        /// </summary>
        private void UpdateCityIntegrity()
        {
            if (!m_IntegrityQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
            {
                m_CityIntegrity = 1f;  // Default to full integrity if no cognitive warfare
                return;
            }

            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var buffer))
            {
                m_CityIntegrity = 1f;
                return;
            }
            if (buffer.Length == 0)
            {
                m_CityIntegrity = 1f;
                return;
            }

            // Calculate average integrity across all districts
            float totalIntegrity = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                totalIntegrity += buffer[i].Integrity;
            }

            m_CityIntegrity = totalIntegrity / buffer.Length;
        }

        /// <summary>
        /// Update UI bindings with current economics state.
        /// </summary>
        private void UpdateBindings()
        {
            m_ShockActActiveBinding.Update(m_CrisisActive);
            m_TaxMultiplierBinding.Update(TaxMultiplier);
            m_LoansAvailableBinding.Update(LoansAvailable);
            // FIX S6-01: Send full CommerceMultiplier (all 5 factors) instead of just integrity
            // Reset crisis day when not in crisis
            if (!m_CrisisActive)
            {
                m_CrisisDayBinding.Update(0);
            }
        }
    }
}
