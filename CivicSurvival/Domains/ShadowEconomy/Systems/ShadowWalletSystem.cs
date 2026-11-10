using System;
using System.Collections.Generic;
using System.Threading;
using Game;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Events; // FIX T17-06..08: For telemetry events
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using Game.Simulation;

namespace CivicSurvival.Domains.ShadowEconomy.Systems
{
    /// <summary>
    /// Manages the centralized Shadow Money wallet.
    /// Access via ServiceRegistry.Instance.Get&lt;IShadowWalletService&gt;().
    ///
    /// Responsibilities:
    /// - Read/write ShadowWalletSingleton
    /// - Track locked operations (operationId → amount)
    /// - Handle freeze/unfreeze/confiscate
    ///
    /// Implements IShadowWalletService for cross-domain access.
    /// </summary>
    [SingletonOwner(typeof(ShadowWalletSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
#pragma warning disable CIVIC173 // lock-nested if/else inflates Write count; actual stream is balanced
    public partial class ShadowWalletSystem : CivicSystemBase, IShadowWalletService, IPostLoadValidation, ICivicSingletonOwner<ShadowWalletSingleton>, IActGatedSystem
#pragma warning restore CIVIC173
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ShadowWalletSystem");

        private EntityQuery m_WalletQuery;
        private EntityQuery m_CountermeasuresQuery;
        private EntityQuery m_DonorSanctionsQuery;
        private EntityQuery m_CurrentActQuery;
#pragma warning disable CIVIC324 // Ephemeral act-gate controller; recreated by OnCreate, reset paths, and Deserialize.
        [System.NonSerialized] private ActGateController m_Gate = null!;
#pragma warning restore CIVIC324
        private EntityQuery m_IncomeRequestQuery;
        private EntityQuery m_DeductRequestQuery;
        private EntityQuery m_ControlRequestQuery;
        private EntityQuery m_StaleRequestQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        /// <summary>Locked operations: operationId → locked amount.</summary>
        private readonly Dictionary<string, long> m_LockedOperations = new();
        private readonly HashSet<string> m_AppliedIncomeKeys = new();
        private readonly List<ShadowIncomeAppliedEvent> m_AppliedIncomeEvents = new();
        private readonly List<(bool Success, long Amount, string Reason)> m_DeductEvents = new();
        private const int MaxAppliedIncomeKeys = 8192;
        private readonly HashSet<string> m_ActiveSlotIdsScratch = new();

        // State migrated from former Core.Services.ShadowWalletService static class.
        // m_WalletLock protects m_LockedOperations and singleton mutations against
        // concurrent main-thread paths. m_PendingDeductions tracks effective costs
        // reserved by producers between queueing a deduct request and the next
        // BudgetResolutionSystem drain.
        internal readonly object m_WalletLock = new();
        private long m_PendingDeductions;
        // A2 FIX 2c: Track sanctions state for change detection (replaces DonorEvent subscription)
        // Not serialized: restored in Deserialize from sanctionsMarkup (derived value)
#pragma warning disable CIVIC241 // Re-detection is correct: sanctions toggle fires fresh after load
        [System.NonSerialized] private bool m_LastSanctionsActive;
#pragma warning restore CIVIC241

        // ============================================================================
        // PUBLIC API
        // ============================================================================

        // LOW FIX: All property getters use TryGetSingleton to avoid TOCTOU race
        public long Balance
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return 0;
                return wallet.Balance;
            }
        }

        public long LockedBalance
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return 0;
                return wallet.LockedBalance;
            }
        }

        public long TotalBalance
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return 0;
                return wallet.Balance + wallet.LockedBalance;
            }
        }

        public bool IsFrozen
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return false;
                return wallet.IsFrozen;
            }
        }

        public float SanctionsMarkup
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return 0f;
                return wallet.SanctionsMarkup;
            }
        }

        public FreezeReason FreezeReason
        {
            get
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return FreezeReason.None;
                return wallet.FreezeReason;
            }
        }

        /// <summary>True once the act-gate is open. The wallet runs from PreWar onward.</summary>
        public bool IsOperational => GateState == ActGateState.Active;

        public Act MinActiveAct => Act.PreWar;

        public ActGateState GateState => m_Gate?.State ?? ActGateState.AwaitingActState;

