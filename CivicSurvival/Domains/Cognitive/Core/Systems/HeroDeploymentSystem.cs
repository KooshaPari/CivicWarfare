using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Features.Wellbeing;
#pragma warning disable CIVIC182 // Phase-neutral budget mutation helper lives with City budget service implementation.
using CivicSurvival.Services.City;
#pragma warning restore CIVIC182
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using Unity.Collections;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Domains.Cognitive.Ops.Countermeasures;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Sole owner of <see cref="HeroDeploymentState"/> singleton.
    ///
    /// Owns the entire hero ("The Voice") lifecycle: deploy / recall / mode switch,
    /// the budget round-trip via BudgetDeductRequest/Result, and the GretaDeployed
    /// penalty bookkeeping per district.
    ///
    /// Split out of CognitiveStateSystem so the cognitive infection/recovery loop
    /// owns its own singleton (CognitiveState) and the hero feature owns a separate
    /// one. CIVIC175 stays clean — each singleton has one writer.
    ///
    /// Ordered before CognitiveStateSystem so the throttled cognitive loop sees
    /// the latest HeroStatus when it computes effective rates.
    /// </summary>
    [SingletonOwner(typeof(HeroDeploymentState))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
    public partial class HeroDeploymentSystem : ThrottledSystemBase, IResettable, IPostLoadValidation, ICivicSingletonOwner<HeroDeploymentState>
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;

        private const double PenaltySystemRetryDelaySeconds = 60d;

        // Arestovych debt-collapse cooldown was calibrated at this sim speed; the lock scales by the
        // live speed at trigger so its real-time length is constant (Telemarathon shock idiom).
        private const float DEBT_CALIBRATION_SPEED = 4f;

        // Fallbacks when BalanceConfig is not yet loaded (mirror balance.contract.yaml Cognitive defaults).
        private const int FALLBACK_ARCHETYPE_SWITCH_COST = 20000;
        private const float FALLBACK_ARCHETYPE_SWITCH_COOLDOWN_HOURS = 12f;
        private const float FALLBACK_ARESTOVYCH_DEBT_RATE_PER_HOUR = 0.02f;
        private const float FALLBACK_ARESTOVYCH_DEBT_MAX = 1f;
        private const float FALLBACK_ARESTOVYCH_COLLAPSE_BASE_HOURS = 6f;
        private const float FALLBACK_ARESTOVYCH_COLLAPSE_DEBT_SCALE = 1f;
        private const float FALLBACK_ARESTOVYCH_COLLAPSE_COOLDOWN_HOURS = 24f;

        private static readonly LogContext Log = new("HeroDeploymentSystem");

        // Values feed telemetry (cognitive.hero_deployed / hero_mode_changed) and must match
        // the telemetry.contract.yaml enum: lowercase "deployed" / "lecturing". Uppercase here
        // made the server reject every hero_deployed event as an invalid enum value.
        private static string HeroStatusName(HeroStatus s) => s switch
        {
            HeroStatus.Inactive => "inactive",
            HeroStatus.Deployed => "deployed",
            HeroStatus.Lecturing => "lecturing",
            _ => "unknown"
        };

        private EntityQuery m_HeroStateQuery;
        private EntityQuery m_CognitiveStateQuery;
        private EntityQuery m_CurrentActQuery;
        private IShadowWalletService m_WalletService = NullShadowWalletService.Instance;
        private BufferLookup<CognitiveIntegrityBuffer> m_CogIntegrityBufferLookup;

        private DistrictPenaltySystem m_PenaltySystem = null!;
#pragma warning disable CIVIC324 // Runtime retry throttle; recomputed from live time and never part of hero save state.
        [System.NonSerialized] private double m_NextPenaltySystemRetryTime;
#pragma warning restore CIVIC324
        private Game.Simulation.SimulationSystem m_SimulationSystem = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            HeroDeploymentState.EnsureExists(EntityManager);

            m_HeroStateQuery = GetEntityQuery(ComponentType.ReadWrite<HeroDeploymentState>());
            m_CognitiveStateQuery = GetEntityQuery(ComponentType.ReadOnly<CognitiveState>());
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_CogIntegrityBufferLookup = GetBufferLookup<CognitiveIntegrityBuffer>(true);

            m_SimulationSystem = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();

            var eventBus = EventBus;
            if (eventBus != null)
                eventBus.Subscribe<DistrictLifecycleEvent>(OnDistrictLifecycle);


            Log.Info(" Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            HeroDeploymentState.EnsureExists(EntityManager);
            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            if (!TryResolvePenaltySystem(force: true))
                Log.Warn(" DistrictPenaltySystem not found — Gerda penalty unavailable");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                var eventBus = EventBus;
                eventBus?.Unsubscribe<DistrictLifecycleEvent>(OnDistrictLifecycle);
            }

            base.OnDestroy();
            Log.Info(" Destroyed");
        }

        protected override void OnThrottledUpdate()
        {
            if (!TryResolvePenaltySystem())
            {
                // Resolution still pending — OnStartRunning emitted the one-shot warn at boot,
                // the Try-method rate-limits actual lookups to once per 60s, and downstream
                // penalty paths (ApplyGerdaPenaltyToAllDistricts / OnDistrictLifecycle) all
                // defensively no-op on null. Continuing the update is safe.
            }

            // CIVIC185: keep buffer lookup current — ApplyGerdaPenaltyToAllDistricts may run from
            // event handlers (DistrictLifecycle, ResetState, ValidateAfterLoad) where stale lookup
            // would miss recent structural changes.
            m_CogIntegrityBufferLookup.Update(this);

            if (!SystemAPI.TryGetSingletonRW<HeroDeploymentState>(out var stateRef))
                return;

            AccrueArestovychDebt(ref stateRef.ValueRW);
        }

        // ============ Arestovych trust-debt ============

        /// <summary>
        /// While the Arestovych archetype is deployed, "trust debt" accrues with active time —
        /// the longer the fake-psychologist stays on air, the more catastrophic the eventual
        /// reality-check. Time via <see cref="GameRate"/>; clamped to the configured max.
        /// </summary>
        private void AccrueArestovychDebt(ref HeroDeploymentState state)
        {
            if (state.Archetype != HeroArchetype.Arestovych || state.HeroStatus == HeroStatus.Inactive)
                return;

            var cog = BalanceConfig.Current?.Cognitive;
            float ratePerHour = cog?.ArestovychDebtRatePerHour ?? FALLBACK_ARESTOVYCH_DEBT_RATE_PER_HOUR;
            float debtMax = cog?.ArestovychDebtMax ?? FALLBACK_ARESTOVYCH_DEBT_MAX;
            float dtHours = GameRate.HoursDelta(ThrottledDeltaSeconds);

            state.ArestovychTrustDebt = System.Math.Min(debtMax, state.ArestovychTrustDebt + ratePerHour * dtHours);
        }

        /// <summary>
        /// Apply a "major hit while active" to the Arestovych archetype — a FakeVideo (his
        /// counter-type) landing exposes the lie and collapses his instant-cut buff. Called
        /// synchronously by the PSYOPS launcher on FakeVideo land (one-writer respected: the write
        /// lives on the singleton owner, exactly like Telemarathon's <c>ApplyMediaTrustHit</c>).
        ///
        /// The collapse window grows with accumulated debt ("worse than before"); during it the
        /// resolver reads Arestovych's counter effectiveness as the floor (see
        /// MentalHealthResolverSystem). The cooldown is locked at trigger and scaled by the live
        /// sim speed so its real-time length is constant. No-op outside Arestovych / inactive /
        /// on cooldown / non-FakeVideo / time unavailable.
        /// </summary>
        public void ApplyArestovychMajorHit(PsyOpsAttackType type)
        {
            if (type != PsyOpsAttackType.FakeVideo)
                return;
            if (!m_HeroStateQuery.TryGetSingletonRW<HeroDeploymentState>(out var stateRef))
                return;

            ref var state = ref stateRef.ValueRW;
            if (state.Archetype != HeroArchetype.Arestovych || state.HeroStatus == HeroStatus.Inactive)
                return;

            if (!GameTimeSystem.TryGetGameHours(out var currentHour))
                return;
            if (currentHour < state.DebtCooldownEndHour)
                return; // a collapse already fired recently — anti-spam

            var cog = BalanceConfig.Current?.Cognitive;
            float baseHours = cog?.ArestovychCollapseBaseHours ?? FALLBACK_ARESTOVYCH_COLLAPSE_BASE_HOURS;
            float debtScale = cog?.ArestovychCollapseDebtScale ?? FALLBACK_ARESTOVYCH_COLLAPSE_DEBT_SCALE;
            float cooldownHours = cog?.ArestovychCollapseCooldownHours ?? FALLBACK_ARESTOVYCH_COLLAPSE_COOLDOWN_HOURS;

            float spentDebt = System.Math.Max(0f, state.ArestovychTrustDebt);
            float collapseHours = baseHours * (1f + spentDebt * debtScale);
            state.DebtCollapseEndHour = currentHour + collapseHours;

            float speed = System.Math.Max(m_SimulationSystem.selectedSpeed, 1f);
            state.DebtCooldownEndHour = currentHour + cooldownHours * (speed / DEBT_CALIBRATION_SPEED);

            state.ArestovychTrustDebt = 0f; // debt is spent in the collapse

            Log.Warn($" Arestovych trust-debt COLLAPSE (debt={spentDebt:F2}) — instant-cut suppressed for {collapseHours:F1}h");

            // The deepfake caught him out: the city reads his excuse in the feed, which is where
            // his credibility was built in the first place.
            HeroVoice.PostExposed(EventBus);
        }

        // ============ Synchronous command entry (AXIOM 14) ============

        /// <summary>
        /// Apply a player hero command synchronously — pause-safe entry point called from
        /// HeroActionIntakeSystem's ModificationEnd tick. Payment is host-direct through
        /// BudgetTransactionResolver (same idiom as <see cref="TrySetHeroArchetype"/>):
        /// every eligibility check runs BEFORE money moves, and the singleton mutation,
        /// penalty transition and events land in the same call — no retained budget
        /// intent, no refund path.
        /// </summary>
        public bool TryApplyHeroActionImmediate(HeroActionType action, HeroStatus mode, out FixedString64Bytes failReason)
        {
            failReason = default;
            if (!m_HeroStateQuery.TryGetSingletonRW<HeroDeploymentState>(out var stateRef))
            {
                failReason = ReasonIds.HeroSystemUnavailable.ToFixedString();
                return false;
            }

            // CIVIC185: this entry runs outside the throttled tick — refresh the buffer
            // lookup ApplyGerdaPenaltyToAllDistricts writes through.
            m_CogIntegrityBufferLookup.Update(this);

            switch (action)
            {
                case HeroActionType.Deploy:
                    return DeployImmediate(ref stateRef.ValueRW, mode, out failReason);
                case HeroActionType.Recall:
                    return RecallImmediate(ref stateRef.ValueRW, out failReason);
                case HeroActionType.SetMode:
                    return SetModeImmediate(ref stateRef.ValueRW, mode, out failReason);
                default:
                    failReason = ReasonIds.InternalError.ToFixedString();
                    return false;
            }
        }

        private static int ToHeroEventCost(long amount)
        {
            if (amount >= int.MaxValue)
                return int.MaxValue;
            if (amount <= int.MinValue)
                return int.MinValue;
            return checked((int)amount);
        }

        // ============ Hero Operations ============

        private bool DeployImmediate(
            ref HeroDeploymentState state,
            HeroStatus mode,
            out FixedString64Bytes failReason)
        {
            failReason = default;
            var currentAct = Act.Crisis;
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
                currentAct = actSingleton.CurrentAct;

            if (!HeroEligibility.CanDeployHero(
                    currentAct,
                    hasPendingHeroDeployBudget: false,
                    mode,
                    state.HeroStatus,
                    state.HeroDeployCost,
                    World,
                    out var reasonId))
            {
                Log.Info($"Cannot deploy hero: {reasonId}");
                failReason = reasonId;
                return false;
            }

            if (mode == HeroStatus.Inactive)
            {
                failReason = ReasonIds.InternalError.ToFixedString();
                return false;
            }

            int cost = state.HeroDeployCost;
            // Synchronous payment (AXIOM 14): eligibility above verified affordability;
            // the resolver deducts on the main thread so the deploy lands in this call.
            var payment = BudgetTransactionResolver.Deduct(
                World,
                m_WalletService,
                cost,
                BudgetCategory.CognitiveOps,
                "Cognitive.HeroDeploy");
            if (!payment.Succeeded)
            {
                Log.Info($" Cannot deploy hero: insufficient funds (need ${cost:N0})");
                failReason = ReasonIds.HeroInsufficientFunds.ToFixedString();
                return false;
            }

            state.HeroStatus = mode;
            if (mode == HeroStatus.Deployed && ArchetypeHasHappinessPenalty(state.Archetype))
                ApplyGerdaPenaltyToAllDistricts(register: true);

#pragma warning disable CIVIC062 // One-shot telemetry event, not per-frame
            EventBus?.SafePublish(new HeroDeployedEvent(
                HeroStatusName(mode),
                ToHeroEventCost(cost)), "HeroDeploymentSystem");
            EventBus?.SafePublish(new HeroModeChangedEvent(
                HeroStatusName(HeroStatus.Inactive),
                HeroStatusName(mode)), "HeroDeploymentSystem");
#pragma warning restore CIVIC062
            // The speaker opens his channel — the city hears who talks for it.
            HeroVoice.PostDeployed(EventBus, state.Archetype);

            string modeStr = mode == HeroStatus.Deployed ? "DEPLOYED (countering propaganda, -15% happiness)" : "LECTURING (boosting recovery)";
            Log.Info($" Hero {modeStr}, paid ${cost:N0}");
            return true;
        }

        private bool RecallImmediate(ref HeroDeploymentState state, out FixedString64Bytes failReason)
        {
            failReason = default;

            if (state.HeroStatus == HeroStatus.Inactive)
            {
                Log.Debug(" Hero not deployed, nothing to recall");
                failReason = ReasonIds.HeroNotDeployed.ToFixedString();
                return false;
            }

            if (state.HeroStatus == HeroStatus.Deployed && ArchetypeHasHappinessPenalty(state.Archetype))
                ApplyGerdaPenaltyToAllDistricts(register: false);

            var recalledArchetype = state.Archetype;
            state.HeroStatus = HeroStatus.Inactive;
            Log.Info(" Hero RECALLED");

            EventBus?.SafePublish(new HeroRecalledEvent(), "HeroDeploymentSystem");
            // The channel goes quiet — said in the feed, where the enemy keeps talking.
            HeroVoice.PostRecalled(EventBus, recalledArchetype);
            return true;
        }

        private bool SetModeImmediate(
            ref HeroDeploymentState state,
            HeroStatus mode,
            out FixedString64Bytes failReason)
        {
            failReason = default;

            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton) && actSingleton.CurrentAct == Act.PreWar)
            {
                failReason = ReasonIds.HeroPrewarLocked.ToFixedString();
                return false;
            }

            if (mode == HeroStatus.Inactive)
                return RecallImmediate(ref state, out failReason);

            if (state.HeroStatus == HeroStatus.Inactive)
                return DeployImmediate(ref state, mode, out failReason);

            var previousMode = state.HeroStatus;

            if (ArchetypeHasHappinessPenalty(state.Archetype))
            {
                if (previousMode == HeroStatus.Deployed && mode == HeroStatus.Lecturing)
                    ApplyGerdaPenaltyToAllDistricts(register: false);
                else if (previousMode == HeroStatus.Lecturing && mode == HeroStatus.Deployed)
                    ApplyGerdaPenaltyToAllDistricts(register: true);
            }

            state.HeroStatus = mode;

            string modeStr = mode == HeroStatus.Deployed ? "DEPLOYED (-50% infection, -15% happiness)" : "LECTURING (+50% recovery)";
            Log.Info($" Hero mode changed to {modeStr}");

