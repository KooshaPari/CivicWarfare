using System;
using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.GridWarfare;
using CivicSurvival.Core.Components.Domain.GridWarfare;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.GridWarfare.Data;
using CivicSurvival.Domains.GridWarfare.Events;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.UI.DomainState;

using CivicSurvival.Core.Services;
using CivicSurvival.Core.Serialization;
namespace CivicSurvival.Domains.GridWarfare.Systems
{
    /// <summary>
    /// Manages player attack operations: Prepare → Ready → Execute flow.
    /// Uses IShadowWalletService for TryLock/ConfirmDeduct/Unlock pattern.
    /// </summary>
    public partial class PlayerAttackSystem : CivicSystemBase, IPostLoadValidation, IOperationSlotReader
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PlayerAttackSystem");

        private const int MAX_SLOTS = 3;

        // Balance — upper bound on the stability cost discount, resolved live from BalanceConfig.
        private static float MaxStabilityDiscount => BalanceConfig.Current.GridWarfare.MaxStabilityDiscount;

        // Slots (initialized in OnCreate)
        private OperationSlot[] m_Slots = null!;
        private readonly object m_SlotsLock = new();  // FIX TOCTOU: Lock for slot operations
        private int m_NextOperationId;
        private int m_NextExecutionId;

        // CIVIC050 FIX: Pre-allocated list for OnUpdate (avoids GC alloc per frame)
        private readonly System.Collections.Generic.List<string> m_ReadyOpsBuffer = new(MAX_SLOTS);

        // Dependencies (injected via SetXxx methods or WireServices)
        private IShadowWalletService m_Wallet = NullShadowWalletService.Instance;
        private ICounterAttackArsenalService m_Arsenal = NullCounterAttackArsenalService.Instance;
        private EnemySimulationSystem? m_EnemySimulation;

        private EntityQuery m_CurrentActQuery;
        // Mirror-city model (co-located on the EnemyState entity) — read at Execute to auto-select a target.
        private EntityQuery m_MirrorQuery;

        // Stability discount — derived from CityStabilitySystem push (not persisted). Cleared by
        // ResetTransientLoadState() on both the new-game and LoadGame paths, so the window before
        // this city's first CSS push prices with the neutral 0 discount rather than the previous
        // city's leftover value.
        [System.NonSerialized] private float m_StabilityDiscount = 0f;

        // Per-axis preferred strike target chosen in the War Room STRIKE view (Phase E), indexed by
        // (int)AttackCategory. UI preference only — transient: ResetTransientLoadState() clears it
        // to "auto" (NoTargetId) on BOTH the new-game path (ResetState) and the LoadGame path
        // (Deserialize), because CS2 reuses the system instance between loads and a [NonSerialized]
        // attribute alone resets nothing — without the explicit Deserialize-side clear, a pick made
        // in one city would silently aim strikes in the next loaded city. Written synchronously on
        // the UI command thread via SetStrikeTargetPreference (Axiom 14 route 1: sync UI thread →
        // managed state), read by SelectAutoTarget at Execute.
        [System.NonSerialized]
        private readonly ushort[] m_PreferredTargetByAxis =
            { EnemyTarget.NoTargetId, EnemyTarget.NoTargetId, EnemyTarget.NoTargetId };

        [System.NonSerialized] private Act m_LastSeenAct;
        [System.NonSerialized] private bool m_HasSeenAct;
        [System.NonSerialized] private readonly HashSet<string> m_ActTransitionUnlockIds = new(StringComparer.Ordinal);
        [System.NonSerialized] private readonly List<OperationCancelledEvent> m_CancelledEvents = new(MAX_SLOTS);
        [System.NonSerialized] private bool m_PendingSlotsPublish;
        private readonly VersionedView<OperationSlotsSnapshot> m_SlotsView = new(OperationSlotsSnapshot.Empty);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Slots = new OperationSlot[MAX_SLOTS];
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                m_Slots[i] = OperationSlot.Empty;
            }
            lock (m_SlotsLock)
            {
                PublishSlotsLocked();
            }
            m_NextOperationId = 1;
            m_NextExecutionId = 1;

            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_MirrorQuery = GetEntityQuery(ComponentType.ReadOnly<MirrorCityState>());

            // S3-04: Post-load reconciliation (slot/wallet consistency)

            // S8-07: Expose active slot IDs for wallet orphan-lock detection
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IOperationSlotReader>(this);

            // T3-1 fix: Clear ghost slots when wallet confiscated
#pragma warning disable CIVIC139 // EventBus set by CivicSystemBase.OnCreate, null = mod init failed
            EventBus?.Subscribe<ShadowNarrativeEvent>(OnShadowNarrativeEvent);