#pragma warning disable CIVIC243 // Implements IShadowWalletService — called via interface dispatch
#pragma warning disable CIVIC114 // False positive: m_WalletQuery inside shared wallet lock, analyzer doesn't detect property-accessor pattern
        public bool CanAfford(long baseCost)
        {
#pragma warning restore CIVIC243
#pragma warning restore CIVIC114
            if (baseCost <= 0) return true;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet)) return false;
                if (wallet.IsFrozen) return false;
                long effectiveCost = SanctionsCostHelper.ApplyMarkup(baseCost, wallet.SanctionsMarkup);
                return wallet.Balance >= effectiveCost;
            }
        }

        // ============================================================================
        // SNAPSHOT / EXISTENCE
        // ============================================================================

        public bool HasWallet
        {
            get
            {
                lock (m_WalletLock)
                {
                    return m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out _);
                }
            }
        }

        public WalletSnapshot GetWalletSnapshot()
        {
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet))
                    return default;
                return new WalletSnapshot(true, wallet.Balance, wallet.IsFrozen, wallet.SanctionsMarkup);
            }
        }

        // ============================================================================
        // AFFORDABILITY WITH PENDING
        // ============================================================================

        public AffordabilityResult CanAffordWithPending(long baseCost)
        {
            if (baseCost <= 0) return AffordabilityResult.Free;

            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet))
                    return AffordabilityResult.Unavailable;
                if (wallet.IsFrozen)
                    return AffordabilityResult.Unavailable;

                long effectiveCost = SanctionsCostHelper.ApplyMarkup(baseCost, wallet.SanctionsMarkup);
                bool affordable = wallet.Balance - Interlocked.Read(ref m_PendingDeductions) >= effectiveCost;
                return new AffordabilityResult(affordable, effectiveCost);
            }
        }

        public AffordabilityResult CanAffordBypassFreeze(long baseCost)
        {
            if (baseCost <= 0) return AffordabilityResult.Free;

            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingleton<ShadowWalletSingleton>(out var wallet))
                    return AffordabilityResult.Unavailable;

                long effectiveCost = SanctionsCostHelper.ApplyMarkup(baseCost, wallet.SanctionsMarkup);
                bool affordable = wallet.Balance - Interlocked.Read(ref m_PendingDeductions) >= effectiveCost;
                return new AffordabilityResult(affordable, effectiveCost);
            }
        }

        // ============================================================================
        // PENDING DEDUCTION TRACKING
        // ============================================================================

        public long PendingDeductions => Interlocked.Read(ref m_PendingDeductions);

        public void RegisterPendingDeduction(long amount)
        {
            if (amount <= 0) return;

            while (true)
            {
                long current = Interlocked.Read(ref m_PendingDeductions);
                long next = amount > long.MaxValue - current ? long.MaxValue : current + amount;
                if (Interlocked.CompareExchange(ref m_PendingDeductions, next, current) == current)
                    return;
            }
        }

        public void RollbackPendingDeduction(long amount)
        {
            if (amount <= 0) return;

            while (true)
            {
                long current = Interlocked.Read(ref m_PendingDeductions);
                if (current <= 0) return;

                long next = amount >= current ? 0 : current - amount;
                if (Interlocked.CompareExchange(ref m_PendingDeductions, next, current) == current)
                    return;
            }
        }

        public void ResetPendingDeductions()
        {
            Interlocked.Exchange(ref m_PendingDeductions, 0);
        }

        /// <summary>
        /// Internal: add income to wallet. Called by ProcessIncomeRequests only.
        /// External callers use ShadowIncomeRequest ECB.
        /// </summary>
        private bool AddIncome(long amount, string reason)
        {
            if (amount <= 0) return false;

            // MED FIX: Use TryGetSingletonRW to avoid TOCTOU race
            if (!SystemAPI.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                return false;

            // FIX S5-06 / N3-04 ACCEPTED: AddIncome itself doesn't check freeze — callers are responsible.
            // Design: freeze blocks spending, not receiving. Callers check IsFrozen before calling.
            // ShadowIncomeRequestSystem processes ECB requests and may bypass for specific cases.
            wallet.ValueRW.Balance += amount;
            wallet.ValueRW.TotalIncome = SaturatingAdd(wallet.ValueRO.TotalIncome, amount);

            if (wallet.ValueRO.IsFrozen)
                Log.Info($"+${amount:N0} for {reason} (frozen), Balance: ${wallet.ValueRO.Balance:N0}");
            else
                Log.Info($"+${amount:N0} for {reason}, Balance: ${wallet.ValueRO.Balance:N0}");

            return true;
        }

        /// <summary>
        /// Synchronous deduct used by <c>BudgetResolutionSystem.ProcessDeductRequests</c>
        /// (ShadowOps category) and by <c>ProcessDeductRequests</c> here when draining
        /// queued <c>ShadowWalletDeductRequest</c> entities. <paramref name="amount"/>
        /// is the effective post-markup cost; producers must compute that through
        /// <see cref="CanAffordWithPending"/>. Respects freeze.
        /// </summary>
        public bool TryDeduct(long amount, string reason)
        {
            if (amount < 0) return false;
            if (amount == 0) return true;

            string logMsg = null!;
            bool isFrozenFail = false;
            bool result = false;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return false;

                if (wallet.ValueRO.IsFrozen)
                {
                    logMsg = $"TryDeduct failed - assets frozen. Reason: {reason}, Amount: ${amount:N0}";
                    isFrozenFail = true;
                }
                else if (wallet.ValueRO.Balance < amount)
                {
                    if (Log.IsDebugEnabled)
                        logMsg = $"TryDeduct failed - insufficient funds. Reason: {reason}, Need: ${amount:N0}, Have: ${wallet.ValueRO.Balance:N0}";
                }
                else
                {
                    wallet.ValueRW.Balance -= amount;
                    wallet.ValueRW.TotalExpenses = SaturatingAdd(wallet.ValueRO.TotalExpenses, amount);
                    logMsg = $"-${amount:N0} for {reason}, Balance: ${wallet.ValueRO.Balance:N0}";
                    result = true;
                }
            }

            if (logMsg != null)
            {
                if (result) Log.Info(logMsg);
                else if (isFrozenFail) Log.Info(logMsg);
                else Log.Debug(logMsg);
            }
            return result;
        }

        /// <summary>
        /// Synchronous deduct that bypasses the freeze gate. Used only by the
        /// police-bribe flow: ordering between deduct-processing and
        /// freeze-control would otherwise reject the bribe before the unfreeze
        /// runs. <paramref name="effectiveCost"/> is already post-markup.
        /// </summary>
        public bool DeductBypassFreeze(long effectiveCost, string reason)
        {
            if (effectiveCost < 0) return false;
            if (effectiveCost == 0) return true;

            string logMsg = null!;
            bool result = false;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return false;

                if (wallet.ValueRO.Balance < effectiveCost)
                {
                    logMsg = $"DeductBypassFreeze failed - insufficient funds. Reason: {reason}, Need: ${effectiveCost:N0}, Have: ${wallet.ValueRO.Balance:N0}";
                }
                else
                {
                    wallet.ValueRW.Balance -= effectiveCost;
                    wallet.ValueRW.TotalExpenses = SaturatingAdd(wallet.ValueRO.TotalExpenses, effectiveCost);
                    logMsg = $"-${effectiveCost:N0} for {reason} (bypass freeze), Balance: ${wallet.ValueRO.Balance:N0}";
                    result = true;
                }
            }

            if (logMsg != null)
                Log.Info(logMsg);
            return result;
        }

        public bool TryApplyRefund(long amount, string reason, string operationKey)
        {
            if (amount <= 0 || string.IsNullOrEmpty(operationKey))
                return false;

            if (m_AppliedIncomeKeys.Contains(operationKey))
                return true;

            string logMsg = null!;
            bool applied = false;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return false;

                wallet.ValueRW.Balance = SaturatingAdd(wallet.ValueRO.Balance, amount);
                wallet.ValueRW.TotalIncome = SaturatingAdd(wallet.ValueRO.TotalIncome, amount);
                logMsg = $"+${amount:N0} refund for {reason}, Balance: ${wallet.ValueRO.Balance:N0}";
                applied = true;
            }

            if (applied)
            {
                m_AppliedIncomeKeys.Add(operationKey);
                TrimAppliedIncomeKeysIfNeeded();
                Log.Info(logMsg);
                EventBus?.SafePublish(new ShadowIncomeAppliedEvent(operationKey, amount, reason), "ShadowWalletSystem");
            }

            return true;
        }

        public bool TryLock(long amount, string operationId)
        {
            if (amount <= 0) return false;
            if (string.IsNullOrEmpty(operationId)) return false;

            // FIX TOCTOU: Atomic check-and-lock under single lock scope
            string tryLockLogMsg = null!;
            bool tryLockResult = false;
            lock (m_WalletLock)
            {
                // Check if operation already locked
                if (m_LockedOperations.ContainsKey(operationId))
                {
                    tryLockLogMsg = $"TryLock failed - operation already locked: {operationId}";
                }
                // MED FIX: Use TryGetSingletonRW to avoid TOCTOU race
                else if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                {
                    // No singleton — silent fail
                }
                else if (wallet.ValueRO.IsFrozen)
                {
                    tryLockLogMsg = $"TryLock failed - assets frozen. Operation: {operationId}";
                }
                else if (wallet.ValueRO.Balance - PendingDeductions < amount)
                {
                    if (Log.IsDebugEnabled)
                    {
                        long available = wallet.ValueRO.Balance - PendingDeductions;
                        tryLockLogMsg = $"TryLock failed - insufficient funds. Operation: {operationId}, Need: ${amount:N0}, Available: ${available:N0} (Balance: ${wallet.ValueRO.Balance:N0}, Pending: ${PendingDeductions:N0})";
                    }
                }
                else
                {
                    // Move from Balance to LockedBalance
                    wallet.ValueRW.Balance -= amount;
                    wallet.ValueRW.LockedBalance += amount;
                    m_LockedOperations[operationId] = amount;
                    tryLockLogMsg = $"LOCKED ${amount:N0} for {operationId}, Balance: ${wallet.ValueRO.Balance:N0}, Locked: ${wallet.ValueRO.LockedBalance:N0}";
                    tryLockResult = true;
                }
            }
            if (tryLockLogMsg != null)
            {
                if (tryLockResult) Log.Info(tryLockLogMsg);
                else if (tryLockLogMsg.Contains("insufficient", StringComparison.Ordinal)) { if (Log.IsDebugEnabled) Log.Debug(tryLockLogMsg); }
                else if (tryLockLogMsg.Contains("frozen", StringComparison.Ordinal)) Log.Info(tryLockLogMsg);
                else Log.Warn(tryLockLogMsg);
            }
            return tryLockResult;
        }