#pragma warning disable CIVIC062 // One-shot telemetry event, not per-frame
            EventBus?.SafePublish(new HeroModeChangedEvent(
                previousMode.ToString().ToUpperInvariant(),
                mode.ToString().ToUpperInvariant()), "HeroDeploymentSystem");
#pragma warning restore CIVIC062

            return true;
        }

        /// <summary>
        /// Synchronously switch the selected speaker archetype from the UI thread (live
        /// response — pause-safe, Axiom 14). Free + instant while no hero is deployed (you are only
        /// choosing who to deploy). While deployed it is a <b>soft-switch with friction</b>: a budget
        /// cost + a cooldown, so a mixed raid cannot be answered by a free per-contact hot-swap. The
        /// Voice-only city-wide happiness penalty is transitioned in/out if the swap crosses the Voice
        /// archetype while Deployed.
        ///
        /// Mirrors the sync internet-mode command (<see cref="CognitiveStateSystem.TrySetInternetMode"/>)
        /// and the AA placement command service: the owner mutates its singleton in-call so the change
        /// lands even while the simulation is paused, without routing through the GameSimulation-phase
        /// request pipeline (which never ticks under pause). The cost is charged host-direct via
        /// <see cref="CityBudgetService.TryDeduct"/> — no deferred ECB — so it is paid under pause too;
        /// a failed deduct rejects before any state mutation (no refund path needed).
        /// </summary>
        public bool TrySetHeroArchetype(HeroArchetype target, out ReasonId reasonId)
        {
            reasonId = ReasonId.None;

            if (!m_HeroStateQuery.TryGetSingletonRW<HeroDeploymentState>(out var stateRef))
            {
                reasonId = ReasonIds.HeroSystemUnavailable;
                return false;
            }
            ref var state = ref stateRef.ValueRW;

            if (state.Archetype == target)
                return true; // already the selected speaker — no-op

            // No deployed hero → choosing the speaker is free and instant.
            if (state.HeroStatus == HeroStatus.Inactive)
            {
                state.Archetype = target;
                ForceNextUpdate();
                Log.Info($" Archetype selected: {target} (no active hero — free)");
                return true;
            }

            // Deployed/Lecturing → friction: cooldown + cost.
            if (!GameTimeSystem.TryGetGameHours(out var currentHour))
            {
                reasonId = ReasonIds.HeroSystemUnavailable;
                return false;
            }

            if (currentHour < state.ArchetypeSwitchCooldownEndHour)
            {
                reasonId = ReasonIds.HeroSwitchCooldown;
                return false;
            }

            var cog = BalanceConfig.Current?.Cognitive;
            int cost = cog?.ArchetypeSwitchCost ?? FALLBACK_ARCHETYPE_SWITCH_COST;
            float cooldownHours = cog?.ArchetypeSwitchCooldownHours ?? FALLBACK_ARCHETYPE_SWITCH_COOLDOWN_HOURS;

            if (cost > 0
                && CityBudgetService.TryDeduct(World, cost, BudgetCategory.CognitiveOps) != BudgetResult.Ok)
            {
                reasonId = ReasonIds.HeroInsufficientFunds;
                return false;
            }

            // Transition the Voice-only happiness penalty if the swap crosses Voice while Deployed.
            if (state.HeroStatus == HeroStatus.Deployed)
            {
                bool wasVoice = ArchetypeHasHappinessPenalty(state.Archetype);
                bool willVoice = ArchetypeHasHappinessPenalty(target);
                if (wasVoice && !willVoice)
                    ApplyGerdaPenaltyToAllDistricts(register: false);
                else if (!wasVoice && willVoice)
                    ApplyGerdaPenaltyToAllDistricts(register: true);
            }

            state.Archetype = target;
            state.ArchetypeSwitchCooldownEndHour = currentHour + cooldownHours;
            ForceNextUpdate();
            Log.Info($" Archetype switched to {target} synchronously (cost ${cost:N0}, cooldown {cooldownHours:F0}h)");
            return true;
        }

        /// <summary>
        /// Only the Voice/Gerda archetype carries the city-wide "lightning rod" happiness penalty.
        /// Arestovych and Patriot pay their cost through trust-debt and exodus instead.
        /// </summary>
        private static bool ArchetypeHasHappinessPenalty(HeroArchetype archetype)
            => archetype == HeroArchetype.Voice;

        private bool TryResolvePenaltySystem(bool force = false)
        {
            if (m_PenaltySystem != null)
                return true;

            double now = SystemAPI.Time.ElapsedTime;
            if (!force && now < m_NextPenaltySystemRetryTime)
                return false;

            m_NextPenaltySystemRetryTime = now + PenaltySystemRetryDelaySeconds;
            m_PenaltySystem = World.GetExistingSystemManaged<DistrictPenaltySystem>();
            return m_PenaltySystem != null;
        }

        /// <summary>
        /// Apply or remove Gerda penalty to all tracked districts.
        /// "Lightning Rod" mechanic: city-wide annoyance when she's countering propaganda.
        /// Reads CognitiveIntegrityBuffer from CognitiveState entity (owned by CognitiveStateSystem).
        /// </summary>
        private void ApplyGerdaPenaltyToAllDistricts(bool register)
        {
            if (m_PenaltySystem == null)
            {
                Log.Warn(" PenaltySystem not available, cannot apply Gerda penalty");
                return;
            }

            if (!m_CognitiveStateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return;

            m_CogIntegrityBufferLookup.Update(this);
            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var integrityBuffer))
                return;

            int count = 0;
            for (int i = 0; i < integrityBuffer.Length; i++)
            {
                int districtIndex = integrityBuffer[i].DistrictIndex;
                if (register)
                    m_PenaltySystem.RegisterPenalty(districtIndex, PenaltySource.GretaDeployed);
                else
                    m_PenaltySystem.RemovePenalty(districtIndex, PenaltySource.GretaDeployed);
                count++;
            }

            string action = register ? "registered" : "removed";
            Log.Info($" Gerda penalty {action} for {count} districts");
        }

        // ============ District Lifecycle ============

        private void OnDistrictLifecycle(DistrictLifecycleEvent evt)
        {
            if (!Enabled) return;
            if (m_PenaltySystem == null)
            {
                Log.Warn(" PenaltySystem not available — skipping Gerda penalty for district lifecycle event");
                return;
            }
            if (!m_HeroStateQuery.TryGetSingleton<HeroDeploymentState>(out var state)) return;
            if (state.HeroStatus != HeroStatus.Deployed || !ArchetypeHasHappinessPenalty(state.Archetype)) return;

            if (evt.Lifecycle == DistrictLifecycle.Created)
                m_PenaltySystem.RegisterPenalty(evt.DistrictIndex, PenaltySource.GretaDeployed);
            else if (evt.Lifecycle == DistrictLifecycle.Destroyed)
                m_PenaltySystem.RemovePenalty(evt.DistrictIndex, PenaltySource.GretaDeployed);
        }

        // ============ IResettable ============

        public void ResetState()
        {
            m_NextPenaltySystemRetryTime = 0d;

            if (!m_HeroStateQuery.TryGetSingletonRW<HeroDeploymentState>(out var stateRef))
                return;

            // Remove GretaDeployed penalties if hero was active (Voice archetype only)
            if (stateRef.ValueRO.HeroStatus == HeroStatus.Deployed && ArchetypeHasHappinessPenalty(stateRef.ValueRO.Archetype))
                ApplyGerdaPenaltyToAllDistricts(register: false);

            stateRef.ValueRW = HeroDeploymentState.Default;
            Log.Info(" State reset");
        }

        public bool CanDeployHero(HeroStatus mode, out string reasonId)
        {
            reasonId = "";
            if (!m_HeroStateQuery.TryGetSingleton<HeroDeploymentState>(out var state))
            {
                reasonId = ReasonIds.HeroSystemUnavailable;
                return false;
            }

            var currentAct = Act.Crisis;
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
                currentAct = actSingleton.CurrentAct;

            return HeroEligibility.CanDeployHero(
                currentAct,
                hasPendingHeroDeployBudget: false,
                mode,
                state.HeroStatus,
                state.HeroDeployCost,
                World,
                out reasonId);
        }

        public bool CanRecallHero(out string reasonId)
        {
            reasonId = "";
            if (!m_HeroStateQuery.TryGetSingleton<HeroDeploymentState>(out var state)
                || state.HeroStatus == HeroStatus.Inactive)
            {
                reasonId = ReasonIds.HeroNotDeployed;
                return false;
            }

            return true;
        }

        public bool CanSetHeroMode(HeroStatus mode, out string reasonId)
        {
            if (mode == HeroStatus.Inactive)
                return CanRecallHero(out reasonId);

            reasonId = "";
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton) && actSingleton.CurrentAct == Act.PreWar)
            {
                reasonId = ReasonIds.HeroPrewarLocked;
                return false;
            }

            if (!m_HeroStateQuery.TryGetSingleton<HeroDeploymentState>(out var state))
            {
                reasonId = ReasonIds.HeroSystemUnavailable;
                return false;
            }

            if (state.HeroStatus == HeroStatus.Inactive)
                return CanDeployHero(mode, out reasonId);

            return true;
        }

        // ============ IPostLoadValidation ============

        /// <summary>
        /// Restore GretaDeployed penalties immediately after load.
        /// HeroStatus is serialized; per-district penalty registrations are not.
        /// </summary>
        public void ValidateAfterLoad()
        {
            HeroDeploymentState.EnsureExists(EntityManager);
            if (!m_HeroStateQuery.TryGetSingletonEntity<HeroDeploymentState>(out var stateEntity)) return;

            if (!TryResolvePenaltySystem(force: true))
            {
                Log.Warn("DistrictPenaltySystem not found — cannot restore Gerda penalty");
                return;
            }

            if (!EntityManager.Exists(stateEntity)) return;
            var state = EntityManager.GetComponentData<HeroDeploymentState>(stateEntity);

            if (state.HeroStatus == HeroStatus.Deployed && ArchetypeHasHappinessPenalty(state.Archetype))
            {
                // HeroStatus + Archetype are serialized source-of-truth; restore the matching
                // non-serialized penalty even if the current act is pre-Crisis.
                ApplyGerdaPenaltyToAllDistricts(register: true);
                Log.Info("Post-load: restored GretaDeployed penalties");
            }
        }
    }
}
