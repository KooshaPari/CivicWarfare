using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Components.Domain.Diplomacy;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Domain.Corruption;
using CivicSurvival.Core.Interfaces.Domain.Debug;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// Shadow Reputation System - Criminal Trust Level ECS Implementation.
    ///
    /// Replaces ShadowReputationService with proper ECS system + own serialization.
    /// Provides reputation service methods for corruption systems.
    /// Implements IShadowReputationService for cross-domain decoupling.
    ///
    /// Criminal world logic: constant rejections = you're not "one of us" anymore.
    ///
    /// Trust Levels:
    ///   0-25:  "Frozen out" — no proactive offers for 30+ days
    ///  26-50:  Normal frequency, basic offers only
    ///  51-75:  +50% offer frequency, better deals available
    /// 76-100:  "Inner circle" — exclusive high-risk/high-reward schemes
    ///
    /// Throttling (ThrottledSystemBase, ~500ms): all OnUpdate work is day-granularity
    /// — passive recovery, freeze timers and the UI singleton key off GameTimeSystem day
    /// boundaries, not per-tick state — so a coarse interval changes nothing it owns.
    /// Two read channels, intentionally asymmetric:
    ///   • IShadowReputationService getters (CurrentTrustLevel/IsFrozenOut/etc.) read the
    ///     m_TrustLevel field directly, so synchronous writers (OnOfferAccepted/OnCaught/
    ///     ModifyTrust) are reflected immediately — the throttle never delays them. All
    ///     sim-side consumers (Mobilization, MaintenanceContract, DistrictModernization,
    ///     ContractResponse, CmChoiceProcessor) use this channel.
    ///   • ReputationStateSingleton is rewritten only on a throttle tick; its consumers are
    ///     UI-only (ShadowReputationUISystem, ReputationDto), where ≤500ms staleness on a
    ///     day-granularity value is invisible.
    /// TrustModificationRequest is drained in batch on the throttle tick. Its sole producer
    /// is BuckwheatSystem (fire-and-forget food-aid bonus; it never reads the result back),
    /// and the request TTL (600 frames) far exceeds the interval, so throttling can neither
    /// expire a pending request nor strand a read-after-write within one flow.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(ReputationStateSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class ShadowReputationSystem : ThrottledSystemBase, IDefaultSerializable, IResettable, IShadowReputationService, IPostLoadValidation
#if DEBUG
        , IReputationDebugMutator