#pragma warning disable CIVIC231 // Escrow confirmation — funds already locked by TryLock (which checks IsFrozen/act)
        public void ConfirmDeduct(string operationId)
        {
#pragma warning restore CIVIC231
            if (string.IsNullOrEmpty(operationId)) return;

            // FIX TOCTOU: Atomic lookup-and-remove under lock
            string confirmLogMsg = null!;
            bool confirmWarn = false;
            lock (m_WalletLock)
            {
                if (!m_LockedOperations.TryGetValue(operationId, out long amount))
                {
                    confirmLogMsg = $"ConfirmDeduct failed - operation not found: {operationId}";
                    confirmWarn = true;
                }
                // MED FIX: Use TryGetSingletonRW to avoid TOCTOU race
                else if (m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                {
                    // ConfirmDeduct completes a previously locked operation — must work even when frozen.
                    // Callers (PlayerAttackSystem) are fire-and-forget: slot is cleared before calling,
                    // so if this returns without acting, money stays in LockedBalance forever (deadlock).
                    // Only new operations (TryLock, TryDeduct, AddIncome) respect freeze.

                    // ISW-009: Guard against negative LockedBalance
                    // R9-M04 FIX: Track actual amount deducted, not originally locked amount.
                    // After confiscation, LockedBalance may be less than locked amount.
                    long actualExpense = System.Math.Min(amount, wallet.ValueRO.LockedBalance);
                    wallet.ValueRW.LockedBalance = System.Math.Max(0, wallet.ValueRO.LockedBalance - amount);
                    wallet.ValueRW.TotalExpenses = SaturatingAdd(wallet.ValueRO.TotalExpenses, actualExpense);
                    m_LockedOperations.Remove(operationId);
                    confirmLogMsg = $"EXECUTED ${actualExpense:N0} for {operationId}, Locked: ${wallet.ValueRO.LockedBalance:N0}";
                }
                else
                {
                    // Singleton unavailable — remove orphaned lock to prevent permanent money loss
                    m_LockedOperations.Remove(operationId);
                    confirmLogMsg = $"ConfirmDeduct: singleton unavailable, forcibly removed lock {operationId} (${amount:N0} lost)";
                    confirmWarn = true;
                }
            }
            if (confirmLogMsg != null)
            {
                if (confirmWarn) Log.Warn(confirmLogMsg); else Log.Info(confirmLogMsg);
            }
        }

        public void Unlock(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return;

            // FIX TOCTOU: Atomic lookup-and-remove under lock
            string unlockLogMsg = null!;
            bool unlockWarn = false;
            lock (m_WalletLock)
            {
                if (!m_LockedOperations.TryGetValue(operationId, out long amount))
                {
                    unlockLogMsg = $"Unlock failed - operation not found: {operationId}";
                    unlockWarn = true;
                }
                // MED FIX: Use TryGetSingletonRW to avoid TOCTOU race
                else if (m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                {
                    // Unlock is a refund/cancellation — must work even when frozen.
                    // Blocking Unlock during freeze = permanent money loss (deadlock).
                    // Only new operations (TryLock, TryDeduct, ConfirmDeduct) respect freeze.

                    // ISW-009: Guard against negative LockedBalance
                    long actualDeduct = System.Math.Min(amount, wallet.ValueRO.LockedBalance);
                    wallet.ValueRW.LockedBalance = System.Math.Max(0, wallet.ValueRO.LockedBalance - amount);
                    wallet.ValueRW.Balance += actualDeduct;
                    m_LockedOperations.Remove(operationId);
                    // R9-M05 FIX: Warn when refund is less than locked amount (money destroyed)
                    if (actualDeduct < amount)
                    {
                        unlockLogMsg = $"UNLOCKED ${actualDeduct:N0} for {operationId} (LOST ${amount - actualDeduct:N0} — LockedBalance insufficient)";
                        unlockWarn = true;
                    }
                    else
                    {
                        unlockLogMsg = $"UNLOCKED ${actualDeduct:N0} for {operationId}, Balance: ${wallet.ValueRO.Balance:N0}";
                    }
                }
                else
                {
                    // Singleton unavailable — remove orphaned lock to prevent permanent money loss
                    m_LockedOperations.Remove(operationId);
                    unlockLogMsg = $"Unlock: singleton unavailable, forcibly removed lock {operationId} (${amount:N0} lost)";
                    unlockWarn = true;
                }
            }
            if (unlockLogMsg != null)
            {
                if (unlockWarn) Log.Warn(unlockLogMsg); else Log.Info(unlockLogMsg);
            }
        }

        /// <summary>
        /// Check if a specific operation lock exists in the wallet.
        /// Used by post-load reconciliation (S3-04) and diagnostics.
        /// </summary>
        public bool HasLock(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return false;
            lock (m_WalletLock)
            {
                return m_LockedOperations.ContainsKey(operationId);
            }
        }

        /// <summary>
        /// Add a freeze source (flags). Multiple sources can be active simultaneously.
        /// FIX T3-1: No early return when already frozen — adds flag instead.
        /// </summary>
#pragma warning disable CIVIC114 // False positive: m_WalletQuery inside shared wallet lock
        public void Freeze(FreezeReason reason)
        {
#pragma warning restore CIVIC114
            if (reason == FreezeReason.None) return;

            bool publishFrozenEvent = false;
            long frozenBalance = 0;
            string freezeLogMsg = null!;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                bool wasFrozen = wallet.ValueRO.IsFrozen;
                wallet.ValueRW.FreezeReason |= reason;
                freezeLogMsg = $"Freeze source added: {reason}. Active: {wallet.ValueRO.FreezeReason}. Balance: ${wallet.ValueRO.Balance:N0}";

                if (!wasFrozen)
                {
                    publishFrozenEvent = true;
                    frozenBalance = wallet.ValueRO.Balance;
                }
            }
            Log.Info(freezeLogMsg);

            if (publishFrozenEvent)
            {
                EventBus?.SafePublish(new ShadowNarrativeEvent(
                    ShadowNarrativeEventType.WalletFrozen,
                    Cost: frozenBalance
                ), "ShadowWalletSystem");
            }
        }

        /// <summary>
        /// Remove a specific freeze source. Wallet unfreezes only when ALL sources cleared.
        /// FIX T3-1: Removes specific flag instead of clearing everything.
        /// </summary>
        public void Unfreeze(FreezeReason reason)
        {
            if (reason == FreezeReason.None) return;

            bool publishUnfrozenEvent = false;
            long unfrozenBalance = 0;
            string unfreezeLogMsg = null!;
            string allClearedMsg = null!;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                if (!wallet.ValueRO.IsFrozen) return;

                wallet.ValueRW.FreezeReason &= ~reason;
                unfreezeLogMsg = $"Freeze source removed: {reason}. Remaining: {wallet.ValueRO.FreezeReason}";

                if (!wallet.ValueRO.IsFrozen)
                {
                    allClearedMsg = $"All freeze sources cleared — assets UNFROZEN. Balance: ${wallet.ValueRO.Balance:N0}";
                    publishUnfrozenEvent = true;
                    unfrozenBalance = wallet.ValueRO.Balance;
                }
            }
            Log.Info(unfreezeLogMsg);
            if (allClearedMsg != null) Log.Info(allClearedMsg);

            if (publishUnfrozenEvent)
            {
                EventBus?.SafePublish(new ShadowNarrativeEvent(
                    ShadowNarrativeEventType.WalletUnfrozen,
                    Cost: unfrozenBalance
                ), "ShadowWalletSystem");
            }
        }

        // A2 FIX 2c: SetSanctionsMarkup removed — DonorConferenceSystem writes ShadowWalletSingleton directly

        public void Confiscate()
        {
            long confiscated;
            // FIX TOCTOU: Clear operations under lock
            lock (m_WalletLock)
            {
                // MED FIX: Use TryGetSingletonRW to avoid TOCTOU race
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                confiscated = SaturatingAdd(wallet.ValueRO.Balance, wallet.ValueRO.LockedBalance);

                wallet.ValueRW.Balance = 0;
                wallet.ValueRW.LockedBalance = 0;
                wallet.ValueRW.TotalExpenses = SaturatingAdd(wallet.ValueRO.TotalExpenses, confiscated);
                // T3-8 fix: Do NOT clear FreezeReason — freeze causes (sanctions, low trust)
                // are independent of balance. Clearing here lets new income bypass freeze.
                wallet.ValueRW.FreezeReason |= FreezeReason.Confiscated;
                m_LockedOperations.Clear();
            }

            Log.Info($"${confiscated:N0} CONFISCATED by authorities!");

            // FIX T17-08: Telemetry for wallet confiscation (safe cast: long→int)
            EventBus?.SafePublish(new ShadowNarrativeEvent(
                ShadowNarrativeEventType.WalletConfiscated,
                Cost: confiscated
            ), "ShadowWalletSystem");
        }

        // ============================================================================
        // POST-LOAD RECONCILIATION (IPostLoadValidation)
        // ============================================================================

        /// <summary>
        /// S2-06: Reconcile FreezeReason.PoliceInvestigation against CountermeasuresCoreFsm.CurrentPhase.IsPoliceActive().
        /// S8-07: Sweep m_LockedOperations for locks with no matching PlayerAttackSystem slot.
        /// Called once after load by PostLoadValidationSystem (frame N+2).
        /// Order 50: before PlayerAttackSystem(60) which checks wallet locks.
        /// </summary>
        public int HydrationOrder => HydrationPriority.WALLET_RECONCILE;
        public void ValidateAfterLoad()
        {
            ReconcileFreezeReason();
            ReconcileLockedBalance();
            ReconcileOrphanedLocks();
            ReconcileSanctionsMarkup();
            PurgeStaleWalletRequestsAfterLoad();
        }

        private void ReconcileLockedBalance()
        {
            bool repaired = false;
            long oldLocked = 0;
            long lockedOpsTotal = 0;
            long totalBalance = 0;
            long repairedLocked = 0;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                lockedOpsTotal = SumLockedOperations();
                if (wallet.ValueRO.LockedBalance == lockedOpsTotal)
                    return;

                oldLocked = wallet.ValueRO.LockedBalance;
                totalBalance = SaturatingAdd(wallet.ValueRO.Balance, wallet.ValueRO.LockedBalance);
                repairedLocked = System.Math.Min(lockedOpsTotal, totalBalance);
                wallet.ValueRW.LockedBalance = repairedLocked;
                wallet.ValueRW.Balance = totalBalance - repairedLocked;
                repaired = true;
            }

            if (repaired)
                Log.Warn($"ShadowWallet locked-balance mismatch after load: scalar=${oldLocked:N0}, lockedOps=${lockedOpsTotal:N0}; preserved total=${totalBalance:N0}, repaired locked=${repairedLocked:N0}");
        }

        private void PurgeStaleWalletRequestsAfterLoad()
        {
            if (m_StaleRequestQuery.IsEmptyIgnoreFilter) return;

            // Post-load validation runs before this system's normal update. Destroy synchronously
            // so loaded wallet command entities cannot execute once before end-frame playback.
            EntityManager.DestroyEntity(m_StaleRequestQuery);
            Log.Info("ValidateAfterLoad: destroyed stale shadow wallet request entities");
        }

        private void ReconcileSanctionsMarkup()
        {
            // Use EntityQuery, not SystemAPI — ValidateAfterLoad runs from PostLoadValidationSystem's
            // OnUpdate context, so this system's __TypeHandle may not be current.
            bool sanctionsActive = false;
            if (m_DonorSanctionsQuery.TryGetSingleton<DonorSanctionsSingleton>(out var sanctions))
                sanctionsActive = sanctions.SanctionsActive;

            bool cleared = false, restored = false;
            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                if (!sanctionsActive && wallet.ValueRO.SanctionsMarkup > 0f)
                {
                    wallet.ValueRW.SanctionsMarkup = 0f;
                    cleared = true;
                }
                else if (sanctionsActive && wallet.ValueRO.SanctionsMarkup <= 0f)
                {
                    wallet.ValueRW.SanctionsMarkup = BalanceConfig.Current.Diplomacy.SanctionsBlackMarketMarkup;
                    restored = true;
                }
            }

            if (cleared)
            {
                m_LastSanctionsActive = false;
                Log.Info("ValidateAfterLoad: cleared stale SanctionsMarkup (zombie sanctions)");
            }
            else if (restored)
            {
                m_LastSanctionsActive = true;
                Log.Info("ValidateAfterLoad: restored SanctionsMarkup from active sanctions");
            }
        }

        private void ReconcileFreezeReason()
        {
            // Countermeasures is the sole producer of FreezeReason.PoliceInvestigation.
            // When Countermeasures is unavailable (beta-gated, dep-skipped, or failed),
            // CountermeasuresCoreFsm singleton is absent and no new police freezes will be set.
            // We leave the existing flag untouched. A "stale flag + Countermeasures
            // unavailable" save can only arise from cross-config reloads, which the
            // no-old-saves policy (SaveVersions.GLOBAL pre-release) excludes — saves
            // are created and loaded with the same feature manifest, so if the flag is
            // present the singleton will be too. A mid-session Countermeasures crash is a
            // code bug to fix upstream, not something to paper over here.
            if (!m_CountermeasuresQuery.TryGetSingleton<CountermeasuresCoreFsm>(out var cmCore))
            {
                Log.Info("S091: CountermeasuresCoreFsm missing; leaving FreezeReason untouched");
                return;
            }

            bool policeActive = cmCore.CurrentPhase.IsPoliceActive();
            bool restoredPoliceFlag = false;
            bool clearedPoliceFlag = false;

            lock (m_WalletLock)
            {
                if (!m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    return;

                bool walletHasPoliceFlag = (wallet.ValueRO.FreezeReason & FreezeReason.PoliceInvestigation) != 0;

                if (policeActive && !walletHasPoliceFlag)
                {
                    wallet.ValueRW.FreezeReason |= FreezeReason.PoliceInvestigation;
                    restoredPoliceFlag = true;
                }
                else if (!policeActive && walletHasPoliceFlag)
                {
                    wallet.ValueRW.FreezeReason &= ~FreezeReason.PoliceInvestigation;
                    clearedPoliceFlag = true;
                }
            }

            if (restoredPoliceFlag)
                Log.Warn("S2-06: PoliceActive=true but FreezeReason lacked PoliceInvestigation — corrected");
            else if (clearedPoliceFlag)
                Log.Warn("S2-06: PoliceActive=false but FreezeReason had PoliceInvestigation — corrected");
            else
                Log.Info($"S2-06: FreezeReason/PoliceActive consistent (PoliceActive={policeActive})");
        }

        private void ReconcileOrphanedLocks()
        {
            var slotReader = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullOperationSlotReader.Instance);
            if (ReferenceEquals(slotReader, NullOperationSlotReader.Instance))
            {
                // F12 / S8-07: Safe-fail — GridWarfare feature unavailable (closed or partially registered).
                // Unlock all deserialized locks and refund; freezing funds permanently is worse.
                int count = 0;
                long refunded = 0;
                lock (m_WalletLock)
                {
                    count = m_LockedOperations.Count;
                    if (count > 0 && m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    {
                        refunded = System.Math.Min(wallet.ValueRO.LockedBalance, SumLockedOperations());
                        wallet.ValueRW.LockedBalance = System.Math.Max(0, wallet.ValueRO.LockedBalance - refunded);
                        wallet.ValueRW.Balance += refunded;
                    }
                    m_LockedOperations.Clear();
                }
                Log.Warn($"S8-07: IOperationSlotReader unavailable (GridWarfare closed) — unlocked all {count} locks as safe-fail, refunded ${refunded:N0}");
                return;
            }

            slotReader.CopyActiveOperationIds(m_ActiveSlotIdsScratch);
            var orphanedIds = new List<string>();
            long orphanedAmount = 0;
            long refundedAmount = 0;
            lock (m_WalletLock)
            {
                foreach (var kvp in m_LockedOperations)
                {
                    if (!m_ActiveSlotIdsScratch.Contains(kvp.Key))
                    {
                        orphanedIds.Add(kvp.Key);
                        orphanedAmount += kvp.Value;
                    }
                }

                if (orphanedIds.Count > 0)
                {
                    if (m_WalletQuery.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                    {
                        refundedAmount = System.Math.Min(wallet.ValueRO.LockedBalance, orphanedAmount);
                        wallet.ValueRW.LockedBalance = System.Math.Max(0, wallet.ValueRO.LockedBalance - orphanedAmount);
                        wallet.ValueRW.Balance += refundedAmount;
                    }

                    foreach (var id in orphanedIds)
                        m_LockedOperations.Remove(id);
                }
            }

            foreach (var id in orphanedIds)
            {
                Log.Warn($"S8-07: Orphaned lock '{id}' has no matching slot — unlocked during reconciliation");
            }
            if (orphanedIds.Count > 0 && refundedAmount < orphanedAmount)
                Log.Warn($"S8-07: Orphaned lock reconciliation refunded ${refundedAmount:N0} of ${orphanedAmount:N0}; locked balance was short");

            if (orphanedIds.Count == 0)
                Log.Info("S8-07: No orphaned wallet locks found");
        }

        private long SumLockedOperations()
        {
            long total = 0;
            foreach (long amount in m_LockedOperations.Values)
            {
                if (amount > long.MaxValue - total)
                    return long.MaxValue;
                total += amount;
            }
            return total;
        }

        private static long SaturatingAdd(long left, long right)
        {
            return right > long.MaxValue - left ? long.MaxValue : left + right;
        }

        private void InitializeGate()
        {
            m_Gate = new ActGateController(
                // Wallet active from PreWar — offshore balance accrues before the war.
                isOpenFor: _ => true,
                onTransition: HandleGateTransition);
        }

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_WalletQuery = GetEntityQuery(ComponentType.ReadWrite<ShadowWalletSingleton>());
            m_CountermeasuresQuery = GetEntityQuery(ComponentType.ReadOnly<CountermeasuresCoreFsm>());
            m_DonorSanctionsQuery = GetEntityQuery(ComponentType.ReadOnly<DonorSanctionsSingleton>());
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_IncomeRequestQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowIncomeRequest>());
            m_DeductRequestQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowWalletDeductRequest>());
            m_ControlRequestQuery = GetEntityQuery(ComponentType.ReadOnly<ShadowWalletControlRequest>());
            m_StaleRequestQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<ShadowWalletDeductRequest>(),
                    ComponentType.ReadOnly<ShadowWalletControlRequest>()
                }
            });

            // Domain-Driven Initialization (Static Factory)
            m_AppliedIncomeKeys.Clear();
            ShadowWalletSingleton.EnsureExists(EntityManager);
            ShadowImportState.EnsureExists(EntityManager);

            InitializeGate();

            // Register for cross-domain access via interface
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IShadowWalletService>(this);

            // A2 FIX 2c: DonorEvent subscription removed — SanctionsMarkup written by DonorConferenceSystem

            Log.Info($"{nameof(ShadowWalletSystem)} created (active from PreWar)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            ShadowWalletSingleton.EnsureExists(EntityManager);
            ShadowImportState.EnsureExists(EntityManager);
        }

        protected override void OnUpdateImpl()
        {
            if (!m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out _))
                return;

            m_Gate.ReconcileFromSingleton(m_CurrentActQuery);
            if (m_Gate.State != ActGateState.Active)
                return;

            // A2 FIX 2c: Sync sanctions markup from singleton (replaces DonorEvent subscription)
            SyncSanctionsFromSingleton();

            // FIX S3-01: Process requests in order: Income → Deduct → Control
            // Income FIRST: maximizes balance before deductions (matches DayEventPriority intent).
            // Control LAST: freeze/confiscate applied after deducts — same-frame freeze doesn't block deduct.
            ProcessIncomeRequests();
            ProcessDeductRequests();
            ProcessControlRequests();
        }

        /// <summary>
        /// A2 FIX 2c: Read sanctions state from DonorSanctionsSingleton, update SanctionsMarkup on change.
        /// Replaces DonorEvent(SanctionsApplied/Expired) subscription.
        /// </summary>
        private void SyncSanctionsFromSingleton()
        {
            bool active = false;
            if (SystemAPI.TryGetSingleton<DonorSanctionsSingleton>(out var sanctions))
                active = sanctions.SanctionsActive;

            if (active == m_LastSanctionsActive)
                return;

            m_LastSanctionsActive = active;
            float markup = active ? BalanceConfig.Current.Diplomacy.SanctionsBlackMarketMarkup : 0f;

            lock (m_WalletLock)
            {
            if (SystemAPI.TryGetSingletonRW<ShadowWalletSingleton>(out var wallet))
                wallet.ValueRW.SanctionsMarkup = markup;
            }

            Log.Info(active
                ? $"Sanctions active: black market markup +{markup:P0}"
                : "Sanctions expired — black market markup cleared");
        }

        /// <summary>
        /// Process ShadowIncomeRequest entities from other domains.
        /// </summary>
        private void ProcessIncomeRequests()
        {
            if (m_IncomeRequestQuery.IsEmpty) return;

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            m_AppliedIncomeEvents.Clear();

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<ShadowIncomeRequest>>()
                .WithEntityAccess())
            {
                string operationKey = request.ValueRO.OperationKey.ToString();
                if (string.IsNullOrEmpty(operationKey))
                {
                    Log.Warn($"Dropped shadow income request without operation key ({request.ValueRO.Reason.ToString()}, ${request.ValueRO.Amount:N0})");
                    ecb.DestroyEntity(entity);
                    continue;
                }

                if (!m_AppliedIncomeKeys.Contains(operationKey))
                {
                    string reason = request.ValueRO.Reason.ToString();
                    if (!AddIncome(request.ValueRO.Amount, reason))
                        continue;

                    m_AppliedIncomeKeys.Add(operationKey);
                    TrimAppliedIncomeKeysIfNeeded();
                    m_AppliedIncomeEvents.Add(new ShadowIncomeAppliedEvent(operationKey, request.ValueRO.Amount, reason));
                }

                ecb.DestroyEntity(entity);
            }

            if (m_AppliedIncomeEvents.Count > 0)
            {
                foreach (var evt in m_AppliedIncomeEvents)
                    EventBus?.SafePublish(evt, "ShadowWalletSystem");
                m_AppliedIncomeEvents.Clear();
            }
        }

        private void TrimAppliedIncomeKeysIfNeeded()
        {
            if (m_AppliedIncomeKeys.Count <= MaxAppliedIncomeKeys)
                return;

            using var keys = m_AppliedIncomeKeys.GetEnumerator();
            if (keys.MoveNext())
            {
                m_AppliedIncomeKeys.Remove(keys.Current);
            }
        }

        /// <summary>
        /// Process ShadowWalletDeductRequest entities from other domains.
        /// </summary>
        private void ProcessDeductRequests()
        {
            if (m_DeductRequestQuery.IsEmpty) return;

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            m_DeductEvents.Clear();

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<ShadowWalletDeductRequest>>()
                .WithEntityAccess())
            {
                string reason = request.ValueRO.Reason.ToString();
                bool success = TryDeduct(request.ValueRO.Amount, reason);
                m_DeductEvents.Add((success, request.ValueRO.Amount, reason));
                long reservationAmount = request.ValueRO.ReservationAmount > 0
                    ? request.ValueRO.ReservationAmount
                    : request.ValueRO.Amount;
                RollbackPendingDeduction(reservationAmount);
                ecb.DestroyEntity(entity);
            }

            if (m_DeductEvents.Count > 0)
            {
                foreach (var evt in m_DeductEvents)
                {
                    if (evt.Success)
                        EventBus?.SafePublish(new ShadowDeductSucceededEvent(evt.Amount, evt.Reason), "ShadowWalletSystem");
                    else
                        EventBus?.SafePublish(new ShadowDeductFailedEvent(evt.Amount, evt.Reason), "ShadowWalletSystem");
                }
                m_DeductEvents.Clear();
            }
        }

        /// <summary>
        /// Process ShadowWalletControlRequest entities from other domains.
        /// Handles freeze/unfreeze/confiscate operations.
        /// S12a-1 ACCEPTED: Freeze/unfreeze arrives via ECB (one-frame delay) — inherent to
        /// Data-Driven Commands pattern. Exploitation window &lt;16ms, same-frame spending
        /// requires another request entity with identical delay.
        /// </summary>
        private void ProcessControlRequests()
        {
            if (m_ControlRequestQuery.IsEmpty) return;

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<ShadowWalletControlRequest>>()
                .WithEntityAccess())
            {
                switch (request.ValueRO.Type)
                {
                    case ShadowWalletControlType.Freeze:
                        Freeze(request.ValueRO.FreezeReason);
                        break;
                    case ShadowWalletControlType.Unfreeze:
                        // FIX T3-1: Remove specific freeze flag (flags enum)
                        Unfreeze(request.ValueRO.FreezeReason);
                        break;
                    case ShadowWalletControlType.Confiscate:
                        // R9-H04 FIX: Set Confiscated flag BEFORE zeroing balance.
                        // Without this, wallet unfreezes when PoliceInvestigation flag is removed,
                        // allowing income to accumulate post-confiscation.
                        Freeze(request.ValueRO.FreezeReason | FreezeReason.Confiscated);
                        Confiscate();
                        break;
                    default:
                        Log.Warn($"Unhandled {nameof(ShadowWalletControlType)}: {request.ValueRO.Type}");
                        break;
                }
                ecb.DestroyEntity(entity);
            }
        }

        // A2 FIX 2c: OnDonorEvent removed — SanctionsMarkup written directly by DonorConferenceSystem

        private void HandleGateTransition(ActGateState old, ActGateState next, bool isInitial)
        {
            if (next == ActGateState.Active)
            {
                if (!isInitial)
                {
                    m_LastSanctionsActive = false;
                    Log.Info("[ShadowWallet] Gate opened");
                }

                return;
            }

            if (next == ActGateState.Inactive && !isInitial)
            {
                m_LastSanctionsActive = false;
                Log.Info("[ShadowWallet] Gate closed");
            }
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IShadowWalletService>(this);

            // A2 FIX 2c: DonorEvent unsubscribe removed — sanctions handled via singleton

            // CM-004 FIX: Clear locked operations and pending counter on destroy
            lock (m_WalletLock)
            {
                m_LockedOperations.Clear();
            }
            ResetPendingDeductions();

            Log.Info($"{nameof(ShadowWalletSystem)} destroyed");
            base.OnDestroy();
        }
    }
}