#pragma warning restore CIVIC139

            // WireServices() in OnStartRunning (and OnGameLoaded) — calling
            // TryGetOrNullObject from OnCreate would throw because
            // FeatureRegistry registration is still in progress.
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_EnemySimulation ??= FeatureRegistry.Instance.Require<EnemySimulationSystem>();
            WireServices();
            SeedActBaselineFromSingleton(force: false);
        }

        // OD-003 FIX: Clear service references to break circular dependencies
        protected override void OnDestroy()
        {
#pragma warning disable CIVIC139 // Matching unsubscribe — null = was never subscribed
            EventBus?.Unsubscribe<ShadowNarrativeEvent>(OnShadowNarrativeEvent);
#pragma warning restore CIVIC139
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IOperationSlotReader>(this);
            m_Wallet = null!;
            m_Arsenal = NullCounterAttackArsenalService.Instance;
            m_EnemySimulation = null;
            base.OnDestroy();
        }

        private void SeedActBaselineFromSingleton(bool force)
        {
            if (!force && m_HasSeenAct)
                return;

            if (!TryReadCurrentAct(out var current))
                return;

            m_LastSeenAct = current;
            m_HasSeenAct = true;
        }

        private void ReconcileActTransition()
        {
            if (!TryReadCurrentAct(out var current))
                return;

            if (!m_HasSeenAct)
            {
                m_LastSeenAct = current;
                m_HasSeenAct = true;
                return;
            }

            if (current == m_LastSeenAct)
                return;

            var previous = m_LastSeenAct;
            m_LastSeenAct = current;
            OnObservedActTransition(previous, current);
        }

        private bool TryReadCurrentAct(out Act current)
        {
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var singleton))
            {
                current = singleton.CurrentAct;
                return true;
            }

            current = default;
            return false;
        }

        private void OnObservedActTransition(Act previous, Act current)
        {
            _ = previous;
            var unlockIds = m_ActTransitionUnlockIds;
            unlockIds.Clear();
            var cancelledEvents = m_CancelledEvents;
            cancelledEvents.Clear();
            IShadowWalletService wallet;
            int skippedClaimedExecutions = 0;

            lock (m_SlotsLock)
            {
                wallet = m_Wallet;
                bool changed = false;
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.State == OperationState.Preparing || slot.State == OperationState.Ready)
                    {
                        string operationId = slot.OperationId;
                        if (!string.IsNullOrEmpty(operationId))
                            unlockIds.Add(operationId);
                        cancelledEvents.Add(new OperationCancelledEvent(slot.AttackType, slot.LockedAmount));
                        m_Slots[i] = OperationSlot.Empty;
                        changed = true;
                    }
                    else if (slot.State == OperationState.Executing)
                    {
                        if (TryTerminalCancelExecutionLocked(slot.ExecutionId, slot.OperationId, out var cancelledSlot))
                        {
                            if (!string.IsNullOrEmpty(cancelledSlot.OperationId))
                                unlockIds.Add(cancelledSlot.OperationId);
                            cancelledEvents.Add(new OperationCancelledEvent(cancelledSlot.AttackType, cancelledSlot.LockedAmount));
                            changed = true;
                        }
                        else
                        {
                            skippedClaimedExecutions++;
                        }
                    }
                }
                m_StabilityDiscount = 0f;
                if (changed)
                    PublishSlotsLocked();
                m_PendingSlotsPublish = false;
            }

            foreach (string unlockId in unlockIds)
                wallet.Unlock(unlockId);

            foreach (var evt in cancelledEvents)
                EventBus?.SafePublish(evt, "PlayerAttackSystem");

            string claimedSuffix = skippedClaimedExecutions > 0
                ? $"; left {skippedClaimedExecutions} claimed execution intent(s) for commit owner"
                : string.Empty;
            Log.Info($"[PlayerAttack] Reset cancellable slots on act transition → {current}{claimedSuffix}");
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            WireServices();
            SeedActBaselineFromSingleton(force: true);
            // H-04 fix: slot validation moved to ValidateAfterLoad (order 60, after wallet order 50)
            // OnGameLoaded fires before PostLoadValidationSystem — wallet may not be deserialized yet
        }

        // ============================================================================
        // POST-LOAD RECONCILIATION (IPostLoadValidation + IOperationSlotReader)
        // ============================================================================

        /// <summary>
        /// IOperationSlotReader: snapshot of active (non-Idle) slot OperationIds.
        /// Used by ShadowWalletSystem.ReconcileOrphanedLocks (S8-07).
        /// </summary>
        public void CopyActiveOperationIds(System.Collections.Generic.ICollection<string> target)
        {
            if (target == null)
                throw new System.ArgumentNullException(nameof(target));

            lock (m_SlotsLock)
            {
                target.Clear();
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    if (m_Slots[i].State != OperationState.Idle &&
                        !string.IsNullOrEmpty(m_Slots[i].OperationId))
                    {
                        target.Add(m_Slots[i].OperationId);
                    }
                }
            }
        }

        /// <summary>
        /// S3-04: For each non-Idle slot, verify wallet has a matching lock.
        /// If wallet lost the lock (desync), clear the slot — prevents ghost operation
        /// that cannot be cancelled (Unlock silently no-ops when lock absent).
        /// Order 60: AFTER ShadowWalletSystem(50) which reconciles freeze/locks first.
        /// </summary>
        public int HydrationOrder => HydrationPriority.SLOT_RECONCILE;
        public void ValidateAfterLoad()
        {
            // Pre-wallet pass: clear slots that reference unknown AttackType (registry changed between saves)
            var ghostLogs = new System.Collections.Generic.List<string>();
            var ghostUnlockIds = new System.Collections.Generic.List<string>();
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    if (m_Slots[i].State == OperationState.Idle) continue;
                    if (!AttackRegistry.Attacks.ContainsKey(m_Slots[i].AttackType))
                    {
                        ghostLogs.Add($"T3-5: Slot {i} has unknown AttackType '{m_Slots[i].AttackType}' — clearing");
                        if (!string.IsNullOrEmpty(m_Slots[i].OperationId))
                            ghostUnlockIds.Add(m_Slots[i].OperationId);
                        m_Slots[i] = OperationSlot.Empty;
                        PublishSlotsLocked();
                    }
                }
                RebaseNextOperationIdLocked();
                RebaseNextExecutionIdLocked();
            }
            foreach (var msg in ghostLogs) Log.Warn(msg);

            var wallet = GetWalletSnapshot();
            foreach (string operationId in ghostUnlockIds)
                wallet.Unlock(operationId);

            // H-04 fix: merged ValidateSlotsAfterLoad logic here (runs after wallet order 50)
            bool hasCurrentGameTime = TryGetGameTime(out var currentGameTime);
            var validationLogs = new System.Collections.Generic.List<(string msg, bool isWarn)>();
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    if (m_Slots[i].State == OperationState.Idle) continue;

                    // T3-5: empty OperationId
                    if (string.IsNullOrEmpty(m_Slots[i].OperationId))
                    {
                        validationLogs.Add(($"T3-5: Slot {i} has state {m_Slots[i].State} but no OperationId — clearing", true));
                        m_Slots[i] = OperationSlot.Empty;
                        PublishSlotsLocked();
                        continue;
                    }

                    // T3-5: non-positive LockedAmount
                    if (m_Slots[i].LockedAmount <= 0)
                    {
                        validationLogs.Add(($"T3-5: Slot {i} ({m_Slots[i].AttackType}) has non-positive LockedAmount — clearing", true));
                        wallet.Unlock(m_Slots[i].OperationId);
                        m_Slots[i] = OperationSlot.Empty;
                        PublishSlotsLocked();
                        continue;
                    }

                    // S3-04: wallet lock consistency (wallet is now deserialized — order 60 > wallet 50)
                    if (!wallet.HasLock(m_Slots[i].OperationId))
                    {
                        validationLogs.Add(($"S3-04: Slot {i} ({m_Slots[i].AttackType}, state={m_Slots[i].State}) " +
                                            $"has no wallet lock for '{m_Slots[i].OperationId}' — clearing", true));
                        m_Slots[i] = OperationSlot.Empty;
                        PublishSlotsLocked();
                        continue;
                    }

                    // R4-S6-01: rebase PrepareStartTime
                    if (m_Slots[i].State == OperationState.Preparing && hasCurrentGameTime)
                    {
                        float savedElapsed = currentGameTime - m_Slots[i].PrepareStartTime;
                        float clampedElapsed = Math.Clamp(savedElapsed, 0f, m_Slots[i].PrepareDuration);
                        float oldStart = m_Slots[i].PrepareStartTime;
                        m_Slots[i].PrepareStartTime = currentGameTime - clampedElapsed;
                        PublishSlotsLocked();
                        validationLogs.Add(($"S6-01: Slot {i} ({m_Slots[i].AttackType}) PrepareStartTime rebased {oldStart:F0} → {m_Slots[i].PrepareStartTime:F0} (elapsed={clampedElapsed:F0}s / {m_Slots[i].PrepareDuration:F0}s)", false));
                    }
                }
                RebaseNextOperationIdLocked();
                RebaseNextExecutionIdLocked();
            }
            foreach (var (msg, isWarn) in validationLogs)
            {
                if (isWarn) Log.Warn(msg); else Log.Info(msg);
            }
        }

        private void WireServices()
        {
            // Get wallet service via ServiceRegistry (cross-domain interface pattern)
            // TryLock/ConfirmDeduct/Unlock requires managed memory, hence interface not singleton.
            // Called from OnStartRunning (first wire) and OnGameLoaded (rewire across saves);
            // never from OnCreate — TryGetOrNullObject throws there because the registration
            // window has not closed yet.
#pragma warning disable CIVIC114 // Wired in OnStartRunning/OnGameLoaded only; no concurrent reads
            m_Wallet = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
            m_Arsenal = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullCounterAttackArsenalService.Instance);