#endif
    {
        public void PurgeAfterLoad() { }

        private const float TRUSTED_THRESHOLD = 50f;

        private static readonly LogContext Log = new("ShadowReputation");

        // ============================================================================
        // STATE (serialized)
        // ============================================================================

        private float m_TrustLevel;
        private float m_FrozenUntilDay;
        private int m_TotalOffersAccepted;
        private int m_TotalOffersRejected;
        private int m_TotalSchemesSuccessful;
        private int m_TotalTimesCaught;
        private int m_LastPassiveRecoveryDay; // S12a-7: tracks last day passive recovery was applied

        // ============================================================================
        // DEPENDENCIES
        // ============================================================================

        private GameTimeSystem? m_GameTimeSystem;
        private EntityQuery m_ReputationSingletonQuery;
        private EntityQuery m_TrustRequestQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private IShadowWalletService m_WalletService = null!; // H37: direct freeze/unfreeze (no ECB delay)
        private ComponentLookup<ReputationStateSingleton> m_ReputationSingletonLookup;

        // Bundle for IShadowReputationService entry points. Owns lookup freshness for
        // ReputationStateSingleton writes so external callers (offer/scheme outcomes)
        // never race a structural change between Update and the singleton write.
        // Initialized once in OnCreate; survives ResetState (no per-session mutable state).
        [System.NonSerialized] private CivicServiceLookups m_ReputationLookups = null!;

        // FIX W1-H2: Suppress per-call wallet control during OnThrottledUpdate to prevent
        // conflicting Freeze+Unfreeze in same ECB batch. Single check at end of frame.
        [System.NonSerialized] private bool m_SuppressWalletControl;
        [System.NonSerialized] private bool m_WalletFreezeDesiredInitialized;
        [System.NonSerialized] private bool m_LastWalletFreezeDesired;

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        protected override void OnCreate()
        {
            base.OnCreate();

            // Domain-Driven Initialization
            ReputationStateSingleton.EnsureExists(EntityManager);

            m_ReputationSingletonQuery = GetEntityQuery(ComponentType.ReadWrite<ReputationStateSingleton>());
            m_TrustRequestQuery = GetEntityQuery(ComponentType.ReadOnly<TrustModificationRequest>());
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            // m_WalletService wired in OnThrottledUpdate / ValidateAfterLoad — TryGetOrNullObject
            // throws here because FeatureRegistry registration is still in progress.
            m_ReputationSingletonLookup = GetComponentLookup<ReputationStateSingleton>(false);

            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_ReputationLookups = new CivicServiceLookups(() =>
            {
                m_ReputationSingletonLookup.Update(this);
            });

            // Initialize with defaults
            ResetState();

            // Register interface for cross-domain access
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IShadowReputationService>(this);
#if DEBUG
                ServiceRegistry.Instance.Register<IReputationDebugMutator>(this);
#endif
            }

            Log.Info($"{nameof(ShadowReputationSystem)} created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_GameTimeSystem ??= GameTimeSystem.Instance;
        }

        // Trust is a day-granularity value: passive recovery, freeze timers and the UI
        // singleton all key off GameTimeSystem day boundaries, not per-tick state. The
        // synchronous IShadowReputationService entry points (OnOfferAccepted/OnCaught/
        // ModifyTrust) apply wallet freeze immediately on their own call path, so the
        // throttled OnUpdate only owns request draining + day-boundary reconciliation —
        // neither needs frame precision. TrustModificationRequest TTL is 600 frames,
        // far beyond this interval, so throttling cannot expire a pending request.
        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;

        protected override void OnThrottledUpdate()
        {
            m_GameTimeSystem ??= GameTimeSystem.Instance;
#pragma warning disable CIVIC256 // Retry-friendly GameTime activation: Instance is published after vanilla TimeSystem resolves.
            if (m_GameTimeSystem == null)
                return;
#pragma warning restore CIVIC256

            ResolveWalletService();
            ReconcileLowTrustWalletFreeze();

            // FIX W1-H2: Suppress per-call wallet control during batch processing.
            // All ModifyTrust calls update m_TrustLevel but skip ECB emission.
            // Single threshold check at end emits at most ONE Freeze or Unfreeze.
            var srCfg = BalanceConfig.Current.ShadowReputation;
            float trustAtFrameStart = m_TrustLevel;
            m_SuppressWalletControl = true;

            // Process cross-domain trust modification requests (Data-Driven Commands)
            ProcessTrustRequests();

            // S12a-7 FIX: Passive trust recovery while frozen — prevents permanent soft-lock.
            // FIX W2-M3: Gate on trustAtFrameStart — only recover if ALREADY frozen at frame start,
            // not if trust just dropped below threshold this frame (prevents free +1f offset on penalty)
            if (trustAtFrameStart < srCfg.FrozenThreshold && m_TrustLevel < srCfg.FrozenThreshold
                && srCfg.FrozenPassiveTrustRecovery > 0f)
            {
                float rawDay = GetCurrentGameDay();
                if (rawDay > 0f)
                {
                    int currentDay = (int)rawDay;
                    if (currentDay > m_LastPassiveRecoveryDay)
                    {
                        m_LastPassiveRecoveryDay = currentDay;
                        ModifyTrust(srCfg.FrozenPassiveTrustRecovery, "passive_recovery");
                    }
                }
            }

            m_SuppressWalletControl = false;

            // Single freeze/unfreeze decision based on NET threshold crossing this frame
            float threshold = srCfg.FrozenThreshold;
            bool wasFrozen = trustAtFrameStart < threshold;
            bool isFrozen = m_TrustLevel < threshold;
            if (!wasFrozen && isFrozen)
            {
                float currentGameDay = GetCurrentGameDay();
                if (currentGameDay >= m_FrozenUntilDay)
                    m_FrozenUntilDay = currentGameDay + srCfg.FreezeDurationDays;
                // FIX W6-M4: Init passive recovery baseline so first tick fires NEXT day, not immediately
                m_LastPassiveRecoveryDay = (int)currentGameDay;
                // H37: Direct call eliminates 1-frame ECB delay — consumers see IsFrozen=true immediately
                ApplyLowTrustWalletFreeze(true);
                Log.Warn($"Player frozen out until day {m_FrozenUntilDay:F1}");
            }
            // FIX W2-M8: Don't unfreeze wallet until freeze duration also expires
            // (prevents 5-day contradictory state: wallet unfrozen but IsFrozenOut=true)
            else if (wasFrozen && !isFrozen && GetCurrentGameDay() >= m_FrozenUntilDay)
            {
                m_FrozenUntilDay = 0f; // FIX W9-M2: Clear timer on threshold unfreeze
                ApplyLowTrustWalletFreeze(false);
                Log.Info("Trust recovered above threshold and freeze expired, wallet unfreezing");
            }

            // FIX W10-H1: Reconcile frozen timer + send Unfreeze.
            // Covers: trust recovered (via passive recovery or external call) BEFORE timer expired.
            // In that case wasFrozen=false on subsequent frames → normal unfreeze branch never fires.
            // When timer finally expires, this block detects "timer stale + not frozen" and unfreezes.
            // Duplicate Unfreeze is safe — ShadowWalletSystem.Unfreeze() guards with !IsFrozen.
            float currentGameDayForFreeze = GetCurrentGameDay();
            if (m_FrozenUntilDay > 0f
                && currentGameDayForFreeze > 0f
                && currentGameDayForFreeze >= m_FrozenUntilDay
                && !IsFrozenOut)
            {
                m_FrozenUntilDay = 0f;
                ApplyLowTrustWalletFreeze(false);
                Log.Info("Freeze timer expired with trust above threshold — wallet unfreeze requested");
            }

            // FIX W7-H1: Always refresh singleton — IsFrozenOut has a time-based component
            // (currentGameDay < m_FrozenUntilDay) that changes without any ModifyTrust call.
            // Without this, UI shows stale "Frozen" indefinitely after freeze expires by time.
            UpdateReputationSingleton();
        }

        /// <summary>
        /// Process TrustModificationRequest entities from other domains.
        /// Uses ECB for deferred entity destruction.
        /// </summary>
        private void ProcessTrustRequests()
        {
            if (m_TrustRequestQuery.IsEmpty) return;

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<TrustModificationRequest>>()
                .WithEntityAccess())
            {
                if (!hasEcb)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    hasEcb = true;
                }

                ModifyTrust(request.ValueRO.Amount, request.ValueRO.Reason.ToString());
                ecb.DestroyEntity(entity);
            }

            if (hasEcb)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        protected override void OnDestroy()
        {
            // Unregister interface
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IShadowReputationService>(this);
#if DEBUG
                ServiceRegistry.Instance.Unregister<IReputationDebugMutator>(this);
#endif
            }

            Log.Info($"{nameof(ShadowReputationSystem)} destroyed");
            base.OnDestroy();
        }

        // ============================================================================
        // Public API
        // ============================================================================

        public float CurrentTrustLevel => m_TrustLevel;

        public bool IsFrozenOut
        {
            get
            {
                if (m_TrustLevel < BalanceConfig.Current.ShadowReputation.FrozenThreshold)
                    return true;

                float currentGameDay = GetCurrentGameDay();
                // FIX W7-M7: Guard early lifecycle — GameTimeSystem returns 0f before init.
                // Without this, any save with m_FrozenUntilDay > 0 reports frozen on load.
                if (currentGameDay <= 0f) return false;
                return currentGameDay < m_FrozenUntilDay;
            }
        }

        public bool IsInnerCircle => m_TrustLevel >= BalanceConfig.Current.ShadowReputation.InnerCircleThreshold;

        public string TrustTier
        {
            get
            {
                var srCfg = BalanceConfig.Current.ShadowReputation;
                if (IsFrozenOut) return "Frozen";
                if (m_TrustLevel >= srCfg.InnerCircleThreshold) return "Inner Circle";
                if (m_TrustLevel >= TRUSTED_THRESHOLD) return "Trusted";
                if (m_TrustLevel >= srCfg.FrozenThreshold) return "Neutral";
                return "Untrusted";
            }
        }

        public float GetFrequencyMultiplier()
        {
            var srCfg = BalanceConfig.Current.ShadowReputation;
            if (IsFrozenOut)
                return srCfg.FrozenFrequency;

            if (m_TrustLevel >= srCfg.InnerCircleThreshold)
                return srCfg.HighFrequency;

            if (m_TrustLevel >= TRUSTED_THRESHOLD)
                return srCfg.MedFrequency;

            return srCfg.LowFrequency;
        }

        public void ModifyTrust(float delta, string reason)
        {
            m_ReputationLookups.RefreshIfStale();
            ResolveWalletService();

            float oldTrust = m_TrustLevel;
            m_TrustLevel = math.clamp(m_TrustLevel + delta, 0f, 100f);

            float currentGameDay = GetCurrentGameDay();
            var srCfg = BalanceConfig.Current.ShadowReputation;

            // FIX W1-H2: When called from OnThrottledUpdate, wallet control is suppressed.
            // OnThrottledUpdate does a single threshold check after all modifications.
            // External callers (OnOfferAccepted, OnCaught) emit immediately.
            if (!m_SuppressWalletControl)
            {
                if (oldTrust >= srCfg.FrozenThreshold &&
                    m_TrustLevel < srCfg.FrozenThreshold)
                {
                    // S12b-8 FIX: Only start new freeze if not already in active freeze period
                    if (currentGameDay >= m_FrozenUntilDay)
                    {
                        m_FrozenUntilDay = currentGameDay + srCfg.FreezeDurationDays;
                    }
                    // FIX W9-H2: Init passive recovery baseline (mirrors OnThrottledUpdate freeze branch)
                    m_LastPassiveRecoveryDay = (int)currentGameDay;
                    ApplyLowTrustWalletFreeze(true);
                    Log.Warn($"Player frozen out until day {m_FrozenUntilDay:F1}");
                }
                else if (oldTrust < srCfg.FrozenThreshold &&
                         m_TrustLevel >= srCfg.FrozenThreshold &&
                         currentGameDay >= m_FrozenUntilDay) // FIX W2-M8: Time gate
                {
                    m_FrozenUntilDay = 0f; // FIX W9-M2: Clear timer on threshold unfreeze
                    ApplyLowTrustWalletFreeze(false);
                    Log.Info("Trust recovered above threshold and freeze expired, wallet unfreezing");
                }
            }

            Log.Info($"{reason}: {oldTrust:F1} -> {m_TrustLevel:F1} ({delta:+0.0;-0.0})");

            // Update UI singleton — skip during batch mode (OnThrottledUpdate does final write)
            if (!m_SuppressWalletControl)
                UpdateReputationSingleton();
        }

        public void OnOfferAccepted()
        {
            m_ReputationLookups.RefreshIfStale();
            m_TotalOffersAccepted++;
            ModifyTrust(BalanceConfig.Current.ShadowReputation.TrustAcceptOffer, "Offer accepted");
        }

        public void OnOfferRejected()
        {
            m_ReputationLookups.RefreshIfStale();
            m_TotalOffersRejected++;
            ModifyTrust(BalanceConfig.Current.ShadowReputation.TrustRejectOffer, "Offer rejected");
        }

        public void OnSchemeSuccessful()
        {
            m_ReputationLookups.RefreshIfStale();
            m_TotalSchemesSuccessful++;
            ModifyTrust(BalanceConfig.Current.ShadowReputation.TrustSuccessfulScheme, "Scheme successful");
        }

        public void OnCaught()
        {
            m_ReputationLookups.RefreshIfStale();
            m_TotalTimesCaught++;
            ModifyTrust(BalanceConfig.Current.ShadowReputation.TrustGetCaught, "Got caught");
        }

        // ============================================================================
        // IResettable
        // ============================================================================

        public void ResetState()
        {
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            ReputationStateSingleton.EnsureExists(EntityManager);
            m_TrustLevel = BalanceConfig.Current.ShadowReputation.InitialTrust;
            m_FrozenUntilDay = 0f;
            m_TotalOffersAccepted = 0;
            m_TotalOffersRejected = 0;
            m_TotalSchemesSuccessful = 0;
            m_TotalTimesCaught = 0;
            m_LastPassiveRecoveryDay = 0;
            m_WalletFreezeDesiredInitialized = false;
            m_LastWalletFreezeDesired = false;

            // Push defaults to singleton after deserialization/reset.
            // Skip during early lifecycle (SetDefaults before first OnUpdate) — singleton query not yet valid.
            if (m_ReputationSingletonQuery.CalculateEntityCount() > 0)
            {
                UpdateReputationSingleton();
            }
        }

