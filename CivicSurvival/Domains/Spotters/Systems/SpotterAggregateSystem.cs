using Game;
using Game.Common;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Spotters;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Spotters.Systems
{
    /// <summary>
    /// Sole writer for spotter domain state. Drains command queue, then runs simulation phases.
    ///
    /// Phases per throttled update (500ms):
    /// 0. DrainCommands: apply all commands from ingress systems
    /// 1. Lifecycle: Reactivation + penalty calculation (single pass)
    /// 2. Detection: Civilians report spotters (Telemarathon vigilance)
    /// 3. Economy: Daily Counter-OSINT cost
    /// 4. Stats: Update singleton for UI
    ///
    /// STALENESS — ACCEPTED (H12, H13):
    /// H12: AirDefense domain runs before Spotters (lower priority) → ADO/BDS read SpotterPenaltyState
    ///      before this system writes it. Deterministic 1-frame stale on wave events (~1/min). Irrelevant.
    /// H13: SpotterBudgetIngressSystem is registered after SpotterAggregateSystem in SpottersDomain.
    ///      Ingress enqueues rollback/finalize after aggregate drains queue → command waits until next
    ///      throttled tick. Delay ≤ throttle period (~500ms). By-design throttle behavior.
    /// </summary>
    [SingletonOwner(typeof(SpotterCountermeasuresState))]
    [SingletonOwner(typeof(SpotterPenaltyState))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
#pragma warning disable CIVIC228 // Budget failure disables Counter-OSINT — background state, 500ms UI delay acceptable
    public partial class SpotterAggregateSystem : ThrottledSystemBase, IResettable, IPostLoadValidation
#pragma warning restore CIVIC228
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("SpotterAggregateSystem");

        // Queries
        private EntityQuery m_CountermeasuresStateQuery;
        private EntityQuery m_PenaltyStateQuery;
        private EntityQuery m_SpotterQuery;
        private EntityQuery m_TelemarathonQuery;

        // PERF: TypeHandle for lifecycle chunk iteration (avoids SystemAPI.Query dependency tracker leak)
        private ComponentTypeHandle<SpotterData> m_SpotterDataTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;

        // ECB
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        // Buffer lookups
        private BufferLookup<EvacuatedReturnBuffer> m_EvacReturnBufferLookup;
        private BufferLookup<InternetDisabledBuffer> m_InternetDisabledBufferLookup;

        // Spotter data lookup (for detection phase writes)
        private ComponentLookup<SpotterData> m_SpotterDataLookup;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;

        // Bundle for external owner entry points. The bundle owns lookup freshness for
        // spotter command application invoked outside the throttled update tick.
        // Initialized once in OnCreate; survives ResetState (no per-session mutable state).
        [System.NonSerialized] private CivicServiceLookups m_CmdLookups = null!;

        // Time provider
        private GameTimeSystem? m_TimeProvider;

        // State (serialized)
        private double m_LastDailyTick;
        [System.NonSerialized] private bool m_Initialized;
        [System.NonSerialized] private bool m_PendingGhostSweep;

        // Cached stats (written to singleton)
        [System.NonSerialized] private int m_ActiveSpotterCount;
        [System.NonSerialized] private int m_ActionableSpotterCount;
        [System.NonSerialized] private int m_TotalSpotterCount;
        [System.NonSerialized] private float m_GlobalPenalty;
        [System.NonSerialized] private float m_RawPenalty;

        // Command mailbox
        private NativeQueue<SpotterCommand> m_CommandQueue;

        private struct SpotterDedupEntry
        {
            public Entity Entity;
            public bool IsCharacterSpotter;
            public bool CountedActive;
            public bool CountedActionable;
        }

        /// <summary>Shared phase key — fires on same frame as SpotterCommandIngressSystem.</summary>
        protected override string ThrottlePhaseKey => "SpotterPipeline";

        /// <summary>Enqueue a command from ingress systems. Thread-safe for single writer (main thread).</summary>
        public void EnqueueCommand(SpotterCommand cmd) => m_CommandQueue.Enqueue(cmd);

        /// <summary>
        /// Toggle district internet through the spotter state owner. Used by the
        /// pause-safe DistrictInternetToggle consumer outside the throttled tick.
        /// </summary>
        public void ToggleInternetForDistrict(int districtIndex)
        {
            m_CmdLookups.RefreshIfStale();

            if (!m_CountermeasuresStateQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var singletonEntity))
            {
                Log.Error("SpotterCountermeasuresState not found!");
                return;
            }

            if (!m_InternetDisabledBufferLookup.TryGetBuffer(singletonEntity, out var buffer)) return;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].DistrictIndex == districtIndex)
                {
                    buffer.RemoveAt(i);

                    EventBus?.SafePublish(new PenaltyRemovedEvent(districtIndex, PenaltySource.InternetDisabled),
                        "SpotterAggregateSystem");

                    Log.Info($"Internet ENABLED for district {districtIndex}");
                    return;
                }
            }

            buffer.Add(new InternetDisabledBuffer { DistrictIndex = districtIndex });

            EventBus?.SafePublish(new PenaltyRegisteredEvent(districtIndex, PenaltySource.InternetDisabled),
                "SpotterAggregateSystem");
            EventBus?.SafePublish(new NarrativeTriggerEvent(
                NarrativeTrigger.InternetDisabled.ToKey()), "SpotterAggregateSystem");
            EventBus?.SafePublish(new NarrativeTriggerEvent(
                NarrativeTrigger.MarianaInternet.ToKey()), "SpotterAggregateSystem");

            Log.Info($"Internet DISABLED for district {districtIndex}");
        }

        /// <summary>
        /// Applies a confirmed retained spotter budget operation without a non-durable
        /// command handoff window. The caller persists idempotency on SpotterBudgetIntent.
        /// </summary>
        public bool ApplyConfirmedBudgetIntent(in SpotterBudgetIntent intent)
        {
            m_CmdLookups.RefreshIfStale();

            var cmd = new SpotterCommand
            {
                TargetIndex = intent.Target.Index,
                TargetVersion = intent.Target.Version,
                Cost = intent.Cost
            };

            switch (intent.Action)
            {
                case AirDefenseActionType.PerformSBUVisit:
                    cmd.Type = SpotterCommandType.PerformSBU;
                    if (!TryResolveCommandTarget(cmd, "ApplyConfirmedSBU", out var sbuTarget, out var sbuData))
                        return false;
                    if (sbuData.IsActive)
                    {
                        if (!DrainPerformSBU(cmd))
                            return false;
                    }
                    else
                    {
                        ApplyEquivalentSBUValue(sbuTarget, ref sbuData);
                    }
                    return DrainFinalizeSBU(cmd);

                case AirDefenseActionType.PerformEvacuation:
                    cmd.Type = SpotterCommandType.RequestEvacuation;
                    if (!TryResolveCommandTarget(cmd, "ApplyConfirmedEvacuation", out _, out var evacData))
                        return false;
                    if (!evacData.IsEvacuating && !DrainRequestEvacuation(cmd))
                        return false;
                    cmd.Type = SpotterCommandType.FinalizeEvacuation;
                    return DrainFinalizeEvacuation(cmd);

                case AirDefenseActionType.ToggleCounterOSINT:
                    return DrainEnableCounterOSINT();

                case AirDefenseActionType.CounterOSINTDailyCost:
                    return ApplyCounterOSINTDailyCost(intent);

                default:
                    return false;
            }
        }

        public void RollbackFailedBudgetIntent(in SpotterBudgetIntent intent)
        {
            m_CmdLookups.RefreshIfStale();

            switch (intent.Action)
            {
                case AirDefenseActionType.PerformSBUVisit:
                    break;
                case AirDefenseActionType.PerformEvacuation:
                    break;
                case AirDefenseActionType.CounterOSINTDailyCost:
                    EnqueueCommand(new SpotterCommand
                    {
                        Type = SpotterCommandType.DisableCounterOSINT,
                        NarrativeHint = NarrativeTrigger.CounterOsintCancel,
                        HasNarrativeHint = true
                    });
                    break;
                default:
                    return;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_TimeProvider = GameTimeSystem.Instance;

            m_CountermeasuresStateQuery = GetEntityQuery(
                ComponentType.ReadWrite<SpotterCountermeasuresState>()
            );

            m_PenaltyStateQuery = GetEntityQuery(
                ComponentType.ReadWrite<SpotterPenaltyState>()
            );

            m_TelemarathonQuery = GetEntityQuery(ComponentType.ReadOnly<TelemarathonRuntimeState>());

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_SpotterDataLookup = GetComponentLookup<SpotterData>(false);
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_EvacReturnBufferLookup = GetBufferLookup<EvacuatedReturnBuffer>(false);
            m_InternetDisabledBufferLookup = GetBufferLookup<InternetDisabledBuffer>(false);

            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_CmdLookups = new CivicServiceLookups(() =>
            {
#pragma warning disable CIVIC289 // External budget drain refreshes lookups before direct command application.
                m_SpotterDataLookup.Update(this);
                m_StorageInfoLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                m_EvacReturnBufferLookup.Update(this);
                m_InternetDisabledBufferLookup.Update(this);
#pragma warning restore CIVIC289
                m_TimeProvider ??= GameTimeSystem.Instance;
            });

            m_SpotterQuery = GetEntityQuery(
                ComponentType.ReadWrite<SpotterData>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Destroyed>()
            );
            m_SpotterDataTypeHandle = GetComponentTypeHandle<SpotterData>(false);
            m_EntityTypeHandle = GetEntityTypeHandle();

            // Domain-Driven Initialization (ensure singletons exist)
            SpotterStatsSingleton.EnsureExists(EntityManager);
            SpotterPenaltyState.EnsureExists(EntityManager);
            SpotterCountermeasuresState.EnsureExists(EntityManager);

            // Command mailbox
            m_CommandQueue = new NativeQueue<SpotterCommand>(Allocator.Persistent);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            EnsureSpotterSingletons();
        }

        /// <summary>
        /// Sync-seed Step 2: publish reconciled spotter aggregates (counts, penalty,
        /// SBU cost, countermeasure state) into the UI-visible singletons inside PLVS
        /// Phase 2. Without this, EnsureExists alone leaves SpotterStatsSingleton +
        /// SpotterPenaltyState at Default until the first throttled tick after unpause,
        /// so any UI panel reading them shows zero spotters / zero penalty on load.
        /// Recount path is side-effect-free: no orphan ECB writes, no reactivation
        /// timers, no command drain — only counts derived from restored SpotterData.
        /// </summary>
        public void ValidateAfterLoad()
        {
            EnsureSpotterSingletons();

            // IPostLoadValidation contract: refresh all lookups touched below.
            m_SpotterDataLookup.Update(this);
            m_StorageInfoLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
            m_EvacReturnBufferLookup.Update(this);
            m_InternetDisabledBufferLookup.Update(this);

            RecountSpotterStatsForSeed();
            ApplyPenaltyFromCount(m_ActiveSpotterCount);
            UpdateStatsSingleton();
        }

        /// <summary>
        /// Sync-seed helper: recount Total/Active/Actionable from restored SpotterData
        /// without the side effects of UpdateSpotterLifecycle (no orphan destroy ECB,
        /// no reactivation timer flips, no dedup ECB). Mirrors the count formula in
        /// UpdateSpotterLifecycle so the seed value matches the first throttled tick.
        /// </summary>
        private void RecountSpotterStatsForSeed()
        {
            bool hasIdb = TryGetInternetDisabledBuffer(out var idbBuffer);
            int totalCount = 0;
            int activeCount = 0;
            int actionableCount = 0;

            m_SpotterDataTypeHandle.Update(this);
            m_EntityTypeHandle.Update(this);
            using var chunks = m_SpotterQuery.ToArchetypeChunkArray(Allocator.Temp);

            for (int c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var spotters = chunk.GetNativeArray(ref m_SpotterDataTypeHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var sd = spotters[i];
                    totalCount++;
                    if (sd.IsActive && (!hasIdb || !IsInternetDisabled(sd.DistrictIndex, idbBuffer)))
                        activeCount++;
                    if (sd.IsActive && !sd.IsEvacuating)
                        actionableCount++;
                }
            }

            m_TotalSpotterCount = totalCount;
            m_ActiveSpotterCount = activeCount;
            m_ActionableSpotterCount = actionableCount;
        }

        private void EnsureSpotterSingletons()
        {
            SpotterStatsSingleton.EnsureExists(EntityManager);
            SpotterPenaltyState.EnsureExists(EntityManager);
            SpotterCountermeasuresState.EnsureExists(EntityManager);
        }

        protected override void OnThrottledUpdate()
        {
            m_InternetDisabledBufferLookup.Update(this);
            m_EvacReturnBufferLookup.Update(this);
#pragma warning disable CIVIC289 // Unconditional — Update is before any lookup usage
            m_SpotterDataLookup.Update(this);
            m_StorageInfoLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
#pragma warning restore CIVIC289

            m_TimeProvider ??= GameTimeSystem.Instance;
            if (m_TimeProvider == null) { Log.Error("TimeProvider unavailable"); return; }
            double currentTime = m_TimeProvider.Current.TotalGameHours;

            // H10/H16: ghost spotter sweep (deferred from Deserialize)
            if (m_PendingGhostSweep)
            {
                m_PendingGhostSweep = false;
                EntityCommandBuffer? ghostEcb = null;
                foreach (var (sd, entity) in SystemAPI.Query<RefRW<SpotterData>>().WithNone<Deleted, Destroyed>().WithEntityAccess())
                {
                    if (sd.ValueRO.IsEvacuating && !HasMatchingEvacuationBudgetRequest(entity))
                    {
                        sd.ValueRW.IsEvacuating = false;
                        Log.Warn($"Post-load evacuation rollback: spotter {entity.Index} (no retained budget request/result)");
                    }

                    if (!sd.ValueRO.IsActive && sd.ValueRO.ReactivateTime <= 0)
                    {
                        Log.Warn($"H10: Ghost spotter {entity.Index} queued for destroy (IsActive=false, ReactivateTime=0 after load)");
                        ghostEcb ??= m_GameSimulationEndBarrier.CreateCommandBuffer();
                        ghostEcb.Value.DestroyEntity(entity);
                    }
                }
            }

            if (!m_Initialized)
            {
                m_LastDailyTick = currentTime;
                m_Initialized = true;
            }

            var pendingDeletes = new NativeHashSet<Entity>(16, Allocator.Temp);

            // PHASE 0: DRAIN COMMANDS
            DrainCommands(pendingDeletes);

            // PHASE 1: LIFECYCLE (single pass — reactivation, counts, penalty, orphan cleanup)
            UpdateSpotterLifecycle(currentTime, pendingDeletes);

            // PHASE 2: DETECTION (Telemarathon civilian vigilance)
            // Early returns first; only does a second pass with reservoir sampling if RNG roll passes
            ProcessCivilianDetection(currentTime, pendingDeletes);

            // PHASE 4: ECONOMY (daily Counter-OSINT cost)
            ProcessDailyTick(currentTime);

            // PHASE 5: STATS (UI singleton)
            UpdateStatsSingleton();

            pendingDeletes.Dispose();
        }

        // ============================================================================
        // PHASE 0: DRAIN COMMANDS
        // ============================================================================

        private void DrainCommands(NativeHashSet<Entity> pendingDeletes)
        {
            while (m_CommandQueue.TryDequeue(out var cmd))
            {
                bool applied = true;
                switch (cmd.Type)
                {
                    case SpotterCommandType.None:
                        applied = false;
                        break;

                    case SpotterCommandType.RequestEvacuation:
                        applied = DrainRequestEvacuation(cmd);
                        break;

                    case SpotterCommandType.EnableCounterOSINT:
                        applied = DrainEnableCounterOSINT();
                        break;

                    case SpotterCommandType.DisableCounterOSINT:
                        applied = DrainDisableCounterOSINT();
                        break;

                    case SpotterCommandType.RollbackEvacuation:
                        DrainRollbackEvacuation(cmd);
                        break;

                    case SpotterCommandType.FinalizeEvacuation:
                        applied = DrainFinalizeEvacuation(cmd, pendingDeletes, trackPendingDelete: true);
                        break;

                    case SpotterCommandType.FinalizeSBU:
                        applied = DrainFinalizeSBU(cmd);
                        break;

                    default:
                        Log.Warn($"Unknown SpotterCommandType: {cmd.Type}");
                        applied = false;
                        break;
                }

                // Narrative hint: publish only if command was actually applied
                if (applied && cmd.HasNarrativeHint)
                {
                    EventBus?.SafePublish(new NarrativeTriggerEvent(
                        cmd.NarrativeHint.ToKey()), "SpotterAggregateSystem");
                }
            }
        }

        /// <summary>
        /// Resolve command target entity and read SpotterData. Returns false if entity is
        /// missing, already Deleted/Destroyed, or lacks SpotterData (ECB-deferred Deleted from
        /// FinalizeEvacuation or ghost sweep may still be pending).
        /// </summary>
        private bool TryResolveCommandTarget(SpotterCommand cmd, string caller, out Entity target, out SpotterData data)
        {
            target = new Entity { Index = cmd.TargetIndex, Version = cmd.TargetVersion };
            if (m_DeletedLookup.HasComponent(target)
                || m_DestroyedLookup.HasComponent(target)
                || !m_SpotterDataLookup.TryGetComponent(target, out data))
            {
                Log.Warn($"{caller}: spotter {cmd.TargetIndex}:{cmd.TargetVersion} not found, Deleted, or Destroyed");
                target = Entity.Null;
                data = default;
                return false;
            }
            return true;
        }

        private bool DrainPerformSBU(SpotterCommand cmd)
        {
            if (!TryResolveCommandTarget(cmd, "PerformSBU", out var target, out var spotterData))
                return false;

            // GUARD: double-action protection
            if (!spotterData.IsActive)
            {
                Log.Warn($"PerformSBU guard: spotter {target.Index} already inactive — skipping");
                return false;
            }

            var cfg = BalanceConfig.Current.Spotter;
            double currentTime = m_TimeProvider!.Current.TotalGameHours;

            spotterData.IsActive = false;
            spotterData.ReactivateTime = currentTime + cfg.SbuSilenceDays * (double)GameRate.HOURS_PER_DAY;
            if (m_SpotterDataLookup.HasComponent(target))
                m_SpotterDataLookup[target] = spotterData;

            if (m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                stateRef.ValueRW.TotalSBUVisits++;

            Log.Info($"SBU deactivated spotter {target.Index}, budget confirmed (cost: ${cmd.Cost})");
            return true;
        }

        private void ApplyEquivalentSBUValue(Entity target, ref SpotterData spotterData)
        {
            var cfg = BalanceConfig.Current.Spotter;
            double currentTime = m_TimeProvider!.Current.TotalGameHours;
            double paidUntil = currentTime + cfg.SbuSilenceDays * (double)GameRate.HOURS_PER_DAY;
            spotterData.IsActive = false;
            spotterData.ReactivateTime = math.max(spotterData.ReactivateTime, paidUntil);
            if (m_SpotterDataLookup.HasComponent(target))
                m_SpotterDataLookup[target] = spotterData;

            if (m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                stateRef.ValueRW.TotalSBUVisits++;

            Log.Info($"SBU equivalent value applied to inactive spotter {target.Index}; silence extended to {spotterData.ReactivateTime:F1}h");
        }

        private bool DrainRequestEvacuation(SpotterCommand cmd)
        {
            if (!TryResolveCommandTarget(cmd, "RequestEvacuation", out var target, out var spotterData))
                return false;

            // GUARD: double-action protection
            if (spotterData.IsEvacuating)
            {
                Log.Warn($"RequestEvacuation guard: spotter {target.Index} already evacuating — skipping");
                return false;
            }

            spotterData.IsEvacuating = true;
            if (m_SpotterDataLookup.HasComponent(target))
                m_SpotterDataLookup[target] = spotterData;
            return true;
        }

        private bool DrainEnableCounterOSINT()
        {
            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                return false;

            // GUARD: idempotent
            if (stateRef.ValueRO.CounterOSINTActive)
                return false;

            stateRef.ValueRW.CounterOSINTActive = true;
            // H15 fix: advance daily tick to prevent double-charge when ProcessDailyTick is overdue
            m_TimeProvider ??= GameTimeSystem.Instance;
            m_LastDailyTick = m_TimeProvider?.Current.TotalGameHours ?? m_LastDailyTick;
            EventBus?.SafePublish(new CounterOSINTToggledEvent(true));
            Log.Info("Counter-OSINT ENABLED");
            return true;
        }

        private bool DrainDisableCounterOSINT()
        {
            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                return false;

            if (!stateRef.ValueRO.CounterOSINTActive)
                return false;

            stateRef.ValueRW.CounterOSINTActive = false;
            EventBus?.SafePublish(new CounterOSINTToggledEvent(false));
            Log.Info("Counter-OSINT DISABLED");
            return true;
        }

        /// <summary>
        /// Budget confirmed for SBU visit — apply side-effects that cannot be reversed.
        /// Runs only after the retained budget entity resolves successfully.
        /// </summary>
        /// <remarks>
        /// Confirmed-path only: ApplyConfirmedBudgetIntent runs DrainPerformSBU (deactivate +
        /// TotalSBUVisits++) then DrainFinalizeSBU on the resolved budget entity. No producer
        /// enqueues PerformSBU, so there is no optimistic/queue path and nothing to roll back on
        /// budget failure (the mutation never happens before confirmation). FinalizeSBU only
        /// touches the singleton (TotalSBUVisits, article roll) and publishes events;
        /// cmd.TargetIndex/Version are retained for validation (confirming the spotter exists).
        /// </remarks>
        private bool DrainFinalizeSBU(SpotterCommand cmd)
        {
            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
            {
                Log.Warn($"FinalizeSBU: singleton missing (target {cmd.TargetIndex}:{cmd.TargetVersion})");
                return false;
            }

            var balance = BalanceConfig.Current;
            var cfg = balance.Spotter;

            // TotalSBUVisits is incremented in DrainPerformSBU on the confirmed path.
            // Use (count - 1) for article chance to preserve original semantics (chance based on prior visits).
            int priorVisits = math.max(0, stateRef.ValueRO.TotalSBUVisits - 1);
            float articleChance = cfg.SbuArticleBaseChance +
                                  priorVisits * cfg.SbuArticleIncrement;
            articleChance = math.min(articleChance, cfg.SbuArticleMaxChance);

            bool articleWritten = stateRef.ValueRW.RandomState.NextFloat() < articleChance;

            if (articleWritten)
            {
                EventBus?.SafePublish(new NarrativeTriggerEvent(
                    NarrativeTrigger.MarianaSbu.ToKey()), "SpotterAggregateSystem");
                EventBus?.SafePublish(new CorruptionGainEvent(
                    balance.Countermeasures.SbuSuspicionGain, "MarianaArticle"),
                    "SpotterAggregateSystem");
            }

            // No increment here — already done in DrainPerformSBU.

            EventBus?.SafePublish(new NarrativeTriggerEvent(
                NarrativeTrigger.SpotterSbuVisit.ToKey()), "SpotterAggregateSystem");

            EventBus?.SafePublish(new SpotterActionEvent("sbu_visit", 0, true));
            Log.Info($"SBU finalized: visit #{stateRef.ValueRO.TotalSBUVisits} (spotter {cmd.TargetIndex}:{cmd.TargetVersion})");
            return true;
        }

        private bool ApplyCounterOSINTDailyCost(in SpotterBudgetIntent intent)
        {
            if (!m_CountermeasuresStateQuery.TryGetSingleton<SpotterCountermeasuresState>(out var state) || !state.CounterOSINTActive)
                return false;

            if (intent.CoveredUntilGameHour > m_LastDailyTick)
                m_LastDailyTick = intent.CoveredUntilGameHour;

            if (Log.IsDebugEnabled)
                Log.Debug($"Counter-OSINT daily cost confirmed: ${intent.Cost} ({intent.Days} day(s))");
            return true;
        }

        private void DrainRollbackEvacuation(SpotterCommand cmd)
        {
            if (!TryResolveCommandTarget(cmd, "RollbackEvacuation", out var target, out var spotterData))
                return;
            if (!spotterData.IsEvacuating)
                return;

            spotterData.IsEvacuating = false;
            if (m_SpotterDataLookup.HasComponent(target))
                m_SpotterDataLookup[target] = spotterData;
            EventBus?.SafePublish(new SpotterActionEvent("evacuation_rollback", 0, false));
            Log.Warn($"Evacuation rolled back — spotter {target.Index} restored");
        }

        private bool DrainFinalizeEvacuation(SpotterCommand cmd)
        {
            return DrainFinalizeEvacuation(cmd, default, trackPendingDelete: false);
        }

        private bool DrainFinalizeEvacuation(
            SpotterCommand cmd,
            NativeHashSet<Entity> pendingDeletes,
            bool trackPendingDelete)
        {
            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                return false;

            if (!m_CountermeasuresStateQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var singletonEntity))
                return false;

            if (!TryResolveCommandTarget(cmd, "FinalizeEvacuation", out var target, out var spotterData))
                return false;

            // GUARD: not evacuating — already rolled back or double-action
            if (!spotterData.IsEvacuating)
            {
                Log.Warn($"FinalizeEvacuation guard: spotter {target.Index} not evacuating — skipping");
                return false;
            }

            bool hasReturnBuffer = m_EvacReturnBufferLookup.TryGetBuffer(singletonEntity, out var returnBuffer);
            var evCfg = BalanceConfig.Current.Spotter;
            m_TimeProvider ??= GameTimeSystem.Instance;

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();

            // H10: set IsActive=false as persisted marker for ghost sweep after load
            spotterData.IsActive = false;
            m_SpotterDataLookup[target] = spotterData;
            ecb.AddComponent<Deleted>(target);
            if (trackPendingDelete)
                pendingDeletes.Add(target);

            stateRef.ValueRW.TotalEvacuations++;

            // Chance return after configured days
            if (hasReturnBuffer
                && stateRef.ValueRW.RandomState.NextFloat() < evCfg.EvacuationReturnChance
                && m_TimeProvider != null)
            {
                double currentTime = m_TimeProvider.Current.TotalGameHours;
                double returnTime = currentTime + evCfg.EvacuationReturnDays * (double)GameRate.HOURS_PER_DAY;
                returnBuffer.Add(new EvacuatedReturnBuffer { ReturnTime = returnTime });
                Log.Info($"Evacuated spotter might return in {evCfg.EvacuationReturnDays} days...");
            }

            EventBus?.SafePublish(new NarrativeTriggerEvent(
                NarrativeTrigger.SpotterEvacuated.ToKey()), "SpotterAggregateSystem");

            EventBus?.SafePublish(new SpotterActionEvent("evacuation", 0, true));
            Log.Info($"Evacuation #{stateRef.ValueRO.TotalEvacuations} finalized for spotter {target.Index}");
            m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            return true;
        }

        // ============================================================================
        // PHASE 1: LIFECYCLE + PENALTY + ORPHAN CLEANUP (SINGLE PASS)
        // ============================================================================

        /// <summary>
        /// Single pass: orphan cleanup, reactivation, active/total count, penalty calculation.
        /// Orphan cleanup: removes SpotterData whose building no longer exists or version mismatch
        /// (covers building demolish, Valera rebind on save/load, entity recycling).
        /// No candidate materialization — detection uses separate reservoir sampling pass only when needed.
        /// </summary>
        private void UpdateSpotterLifecycle(double currentTime, NativeHashSet<Entity> pendingDeletes)
        {
            bool hasIdb = TryGetInternetDisabledBuffer(out var idbBuffer);
            int activeCount = 0;
            int actionableCount = 0;
            int totalCount = 0;
            EntityCommandBuffer? orphanEcb = null;

            m_SpotterDataTypeHandle.Update(this);
            m_EntityTypeHandle.Update(this);
            var chunks = m_SpotterQuery.ToArchetypeChunkArray(Unity.Collections.Allocator.Temp);
            int spotterCapacity = 0;
            for (int c = 0; c < chunks.Length; c++)
                spotterCapacity += chunks[c].Count;

            var seenByBuilding = new NativeHashMap<long, SpotterDedupEntry>(
                math.max(1, spotterCapacity),
                Allocator.Temp);
            for (int c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var spotters = chunk.GetNativeArray(ref m_SpotterDataTypeHandle);
                var entities = chunk.GetNativeArray(m_EntityTypeHandle);

                for (int i = 0; i < chunk.Count; i++)
                {
                    var sd = spotters[i];

                    // Orphan cleanup: building demolished or version recycled
                    var building = sd.GetBuildingEntity();
                    if (!m_StorageInfoLookup.Exists(building)
                        || m_DeletedLookup.HasComponent(building)
                        || m_DestroyedLookup.HasComponent(building))
                    {
                        orphanEcb ??= m_GameSimulationEndBarrier.CreateCommandBuffer();
                        orphanEcb.Value.AddComponent<Deleted>(entities[i]);
                        pendingDeletes.Add(entities[i]);
                        Log.Info($"Orphaned spotter {entities[i].Index} removed (building {sd.Building.Index} gone)");
                        continue;
                    }

                    if (!sd.IsActive &&
                        sd.ReactivateTime > 0 &&
                        currentTime >= sd.ReactivateTime)
                    {
                        sd.IsActive = true;
                        sd.ReactivateTime = 0;
                        spotters[i] = sd;
                        Log.Info("Spotter reactivated after silence period");
                        EventBus?.SafePublish(new NarrativeTriggerEvent(
                            NarrativeTrigger.SpotterReactivate.ToKey()), "SpotterAggregateSystem");
                    }

                    bool countedActive = sd.IsActive
                        && (!hasIdb || !IsInternetDisabled(sd.DistrictIndex, idbBuffer));
                    bool countedActionable = sd.IsActive && !sd.IsEvacuating;

                    long buildingKey = PackBuildingId(sd.Building.Index, sd.Building.Version);
                    if (seenByBuilding.TryGetValue(buildingKey, out var existing))
                    {
                        bool keepCurrent = sd.IsCharacterSpotter && !existing.IsCharacterSpotter;
                        orphanEcb ??= m_GameSimulationEndBarrier.CreateCommandBuffer();

                        if (keepCurrent)
                        {
                            orphanEcb.Value.AddComponent<Deleted>(existing.Entity);
                            pendingDeletes.Add(existing.Entity);
                            totalCount--;
                            if (existing.CountedActive)
                                activeCount--;
                            if (existing.CountedActionable)
                                actionableCount--;

                            seenByBuilding[buildingKey] = new SpotterDedupEntry
                            {
                                Entity = entities[i],
                                IsCharacterSpotter = sd.IsCharacterSpotter,
                                CountedActive = countedActive,
                                CountedActionable = countedActionable
                            };
                            Log.Warn($"Duplicate spotter {existing.Entity.Index} removed; character spotter {entities[i].Index} kept for building {sd.Building.Index}");
                        }
                        else
                        {
                            orphanEcb.Value.AddComponent<Deleted>(entities[i]);
                            pendingDeletes.Add(entities[i]);
                            Log.Warn($"Duplicate spotter {entities[i].Index} removed for building {sd.Building.Index}");
                            continue;
                        }
                    }
                    else
                    {
                        seenByBuilding.Add(buildingKey, new SpotterDedupEntry
                        {
                            Entity = entities[i],
                            IsCharacterSpotter = sd.IsCharacterSpotter,
                            CountedActive = countedActive,
                            CountedActionable = countedActionable
                        });
                    }

                    totalCount++;

                    if (countedActive)
                        activeCount++;
                    if (countedActionable)
                        actionableCount++;
                }
            }
            seenByBuilding.Dispose();

            if (orphanEcb.HasValue)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);

            m_TotalSpotterCount = totalCount;
            m_ActionableSpotterCount = actionableCount;
            ApplyPenaltyFromCount(activeCount);
        }

        private void ApplyPenaltyFromCount(int activeCount)
        {
            var spotterCfg = BalanceConfig.Current.Spotter;
            m_ActiveSpotterCount = activeCount;
            m_RawPenalty = math.min(
                spotterCfg.MaxGlobalPenalty,
                activeCount * spotterCfg.PenaltyPerSpotter
            );

            bool isCounterOSINTActive = false;
            if (m_CountermeasuresStateQuery.TryGetSingleton<SpotterCountermeasuresState>(out var state))
            {
                isCounterOSINTActive = state.CounterOSINTActive;
            }

            m_GlobalPenalty = isCounterOSINTActive
                ? m_RawPenalty * spotterCfg.CounterOsintMultiplier
                : m_RawPenalty;
        }

        // ============================================================================
        // PHASE 2: CIVILIAN DETECTION
        // ============================================================================

        /// <summary>
        /// PERF: All early returns run before any entity iteration.
        /// Only if RNG roll passes, do a second pass with reservoir sampling to pick a target.
        /// Previous version materialized NativeList of ALL candidates every tick — now zero allocation
        /// on the common path (detection fires ~once per hour).
        /// </summary>
        private void ProcessCivilianDetection(double currentTime, NativeHashSet<Entity> pendingDeletes)
        {
            var telemarathon = m_TelemarathonQuery.TryGetSingleton<TelemarathonRuntimeState>(out var tm)
                ? tm : TelemarathonRuntimeState.Default;
            if (!telemarathon.IsActive || telemarathon.IsInShock)
                return;

            float detectionBonus = telemarathon.SpotterDetectionBonus;
            if (detectionBonus <= 0f)
                return;

            if (m_ActionableSpotterCount == 0)
                return;

            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                return;

            bool hasIdb = TryGetInternetDisabledBuffer(out var idbBuffer);
            int eligibleSpotterCount = 0;
            foreach (var (spotter, entity) in SystemAPI.Query<RefRO<SpotterData>>().WithNone<Deleted, Destroyed>().WithEntityAccess())
            {
                if (IsEligibleForCivilianDetection(spotter.ValueRO, entity, pendingDeletes, hasIdb, idbBuffer))
                    eligibleSpotterCount++;
            }

            if (eligibleSpotterCount == 0)
                return;

            float updateHours = GameRate.HoursDelta(ThrottledDeltaSeconds);
            var spotterCfg = BalanceConfig.Current.Spotter;
#pragma warning disable CIVIC247 // Probability — near-zero is valid
            // Civilian detection scales by the same eligible predicate used by reservoir sampling.
            float chanceThisUpdate = CalculateCivilianDetectionChance(
                spotterCfg.BaseDetectionChancePerHour,
                detectionBonus,
                telemarathon.EffectivenessMult,
                updateHours,
                eligibleSpotterCount);
#pragma warning restore CIVIC247

            // RNG gate — most ticks exit here with zero entity work
            if (stateRef.ValueRW.RandomState.NextFloat() >= chanceThisUpdate)
                return;

            // Reservoir sampling (k=1): pick uniformly random active spotter in single pass.
            // No allocation, no NativeList — just one Entity slot and a counter.
            Entity selected = Entity.Null;
            int targetOrdinal = stateRef.ValueRW.RandomState.NextInt(0, eligibleSpotterCount);
            int seen = 0;

            foreach (var (spotter, entity) in SystemAPI.Query<RefRO<SpotterData>>().WithNone<Deleted, Destroyed>().WithEntityAccess())
            {
                if (!IsEligibleForCivilianDetection(spotter.ValueRO, entity, pendingDeletes, hasIdb, idbBuffer))
                    continue;

                if (seen == targetOrdinal)
                {
                    selected = entity;
                    break;
                }
                seen++;
            }

            if (selected == Entity.Null) return;

            if (m_SpotterDataLookup.TryGetComponent(selected, out var detectedSpotter))
            {
                bool wasActionable = detectedSpotter.IsActive && !detectedSpotter.IsEvacuating;
                detectedSpotter.IsActive = false;
                detectedSpotter.ReactivateTime = currentTime + spotterCfg.SpotterSilenceHours;
                m_SpotterDataLookup[selected] = detectedSpotter;

                if (m_ActiveSpotterCount > 0) m_ActiveSpotterCount--;
                if (wasActionable && m_ActionableSpotterCount > 0) m_ActionableSpotterCount--;
                ApplyPenaltyFromCount(m_ActiveSpotterCount);

                Log.Info("Civilian reported a spotter! (Alarmist mode)");
                EventBus?.SafePublish(new NarrativeTriggerEvent(
                    NarrativeTrigger.SpotterCivilianReport.ToKey()), "SpotterAggregateSystem");
            }
        }

        private static bool IsEligibleForCivilianDetection(
            in SpotterData spotter,
            Entity entity,
            NativeHashSet<Entity> pendingDeletes,
            bool hasInternetDisabledBuffer,
            DynamicBuffer<InternetDisabledBuffer> internetDisabledBuffer)
        {
            return spotter.IsActive
                && !spotter.IsEvacuating
                && !pendingDeletes.Contains(entity)
                && (!hasInternetDisabledBuffer || !IsInternetDisabled(spotter.DistrictIndex, internetDisabledBuffer));
        }

        private static float CalculateCivilianDetectionChance(
            float baseDetectionChancePerHour,
            float detectionBonus,
            float effectivenessMult,
            float updateHours,
            int actionableSpotterCount)
        {
            const float MAX_DETECTION_CHANCE_PER_TICK = 0.5f;
            return Unity.Mathematics.math.min(
                MAX_DETECTION_CHANCE_PER_TICK,
                baseDetectionChancePerHour
                * (1f + detectionBonus)
                * effectivenessMult
                * updateHours
                * actionableSpotterCount);
        }

        // ============================================================================
        // PHASE 4: ECONOMY (DAILY COST)
        // ============================================================================

        private void ProcessDailyTick(double currentTime)
        {
            const double HOURS_PER_DAY = GameRate.HOURS_PER_DAY;

            if (HasPendingCounterOSINTDailyBudget())
                return;

            if (currentTime - m_LastDailyTick < HOURS_PER_DAY)
                return;

            double overdueDays = (currentTime - m_LastDailyTick) / HOURS_PER_DAY;
            if (overdueDays > 1d)
            {
                int skipped = (int)System.Math.Floor(overdueDays) - 1;
                Log.Warn($"Daily tick overdue by {overdueDays:F1} days — charging one day and skipping {skipped} backlog day(s) to prevent budget burst");
            }

            ProcessDailyCost(1, currentTime);
        }

        private void ProcessDailyCost(int days, double coveredUntil)
        {
            if (!m_CountermeasuresStateQuery.TryGetSingletonRW<SpotterCountermeasuresState>(out var stateRef))
                return;

            if (!stateRef.ValueRO.CounterOSINTActive)
                return;

            int dailyCost = BalanceConfig.Current.Spotter.CounterOsintDailyCost;
            int totalCost = dailyCost * days;

            if (!CanAffordDailyCounterOSINT(totalCost))
            {
                EnqueueCommand(new SpotterCommand
                {
                    Type = SpotterCommandType.DisableCounterOSINT,
                    NarrativeHint = NarrativeTrigger.CounterOsintCancel,
                    HasNarrativeHint = true
                });
                Log.Warn("Counter-OSINT daily cost budget failed — disable queued");
                return;
            }

            var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
            var budgetEntity = ecb.QueuePendingOperation(new SpotterBudgetIntent
            {
                Action = AirDefenseActionType.CounterOSINTDailyCost,
                Target = EntityRef.FromEntity(Entity.Null),
                Cost = totalCost,
                Days = days,
                CoveredUntilGameHour = coveredUntil,
                RefundOperationKey = new FixedString128Bytes(RefundOperationKey(AirDefenseActionType.CounterOSINTDailyCost, days, totalCost, coveredUntil))
            });
            bool queued = BudgetEmitter.TryQueueDeductOnEntity(
                World,
                ecb,
                budgetEntity,
                totalCost,
                BudgetCategory.SpotterOps,
                BudgetPriority.DailyCost,
                "Spotter.CounterOSINTDailyCost",
                out _,
                BudgetResultMode.RetainResult);

            if (!queued)
            {
                ecb.DestroyEntity(budgetEntity);
                EnqueueCommand(new SpotterCommand
                {
                    Type = SpotterCommandType.DisableCounterOSINT,
                    NarrativeHint = NarrativeTrigger.CounterOsintCancel,
                    HasNarrativeHint = true
                });
                Log.Warn("Counter-OSINT daily cost budget failed — disable queued");
                return;
            }

            m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            if (Log.IsDebugEnabled) Log.Debug($"Queued Counter-OSINT daily cost: ${totalCost} ({days} day(s))");
        }

        private bool HasPendingCounterOSINTDailyBudget()
        {
            foreach (var intent in SystemAPI.Query<RefRO<SpotterBudgetIntent>>()
                .WithAll<BudgetDeductRequest>())
            {
                var value = intent.ValueRO;
                if (value.Action == AirDefenseActionType.CounterOSINTDailyCost
                    && !value.DomainApplied
                    && !value.DomainRejected
                    && !value.ChargeFailed)
                    return true;
            }
            return false;
        }

        private static string RefundOperationKey(AirDefenseActionType action, int days, int totalCost, double coveredUntil)
            => $"SpotterRefund:Daily:{(int)action}:{days}:{totalCost}:{coveredUntil:R}";

        private bool CanAffordDailyCounterOSINT(long totalCost)
        {
            return totalCost > 0 && CityBudgetService.CanAffordWithPending(World, totalCost);
        }

        // ============================================================================
        // PHASE 5: STATS SINGLETON
        // ============================================================================

        private void UpdateStatsSingleton()
        {
            bool hasCmState = m_CountermeasuresStateQuery.TryGetSingleton<SpotterCountermeasuresState>(out var cmState);

            if (SystemAPI.TryGetSingletonRW<SpotterStatsSingleton>(out var singleton))
            {
                ref var s = ref singleton.ValueRW;
                // PERF: Use cached count from lifecycle pass instead of CalculateEntityCount (sync point)
                s.TotalCount = m_TotalSpotterCount;
                s.ActiveCount = m_ActiveSpotterCount;
                s.ActionableCount = m_ActionableSpotterCount;

                // Inline GetSBUCost (5 lines)
                var cfg = BalanceConfig.Current.Spotter;
                int totalVisits = hasCmState ? cmState.TotalSBUVisits : 0;
#pragma warning disable CIVIC067 // Intentional step function
                s.SBUCost = math.min(cfg.SbuMaxCost, cfg.SbuBaseCost + (totalVisits / 5) * cfg.SbuCostIncrement);
#pragma warning restore CIVIC067

                if (hasCmState)
                {
                    s.TotalSBUVisits = cmState.TotalSBUVisits;
                    s.TotalEvacuations = cmState.TotalEvacuations;
                }
                s.EvacuationCost = cfg.EvacuationCost;
                s.CounterOSINTDailyCost = cfg.CounterOsintDailyCost;
            }

            if (SystemAPI.TryGetSingletonRW<SpotterPenaltyState>(out var penaltySingleton))
            {
                ref var p = ref penaltySingleton.ValueRW;
                p.RawPenalty = m_RawPenalty;
                p.GlobalPenalty = m_GlobalPenalty;
                p.IsCounterOSINTActive = hasCmState && cmState.CounterOSINTActive;
            }
        }

        // ============================================================================
        // HELPERS
        // ============================================================================

        private bool TryGetInternetDisabledBuffer(out DynamicBuffer<InternetDisabledBuffer> buffer)
        {
            buffer = default;
            if (!m_CountermeasuresStateQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var se))
                return false;
            return m_InternetDisabledBufferLookup.TryGetBuffer(se, out buffer);
        }

        private static bool IsInternetDisabled(int districtIndex, DynamicBuffer<InternetDisabledBuffer> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].DistrictIndex == districtIndex)
                    return true;
            }
            return false;
        }

        private bool HasMatchingEvacuationBudgetRequest(Entity target)
        {
            foreach (var intent in SystemAPI.Query<RefRO<SpotterBudgetIntent>>().WithAll<BudgetDeductRequest>())
            {
                var value = intent.ValueRO;
                if (value.Action == AirDefenseActionType.PerformEvacuation
                    && value.Target.Index == target.Index
                    && value.Target.Version == target.Version)
                    return true;
            }
            return false;
        }

        private static long PackBuildingId(int index, int version)
        {
            return ((long)index << 32) | (uint)version;
        }

        // ============================================================================
        // LIFECYCLE
        // ============================================================================

        public void ResetState()
        {
            m_LastDailyTick = 0;
            ResetTransientRuntimeState();
            Log.Info("State reset");
        }

        protected override void OnDestroy()
        {
            if (m_CommandQueue.IsCreated) m_CommandQueue.Dispose();
            base.OnDestroy();
            Log.Info("Destroyed");
        }
    }
}