#pragma warning restore CIVIC114

            Log.Info("Services wired");
        }

        private void OnShadowNarrativeEvent(ShadowNarrativeEvent evt)
        {
            if (evt.Type != ShadowNarrativeEventType.WalletConfiscated)
                return;

            var clearedSlots = new System.Collections.Generic.List<string>();
            var cancelledEvents = new System.Collections.Generic.List<OperationCancelledEvent>();
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.State == OperationState.Preparing || slot.State == OperationState.Ready)
                    {
#pragma warning disable CIVIC230 // Log accumulation: each slot index is unique in loop
                        clearedSlots.Add($"Confiscation: clearing ghost slot {i} ({slot.AttackType}, state={slot.State})");
#pragma warning restore CIVIC230
                        // FIX S3-03: Notify UI about confiscated operations (RefundedAmount=0 — funds seized)
                        cancelledEvents.Add(new OperationCancelledEvent(slot.AttackType, 0, IsConfiscated: true));
                        m_Slots[i] = OperationSlot.Empty;
                        PublishSlotsLocked();
                    }
                    else if (slot.State == OperationState.Executing)
                    {
                        if (TryTerminalCancelExecutionLocked(slot.ExecutionId, slot.OperationId, out var cancelledSlot))
                        {
#pragma warning disable CIVIC230 // Log accumulation: each slot index is unique in loop
                            clearedSlots.Add($"Confiscation: terminal-cancelled execution slot {i} ({cancelledSlot.AttackType}, execution={cancelledSlot.ExecutionId})");
#pragma warning restore CIVIC230
                            cancelledEvents.Add(new OperationCancelledEvent(cancelledSlot.AttackType, 0, IsConfiscated: true));
                            PublishSlotsLocked();
                        }
                        else
                        {
#pragma warning disable CIVIC230 // Log accumulation: each slot index is unique in loop
                            clearedSlots.Add($"Confiscation: left claimed execution slot {i} ({slot.AttackType}, execution={slot.ExecutionId}) for commit owner");
#pragma warning restore CIVIC230
                        }
                    }
                }
            }
            foreach (var cancelledEvent in cancelledEvents)
                EventBus?.SafePublish(cancelledEvent, "PlayerAttackSystem");
            foreach (var msg in clearedSlots) Log.Info(msg);
        }

        protected override void OnUpdateImpl()
        {
            ReconcileActTransition();

            if (m_PendingSlotsPublish)
            {
                lock (m_SlotsLock)
                {
                    PublishSlotsLocked();
                    m_PendingSlotsPublish = false;
                }
            }

            if (!TryGetGameTime(out var gameTime))
                return;

            // FIX HIGH: Lock slot access to prevent race with UI thread methods
            lock (m_SlotsLock)
            {
                m_ReadyOpsBuffer.Clear();
                // Check for Preparing → Ready transitions
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    if (m_Slots[i].State == OperationState.Preparing && m_Slots[i].IsPreparationComplete(gameTime))
                    {
                        m_Slots[i].State = OperationState.Ready;
                        PublishSlotsLocked();
                        m_ReadyOpsBuffer.Add(m_Slots[i].AttackType);
                    }
                }
            }
            foreach (var attackType in m_ReadyOpsBuffer)
            {
                EventBus?.SafePublish(new OperationReadyEvent(attackType), "PlayerAttackSystem");
                Log.Info($"Operation {attackType} is READY");
            }
        }

        private static bool TryGetGameTime(out float gameTimeSeconds)
        {
            // FIX: Use TotalGameHours (stable across save/load) instead of ElapsedTime (resets on load).
            // PrepareStartTime is serialized, so the clock must survive save/load.
            // LOAD-INVARIANT: runtime and post-load paths may run before GameTime activation.
            if (!GameTimeSystem.TryGetGameHours(out var gameHours))
            {
                gameTimeSeconds = 0f;
                return false;
            }

            gameTimeSeconds = gameHours * GameRate.SECONDS_PER_HOUR;
            return true;
        }

        /// <summary>
        /// Current clamped stability discount (max <see cref="MaxStabilityDiscount"/>).
        /// UI reads this to stay in sync with simulation cost calculation.
        /// </summary>
        public float StabilityDiscount => m_StabilityDiscount;

        /// <summary>
        /// Set stability discount (called by CityStabilitySystem).
        /// TS-006 FIX: Lock for thread-safe access with CalculateCost.
        /// </summary>
        public void SetStabilityDiscount(float discount)
        {
            lock (m_SlotsLock)
            {
                m_StabilityDiscount = math.clamp(discount, 0f, MaxStabilityDiscount);
            }
        }

        /// <summary>
        /// Read-only prepare verdict for UI. Must stay in sync with PrepareOperation.
        /// </summary>
        public bool CanPrepareOperation(string attackType, out FixedString64Bytes failReason)
        {
            failReason = default;

            if (!AttackRegistry.Attacks.TryGetValue(attackType, out var attack))
            {
                failReason = ReasonIds.GwUnknownAttack.ToFixedString();
                return false;
            }

            var currentAct = m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                ? actSingleton.CurrentAct
                : Act.PreWar;
            lock (m_SlotsLock)
            {
                for (int i = 0; i < m_Slots.Length; i++)
                {
                    if (m_Slots[i].AttackType == attackType && m_Slots[i].State != OperationState.Idle)
                    {
                        failReason = GetPrepareReason(
                            currentAct,
                            attack.BaseCost,
                            duplicate: true,
                            hasEmptySlot: true);
                        return false;
                    }
                }

                bool hasEmptySlot = FindEmptySlot() >= 0;
                string reason = GetPrepareReason(currentAct, attack.BaseCost, duplicate: false, hasEmptySlot);
                if (reason.Length > 0)
                {
                    failReason = reason;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Start preparing an operation. Locks funds in wallet.
        /// </summary>
        public bool PrepareOperation(string attackType)
        {
            return PrepareOperation(attackType, out _);
        }

        public bool PrepareOperation(string attackType, out FixedString64Bytes failReason)
        {
            failReason = default;

            if (!AttackRegistry.Attacks.TryGetValue(attackType, out var attack))
            {
                Log.Warn($"Unknown attack type: {attackType}");
                failReason = ReasonIds.GwUnknownAttack.ToFixedString();
                return false;
            }

            // FIX TOCTOU: Lock slot operations to prevent race between FindEmptySlot and slot assignment
            string prepareLogMsg = null!;
            bool prepareSuccess = false;
            long lockedCost = 0;
            float prepareDuration = attack.PrepareDuration;
            var currentAct = m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                ? actSingleton.CurrentAct
                : Act.PreWar;
            lock (m_SlotsLock)
            {
                // L-43 FIX: Reject duplicate attackType — only one active slot per type allowed
                bool alreadyActive = false;
                for (int i = 0; i < m_Slots.Length; i++)
                {
                    if (m_Slots[i].AttackType == attackType && m_Slots[i].State != OperationState.Idle)
                    {
                        alreadyActive = true;
                        break;
                    }
                }

                if (alreadyActive)
                {
                    prepareLogMsg = $"Operation {attackType} already in progress";
                    failReason = GetPrepareReason(
                        currentAct,
                        attack.BaseCost,
                        duplicate: true,
                        hasEmptySlot: true);
                }
                else
                {
                    // Find empty slot
                    int slotIndex = FindEmptySlot();
                    string reason = GetPrepareReason(
                        currentAct,
                        attack.BaseCost,
                        duplicate: false,
                        hasEmptySlot: slotIndex >= 0);
                    if (reason.Length > 0)
                    {
                        prepareLogMsg = reason == ReasonIds.GwWalletUnavailable
                            ? "Wallet not wired"
                            : reason;
                        failReason = reason;
                    }
                    else
                    {
                        if (!TryGetGameTime(out var gameTime))
                        {
                            prepareLogMsg = "Game time unavailable";
                            failReason = ReasonIds.GwSystemUnavailable.ToFixedString();
                        }
                        else
                        {
                            // Calculate cost with stability discount
                            long cost = CalculateCost(attack.BaseCost);
                            string operationId = $"op_{attackType}_{m_NextOperationId++}";
                            var wallet = m_Wallet;

                            // Lock funds
                            if (!wallet.TryLock(cost, operationId))
                            {
                                prepareLogMsg = $"Insufficient funds for {attackType} (need ${cost:N0})";
                                failReason = ReasonIds.GwInsufficientFunds.ToFixedString();
                            }
                            else
                            {
                                // Start preparation
                                m_Slots[slotIndex] = new OperationSlot
                                {
                                    AttackType = attackType,
                                    State = OperationState.Preparing,
                                    LockedAmount = cost,
                                    PrepareStartTime = gameTime,
                                    PrepareDuration = prepareDuration,
                                    OperationId = operationId
                                };
                                PublishSlotsLocked();
                                lockedCost = cost;
                                prepareLogMsg = $"Preparing {attackType}, cost ${cost:N0}, duration {prepareDuration}s";
                                prepareSuccess = true;
                            }
                        }
                    }
                }
            }
            if (prepareSuccess) Log.Info(prepareLogMsg);
            else Log.Warn(prepareLogMsg);
            if (prepareSuccess)
                EventBus?.SafePublish(new OperationPreparingEvent(attackType, lockedCost, prepareDuration), "PlayerAttackSystem");
            return prepareSuccess;
        }

        /// <summary>
        /// Execute a ready operation by recording a durable effect intent.
        /// </summary>
        public bool ExecuteOperation(string attackType)
        {
            return ExecuteOperation(attackType, out _);
        }

        public bool ExecuteOperation(string attackType, out FixedString64Bytes failReason)
        {
            failReason = default;

            // R3-D-1/S551 FIX: fail closed when the act singleton is unavailable.
            // Gate opens the moment the war starts (first non-PreWar act) — players rarely
            // reach Adaptation, so the counterattack is available from Crisis onward.
            if (!m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton) || actSingleton.CurrentAct < Act.Crisis)
            {
                failReason = ReasonIds.GwLockedReason.ToFixedString();
                return false;
            }

            if (m_EnemySimulation == null)
            {
                failReason = ReasonIds.GwSystemUnavailable.ToFixedString();
                return false;
            }

            if (!AttackRegistry.Attacks.TryGetValue(attackType, out var attack))
            {
                Log.Warn($"Unknown attack type in Execute: {attackType}");
                failReason = ReasonIds.GwUnknownAttack.ToFixedString();
                return false;
            }

            OperationSlot slot;
            bool executeFound;
            bool outOfStock = false;
            bool isVulnerable = m_EnemySimulation.IsVulnerableWindow();
            bool isBlocked = m_EnemySimulation.IsAttackBlocked(attack.Category);
            float actualDamage = m_EnemySimulation.CalculateDamage(attack.Category, attack.BaseDamage);

            // Physical-model launch: the operation fires a real outbound projectile, so it costs
            // one arsenal munition (drone/ballistic by category) on top of the locked Shadow Cash.
            // Fail-fast here (HasStock check, no spend) so an empty arsenal never moves the slot to
            // Executing; the actual TrySpend is done atomically with the launch in
            // EnemyOperationEffectSystem (commit) so a rolled-back commit — act-gate closed before
            // launch — never leaks a munition. Same "check then commit" shape as
            // GridOperationEligibility.CanPrepareOperation.
            var arsenalKind = ArsenalKindMap.ForCategory(attack.Category);

            // Auto-select the target now (phase C): the live target of this attack's axis with the highest
            // hp, deterministically. Read on the sync UI command thread — a small read-only fold over the
            // target buffer (same synchronous-singleton-read shape as the act gate above), nothing heavy,
            // pause-safe. Phase E replaces this with the War Room's explicit pick. NoTargetId when there is
            // no generated city yet — arrival then resolves via the legacy axis path.
            ushort targetId = SelectAutoTarget(attack.Category);

            // FIX TOCTOU: Lock slot operations to prevent race between FindSlotByType and slot modification
            lock (m_SlotsLock)
            {
                int slotIndex = FindSlotByType(attackType, OperationState.Ready);
                if (slotIndex < 0)
                {
                    executeFound = false;
                    slot = default;
                }
                else if (!m_Arsenal.HasStock(arsenalKind, 1))
                {
                    // Found a ready slot but the arsenal is empty — do not transition; reject.
                    executeFound = false;
                    outOfStock = true;
                    slot = default;
                }
                else
                {
                    executeFound = true;
                    slot = m_Slots[slotIndex];
                    slot.State = OperationState.Executing;
                    slot.ExecutionId = m_NextExecutionId++;
                    slot.ExecutionCategory = attack.Category;
                    slot.ExecutionBaseDamage = attack.BaseDamage;
                    slot.ExecutionActualDamage = actualDamage;
                    slot.ExecutionWasBlocked = isBlocked;
                    slot.ExecutionWasVulnerable = isVulnerable;
                    slot.ExecutionClaimed = false;
                    slot.TargetId = targetId;
                    m_Slots[slotIndex] = slot;
                    PublishSlotsLocked();
                }
            }
            if (!executeFound)
            {
                if (outOfStock)
                {
                    Log.Warn($"No arsenal stock ({arsenalKind}) to launch {attackType}");
                    // Typed reason: tell the player WHICH munition this operation launches —
                    // the generic text sent one player buying drones for a rocket-borne op.
                    failReason = (arsenalKind == ArsenalKind.Drone
                        ? ReasonIds.GwNoArsenalDrone
                        : ReasonIds.GwNoArsenalBallistic).ToFixedString();
                    return false;
                }
                Log.Warn($"No ready operation of type: {attackType}");
                failReason = ReasonIds.GwNotReady.ToFixedString();
                return false;
            }

            string status;
            if (isVulnerable) status = "VULNERABLE WINDOW";
            else if (isBlocked) status = "BLOCKED";
            else status = "normal";
            Log.Info($"Queued execution {slot.ExecutionId} for {attackType}: {actualDamage:F1}% damage ({status})");

            return true;
        }

        /// <summary>
        /// Cancel an operation. Returns locked funds.
        /// </summary>
        public bool CancelOperation(string attackType)
        {
            return CancelOperation(attackType, out _);
        }

        public bool CancelOperation(string attackType, out FixedString64Bytes failReason)
        {
            failReason = default;

            OperationSlot slot;
            bool cancelFound;
            // FIX TOCTOU: Lock slot operations to prevent race between find and slot modification
            lock (m_SlotsLock)
            {
                // Executing slots are durable effect intents. Only the GridWarfare
                // commit owner may clear them after commit or explicit rollback.
                int slotIndex = -1;
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    if (m_Slots[i].AttackType == attackType &&
                        (m_Slots[i].State == OperationState.Preparing || m_Slots[i].State == OperationState.Ready))
                    {
                        slotIndex = i;
                        break;
                    }
                }

                if (slotIndex < 0)
                {
                    cancelFound = false;
                    slot = default;
                }
                else
                {
                    cancelFound = true;
                    slot = m_Slots[slotIndex];

                    // Clear slot within lock
                    m_Slots[slotIndex] = OperationSlot.Empty;
                    PublishSlotsLocked();
                }
            }
            if (!cancelFound)
            {
                Log.Warn($"No active operation of type: {attackType}");
                failReason = ReasonIds.GwNoActiveOperation.ToFixedString();
                return false;
            }

            // Unlock funds (return to wallet) - outside lock as wallet has its own lock
#pragma warning disable CIVIC114 // Set once in OnCreate, then read-only; wallet has own lock
            m_Wallet.Unlock(slot.OperationId!);
#pragma warning restore CIVIC114

            EventBus?.SafePublish(new OperationCancelledEvent(attackType, slot.LockedAmount), "PlayerAttackSystem");
            Log.Info($"Cancelled {attackType}, refunded ${slot.LockedAmount:N0}");

            return true;
        }

        /// <summary>
        /// Calculate final cost with discount and markup. Shared between sim and UI.
        /// </summary>
        public static long CalculateFinalCost(long baseCost, float discount, float markup)
        {
            return (long)Math.Round(baseCost * (1f - discount) * (1f + markup));
        }

        /// <summary>
        /// Calculate cost with stability discount (instance convenience wrapper).
        /// </summary>
        private long CalculateCost(long baseCost)
        {
            // FIX T3-12: Apply sanctions markup (caller-side; wallet null = no markup)
#pragma warning disable CIVIC005
            float markup = m_Wallet.SanctionsMarkup;
#pragma warning restore CIVIC005
            return CalculateFinalCost(baseCost, m_StabilityDiscount, markup);
        }

        private string GetPrepareReason(Act currentAct, long baseCost, bool duplicate, bool hasEmptySlot)
        {
            long cost = CalculateCost(baseCost);
            long availableBalance = m_Wallet.Balance - m_Wallet.PendingDeductions;
            return GridOperationEligibility.CanPrepareOperation(
                currentAct,
                walletAvailable: m_Wallet.IsOperational,
                walletFrozen: m_Wallet.IsFrozen,
                availableBalance,
                cost,
                duplicate,
                hasEmptySlot,
                out var reasonId)
                ? ""
                : reasonId;
        }

        private int FindEmptySlot()
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if (m_Slots[i].State == OperationState.Idle)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Store (or clear) the War Room STRIKE view's preferred target for an axis (Phase E). Called
        /// synchronously from the UI trigger on the command thread — a plain managed-state write (Axiom 14
        /// route 1), pause-safe and immediately visible to the next Execute. <paramref name="targetId"/> is
        /// an <see cref="EnemyTarget.Id"/>, or <see cref="EnemyTarget.NoTargetId"/> to clear back to
        /// auto-select. No validity check here (the city may change between pick and launch); the preference
        /// is only ever <em>honoured</em> by <see cref="SelectAutoTarget"/> when the target is still live and
        /// valid, otherwise it silently falls back to the auto pick.
        /// </summary>
        public void SetStrikeTargetPreference(AttackCategory category, ushort targetId)
        {
            int axis = (int)category;
            if (axis < 0 || axis >= m_PreferredTargetByAxis.Length)
            {
                Log.Warn($"SetStrikeTargetPreference: out-of-range axis {axis}");
                return;
            }
            m_PreferredTargetByAxis[axis] = targetId;
            if (Log.IsDebugEnabled)
                Log.Debug($"Strike target preference for {category} = {(targetId == EnemyTarget.NoTargetId ? "auto" : targetId.ToString())}");
        }

        /// <summary>
        /// Readout of the per-axis strike-target preference (the last War Room pick), or
        /// <see cref="EnemyTarget.NoTargetId"/> for auto / out-of-range axis. The preference outlives
        /// the War Room overlay (it is honoured by the next Execute of the axis), so the UI producer
        /// ships it back and a re-opened overlay restores the highlight instead of silently aiming at
        /// a target it no longer shows. Read-only; safe on the sync UI command thread.
        /// </summary>
        public ushort GetStrikeTargetPreference(AttackCategory category)
        {
            int axis = (int)category;
            return axis >= 0 && axis < m_PreferredTargetByAxis.Length
                ? m_PreferredTargetByAxis[axis]
                : EnemyTarget.NoTargetId;
        }

        /// <summary>
        /// Select the mirror-city target for a strike of <paramref name="category"/> — a thin
        /// EntityManager wrapper over the shared <see cref="Core.Logic.MirrorTargetSelection"/> rule
        /// (the SAME one the arrival resolution applies, so the Execute-time aim and the landing
        /// agree by construction). A still-valid War Room pick wins (including a deliberate SEAD
        /// pick on an AA site); otherwise the automatic pick is the live contributing target of the
        /// axis with the highest hp. Returns <see cref="EnemyTarget.NoTargetId"/> when there is no
        /// generated city or nothing qualifies — arrival then re-resolves through the shared
        /// fallback. Read-only; safe on the sync UI command thread.
        /// </summary>
        private ushort SelectAutoTarget(AttackCategory category)
        {
            if (!m_MirrorQuery.TryGetSingletonEntity<MirrorCityState>(out var entity))
                return EnemyTarget.NoTargetId;

            var state = EntityManager.GetComponentData<MirrorCityState>(entity);
            if (!state.Generated || !EntityManager.HasBuffer<EnemyTarget>(entity))
                return EnemyTarget.NoTargetId;

            var buffer = EntityManager.GetBuffer<EnemyTarget>(entity, isReadOnly: true);

            int axis = (int)category;
            ushort preferred = axis >= 0 && axis < m_PreferredTargetByAxis.Length
                ? m_PreferredTargetByAxis[axis]
                : EnemyTarget.NoTargetId;

            return Core.Logic.MirrorTargetSelection.SelectAutoTargetId(buffer, category, preferred);
        }

        private int FindSlotByType(string attackType, OperationState requiredState)
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if (m_Slots[i].AttackType == attackType && m_Slots[i].State == requiredState)
                    return i;
            }
            return -1;
        }

        private IShadowWalletService GetWalletSnapshot()
        {
            lock (m_SlotsLock)
            {
                return m_Wallet;
            }
        }

        private void RebaseNextOperationIdLocked()
        {
            int next = Math.Max(m_NextOperationId, 1);
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                string operationId = m_Slots[i].OperationId;
                if (string.IsNullOrEmpty(operationId)) continue;

                int suffixStart = operationId.LastIndexOf('_') + 1;
                if (suffixStart <= 0 || suffixStart >= operationId.Length) continue;
                if (int.TryParse(operationId.Substring(suffixStart), out int id) && id >= next)
                    next = id + 1;
            }
            m_NextOperationId = next;
        }

        private void RebaseNextExecutionIdLocked()
        {
            int next = Math.Max(m_NextExecutionId, PlayerAttackCodec.DefaultNextExecutionId);
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                int executionId = m_Slots[i].ExecutionId;
                if (executionId >= next)
                    next = executionId + 1;
            }
            m_NextExecutionId = next;
        }

        private void PublishSlotsLocked()
        {
            var slots = new OperationSlotSnapshot[MAX_SLOTS];
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                var slot = m_Slots[i];
                slots[i] = new OperationSlotSnapshot(
                    slot.AttackType,
                    (int)slot.State,
                    slot.LockedAmount,
                    slot.PrepareStartTime,
                    slot.PrepareDuration,
                    slot.OperationId);
            }
            m_SlotsView.Publish(new OperationSlotsSnapshot(slots));
        }

        public IVersionedView<OperationSlotsSnapshot> SlotsView => m_SlotsView;

        internal void ClaimPendingOperationEffects(List<OperationExecutedEvent> target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.State != OperationState.Executing || slot.ExecutionClaimed)
                        continue;

                    target.Add(new OperationExecutedEvent(
                        AttackType: slot.AttackType,
                        Category: slot.ExecutionCategory,
                        BaseDamage: slot.ExecutionBaseDamage,
                        ActualDamage: slot.ExecutionActualDamage,
                        WasBlocked: slot.ExecutionWasBlocked,
                        WasVulnerable: slot.ExecutionWasVulnerable,
                        ShadowSpent: slot.LockedAmount,
                        OperationId: slot.OperationId,
                        ExecutionId: slot.ExecutionId,
                        TargetId: slot.TargetId));
                    slot.ExecutionClaimed = true;
                    m_Slots[i] = slot;
                }
            }
        }

        internal bool CompleteOperationExecution(int executionId, string operationId)
        {
            if (executionId <= 0 || string.IsNullOrEmpty(operationId))
                return false;

            bool completed = false;
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.State != OperationState.Executing ||
                        !slot.ExecutionClaimed ||
                        slot.ExecutionId != executionId ||
                        !string.Equals(slot.OperationId, operationId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    m_Slots[i] = OperationSlot.Empty;
                    PublishSlotsLocked();
                    completed = true;
                    break;
                }
            }

            if (!completed)
                return false;