#if DEBUG
        public void DebugSetTrust(float value, string source)
        {
            float oldTrust = m_TrustLevel;
            m_TrustLevel = math.clamp(value, 0f, 100f);
            ReconcileLowTrustWalletFreeze(force: true);
            UpdateReputationSingleton();
            Log.Info($"[DEBUG] {source}: trust {oldTrust:F1} -> {m_TrustLevel:F1}");
        }

        public void DebugResetReputation(string source)
        {
            ResetState();
            Log.Info($"[DEBUG] {source}: reputation reset");
        }
#endif

        // H37: CreateWalletControlRequest removed — LowTrustLevel freeze/unfreeze
        // now uses direct IShadowWalletService.Freeze/Unfreeze (zero ECB delay).
        // Countermeasures freeze still uses ShadowWalletControlRequest ECB path
        // (ordering dependency: Income → Deduct → Control in ShadowWalletSystem).

        // ============================================================================
        // HELPERS
        // ============================================================================

        private float GetCurrentGameDay()
        {
            m_GameTimeSystem ??= GameTimeSystem.Instance;
#pragma warning disable CIVIC256 // Public entry points may run before GameTime activation; 0f preserves retry semantics.
            if (m_GameTimeSystem == null)
                return 0f;
#pragma warning restore CIVIC256
            return m_GameTimeSystem.Current.CurrentDay;
        }

        /// <summary>
        /// Write UI-relevant fields to ReputationStateSingleton for direct query by panels.
        /// </summary>
        private void UpdateReputationSingleton()
        {
            m_ReputationSingletonLookup.Update(this);
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (!m_ReputationSingletonQuery.TryGetSingletonEntity<ReputationStateSingleton>(out var entity))
                return;
            m_ReputationSingletonLookup[entity] = new ReputationStateSingleton
            {
                TrustLevel = m_TrustLevel,
                Tier = GetReputationTier(),
                IsFrozenOut = IsFrozenOut,
                FrequencyMultiplier = GetFrequencyMultiplier()
            };
        }

        /// <summary>
        /// Convert trust state to ReputationTier enum.
        /// </summary>
        private ReputationTier GetReputationTier()
        {
            var srCfg = BalanceConfig.Current.ShadowReputation;
            if (IsFrozenOut) return ReputationTier.Frozen;
            if (m_TrustLevel >= srCfg.InnerCircleThreshold) return ReputationTier.InnerCircle;
            if (m_TrustLevel >= TRUSTED_THRESHOLD) return ReputationTier.Trusted;
            if (m_TrustLevel >= srCfg.FrozenThreshold) return ReputationTier.Neutral;
            return ReputationTier.Untrusted;
        }

        public int HydrationOrder => HydrationPriority.WALLET_RECONCILE + 1;

        public void ValidateAfterLoad()
        {
            // G10-2: destroy stale fire-and-forget TrustModificationRequest entities
            // that survived save/load. Producers (e.g. BuckwheatSystem) already
            // committed their own effect before save; re-applying the delta in
            // ProcessTrustRequests() on a post-load frame is the save-scum
            // double-apply. Selective purge of the one command type this system
            // consumes (doctrine Invariant 3), matching BudgetResolutionSystem's
            // FireAndForget branch. Order-independent of the wallet reconcile below
            // (destroying request entities does not touch m_TrustLevel), so the
            // existing reconcile order is preserved.
            if (!m_TrustRequestQuery.IsEmpty)
            {
                int stale = m_TrustRequestQuery.CalculateEntityCount();
                EntityManager.DestroyEntity(m_TrustRequestQuery);
                Log.Info($"ValidateAfterLoad: destroyed {stale} stale TrustModificationRequest entities");
            }

            ResolveWalletService(force: true);
            ReconcileLowTrustWalletFreeze(force: true);
            UpdateReputationSingleton();

        }

        private void ReconcileLowTrustWalletFreeze(bool force = false)
        {
            ApplyLowTrustWalletFreeze(IsFrozenOut, force);
        }

        private void ApplyLowTrustWalletFreeze(bool freeze, bool force = false)
        {
            if (!force && m_WalletFreezeDesiredInitialized && freeze == m_LastWalletFreezeDesired)
                return;

            if (freeze)
                m_WalletService.Freeze(FreezeReason.LowTrustLevel);
            else
                m_WalletService.Unfreeze(FreezeReason.LowTrustLevel);

            m_LastWalletFreezeDesired = freeze;
            m_WalletFreezeDesiredInitialized = true;
        }

        private void ResolveWalletService(bool force = false)
        {
            if (!force && m_WalletService != null)
                return;

            m_WalletService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
        }
    }
}