#pragma warning disable CIVIC114 // Set once in lifecycle, then read-only; wallet has own lock
            m_Wallet.ConfirmDeduct(operationId);
#pragma warning restore CIVIC114
            return true;
        }

        internal void RollbackOperationExecution(int executionId, string operationId)
        {
            if (executionId <= 0 || string.IsNullOrEmpty(operationId))
                return;

            bool rolledBack = false;
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.State != OperationState.Executing ||
                        !slot.ExecutionClaimed ||
                        slot.ExecutionId != executionId ||
                        !string.Equals(slot.OperationId, operationId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    slot.State = OperationState.Ready;
                    slot.ExecutionId = 0;
                    slot.ExecutionCategory = AttackCategory.Kinetic;
                    slot.ExecutionBaseDamage = 0f;
                    slot.ExecutionActualDamage = 0f;
                    slot.ExecutionWasBlocked = false;
                    slot.ExecutionWasVulnerable = false;
                    slot.ExecutionClaimed = false;
                    m_Slots[i] = slot;
                    PublishSlotsLocked();
                    rolledBack = true;
                    break;
                }
            }

            if (rolledBack)
                Log.Warn($"Rolled back execution intent {executionId} for {operationId}");
        }

        /// <summary>
        /// Terminal close of a launched execution whose normal <see cref="CompleteOperationExecution"/>
        /// could not confirm (the slot drifted out of the expected Executing+Claimed shape). The
        /// projectile is already in flight, so the operation can never return to Ready: whatever
        /// state the slot is in, clear it and confirm the wallet deduction (the strike was
        /// delivered — the cost is owed). Total by design — after this call no slot references the
        /// execution, so a contract drift can never leave a permanently stuck Executing slot.
        /// </summary>
        internal void TerminalClearClaimedExecution(int executionId, string operationId)
        {
            if (executionId <= 0 || string.IsNullOrEmpty(operationId))
                return;

            bool cleared = false;
            lock (m_SlotsLock)
            {
                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    var slot = m_Slots[i];
                    if (slot.ExecutionId != executionId ||
                        !string.Equals(slot.OperationId, operationId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    m_Slots[i] = OperationSlot.Empty;
                    PublishSlotsLocked();
                    cleared = true;
                    break;
                }
            }

            if (!cleared)
                return; // no slot references the execution — nothing to close

#pragma warning disable CIVIC114 // Set once in lifecycle, then read-only; wallet has own lock
            m_Wallet.ConfirmDeduct(operationId);
#pragma warning restore CIVIC114
            Log.Warn($"Terminally closed execution {executionId} for {operationId} (launched, but the slot had drifted out of the commit shape)");
        }

        [CallerHoldsLock("m_SlotsLock")]
        private bool TryTerminalCancelExecutionLocked(int executionId, string operationId, out OperationSlot cancelledSlot)
        {
            cancelledSlot = default;
            if (executionId <= 0 || string.IsNullOrEmpty(operationId))
                return false;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                var slot = m_Slots[i];
                if (slot.State != OperationState.Executing ||
                    slot.ExecutionClaimed ||
                    slot.ExecutionId != executionId ||
                    !string.Equals(slot.OperationId, operationId, StringComparison.Ordinal))
                {
                    continue;
                }

                cancelledSlot = slot;
                m_Slots[i] = OperationSlot.Empty;
                return true;
            }

            return false;
        }

    }
}
