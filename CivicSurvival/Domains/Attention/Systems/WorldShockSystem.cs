using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Debug;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Attention.Data;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Attention.Systems
{
    /// <summary>
    /// Coordinator for World Shock — the "Attention Economy".
    ///
    /// Responsibilities:
    /// - State initialization and serialization
    /// - Tier calculation and narrative events
    /// - UI singleton updates
    /// - Public API
    ///
    /// SINGLE WRITER for WorldShockState. Reads deltas from:
    /// - WorldShockReactionSystem: casualties, destroyed buildings (event-driven, read-only)
    /// - WorldShockDecaySystem: shock decay over time (throttled, read-only)
    ///
    /// Performance: ~0.01ms (apply deltas + tier check + singleton update).
    ///
    /// S14b-2 ACCEPTED: No act guard needed — shock is event-driven (casualties/destroyed buildings);
    /// during PreWar no attacks occur, so shock stays at 0.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(ShockStateSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.None)]
    public partial class WorldShockSystem : CivicSystemBase, IResettable, IPostLoadValidation, ICivicSingletonOwner<ShockStateSingleton>
#if DEBUG
        , IShockDebugMutator
#endif
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("WorldShockSystem");

        private EntityQuery m_StateQuery;
        [System.NonSerialized]
        private CivicSingletonHandle<WorldShockState> m_State;
        private EntityQuery m_ShockSingletonQuery;
        private ComponentLookup<ShockStateSingleton> m_ShockSingletonLookup;
        private AidTier m_LastTier;

        // Subsystem references (read deltas, guaranteed RegisterBefore])
        private WorldShockDecaySystem m_DecaySystem = null!;
        private WorldShockReactionSystem m_ReactionSystem = null!;

        // CRIT-C1 root fix: apply reaction deltas exactly once per production epoch. The reaction
        // system is RequireAnyForUpdate-gated, so on idle frames it does not run; without this guard
        // we would re-apply its last deltas every frame (counters double, shock races to 100%).
        // Transient producer/consumer cursor: WorldShockReactionSystem.ProducedEpoch is a runtime
        // auto-property (not persisted), so this cursor must not be serialized — it is realigned to
        // the producer epoch in ResetState to avoid re-applying already-applied reaction deltas.
        [System.NonSerialized] private uint m_LastReactionEpoch;

        // TS-015 FIX: Queue shock additions to avoid read-modify-write race
        private float m_PendingShock;
        private string m_PendingShockSource = null!;

        // T1-6 FIX: 7-day rolling window for weekly stats
        private const int ROLLING_WINDOW_DAYS = 7;
        private bool m_DayChanged;
        internal int[] m_DailyCasualties = new int[ROLLING_WINDOW_DAYS];
        internal int[] m_DailyBuildings = new int[ROLLING_WINDOW_DAYS];
        internal int[] m_DailyCritical = new int[ROLLING_WINDOW_DAYS];
        internal int m_RingIndex;
        // Delta tracking: previous TotalX at start of current day
        internal long m_PrevTotalCasualties;
        internal long m_PrevTotalBuildings;
        internal long m_PrevTotalCritical;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Domain-Driven Initialization
            ShockStateSingleton.EnsureExists(EntityManager);

            m_StateQuery = GetEntityQuery(ComponentType.ReadWrite<WorldShockState>());
            m_State = CreateSingletonHandle<WorldShockState>(m_StateQuery);
            m_ShockSingletonQuery = GetEntityQuery(ComponentType.ReadWrite<ShockStateSingleton>());
            m_ShockSingletonLookup = GetComponentLookup<ShockStateSingleton>(false);

            EnsureStateEntity();

            // T1-6: Subscribe to day changes for rolling window
            if (EventBus != null)
            {
                EventBus.Subscribe<DayChangedEvent>(OnDayChanged, DayEventPriority.StateChange);

                // CIVIC243 FIX: Wire AddShock to FirstStrike scenario event
                EventBus.Subscribe<FirstStrikeCascadeEvent>(OnFirstStrikeCascade);

                // FIX M-100: Apply AttentionIncrease from import discovery event
                EventBus.Subscribe<ShadowNarrativeEvent>(OnShadowNarrative);
            }

            // Subsystem references (both RegisterBefore(WorldShockSystem)])
            m_DecaySystem = World.GetExistingSystemManaged<WorldShockDecaySystem>();
            m_ReactionSystem = World.GetExistingSystemManaged<WorldShockReactionSystem>();

            // Post-load reconciliation: sync m_LastTier + m_PrevTotal* from ECS state
#if DEBUG
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IShockDebugMutator>(this);
#endif

            Log.Info("Created — Attention Economy coordinator (single writer for WorldShockState)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            ShockStateSingleton.EnsureExists(EntityManager);
            EnsureStateEntity();
        }

        protected override void OnDestroy()
        {
            // Unsubscribe from events
            if (ServiceRegistry.IsInitialized && EventBus != null)
            {
                EventBus.Unsubscribe<DayChangedEvent>(OnDayChanged);
                EventBus.Unsubscribe<FirstStrikeCascadeEvent>(OnFirstStrikeCascade);
                EventBus.Unsubscribe<ShadowNarrativeEvent>(OnShadowNarrative);
            }

            // Cleanup state entity to prevent memory leak
            var stateEntity = m_State.Entity;
            if (stateEntity != Entity.Null && EntityManager.Exists(stateEntity))
            {
                EntityManager.DestroyEntity(stateEntity);
                m_State.Invalidate();
            }
#if DEBUG
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IShockDebugMutator>(this);
#endif
            base.OnDestroy();
        }

        protected override void OnUpdateImpl()
        {
            m_ShockSingletonLookup.Update(this);

            if (!TryGetStateEntity(out var stateEntity))
            {
                Log.Warn("WorldShockState missing during update — shock update deferred");
                return;
            }

            bool restoredSerializedState = false;

            // Apply serialized state from save
            if (m_HasSerializedState)
            {
                SystemAPI.SetComponent(stateEntity, m_SerializedState);
                m_LastTier = m_SerializedState.CurrentTier;
                m_HasSerializedState = false;
                restoredSerializedState = true;
                Log.Info($"Restored saved state: Shock={m_SerializedState.ShockLevel:F1}%");
            }

            // SINGLE WRITER: Read state, apply all deltas, write once at end
            var state = SystemAPI.GetComponent<WorldShockState>(stateEntity);

            // Apply decay delta (from WorldShockDecaySystem, guaranteed RegisterBefore])
            if (m_DecaySystem != null && !restoredSerializedState)
            {
                if (m_DecaySystem.DecayDelta < 0f)
                {
                    state.ShockLevel = math.max(0f, state.ShockLevel + m_DecaySystem.DecayDelta);
                }
                if (m_DecaySystem.AdvanceLastUpdateTime > 0.0)
                {
                    state.LastUpdateTime = m_DecaySystem.AdvanceLastUpdateTime;
                }
            }

            // Apply reaction deltas (from WorldShockReactionSystem, guaranteed RegisterBefore]).
            // ONLY when the reaction system produced a new epoch this run — its RequireAnyForUpdate
            // gate means it does not run on idle frames, and re-applying its stale deltas every frame
            // is exactly CRIT-C1 (counters double, shock races to 100%).
            if (m_ReactionSystem != null && m_ReactionSystem.ProducedEpoch != m_LastReactionEpoch)
            {
                // Accumulate counters once per epoch — even if ShockGain==0 (e.g. ShockPerBuilding config is 0)
                state.TotalCasualties += m_ReactionSystem.Casualties;
                state.TotalBuildingsDestroyed += m_ReactionSystem.BuildingsDestroyed;
                state.TotalCivilianBuildingsDestroyed += m_ReactionSystem.CivilianBuildingsDestroyed;
                state.TotalCriticalHits += m_ReactionSystem.CriticalHits;

                if (m_ReactionSystem.ShockGain > 0f)
                {
                    state.ShockLevel = math.min(100f, state.ShockLevel + m_ReactionSystem.ShockGain);
                }
                if (m_ReactionSystem.CriticalHits > 0 && EventBus != null)
                {
                    EventBus.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.ShockCriticalInfra.ToKey()));
                }
                if (m_ReactionSystem.LastTragedyTime > 0.0)
                {
                    state.LastTragedyTime = m_ReactionSystem.LastTragedyTime;
                }

                m_LastReactionEpoch = m_ReactionSystem.ProducedEpoch;
            }

            // TS-015 FIX: Apply pending shock from public API calls
            if (m_PendingShock > 0f)
            {
                float oldShock = state.ShockLevel;
                state.ShockLevel = math.min(100f, state.ShockLevel + m_PendingShock);
                var timeProvider = GameTimeSystem.Instance;
                if (timeProvider != null)
                {
                    state.LastTragedyTime = timeProvider.Current.TotalGameHours;
                }
                Log.Info($"+{m_PendingShock:F1}% ({m_PendingShockSource}). " +
                    $"Total: {oldShock:F1}% -> {state.ShockLevel:F1}%");
                m_PendingShock = 0f;
                m_PendingShockSource = null!;
            }

            // T1-6: Track today's delta BEFORE ring advance (captures day-change-frame events in old slot)
            UpdateDailyTracking(state);

            // T1-6: Advance ring buffer on day change (flag set by DayChangedEvent handler)
            if (m_DayChanged)
            {
                m_DayChanged = false;
                AdvanceRingBuffer(state);
            }

            // T1-6: Compute Past 7 Days from ring buffer → overwrite ThisWeek fields
            state.CasualtiesThisWeek = SumRingBuffer(m_DailyCasualties);
            state.BuildingsDestroyedThisWeek = SumRingBuffer(m_DailyBuildings);
            state.CriticalHitsThisWeek = SumRingBuffer(m_DailyCritical);

            // Update tier
            state.CurrentTier = CalculateTier(state.ShockLevel);

            if (m_LastTier != state.CurrentTier)
            {
                OnTierChanged(m_LastTier, state.CurrentTier, state.ShockLevel,
                    state.TotalCasualties, state.TotalBuildingsDestroyed);
                m_LastTier = state.CurrentTier;
            }

            // Save state (tier may have changed)
            SystemAPI.SetComponent(stateEntity, state);

            // Update UI singleton
            UpdateShockSingleton(state);
        }

        private void UpdateShockSingleton(WorldShockState state)
        {
            m_ShockSingletonLookup.Update(this);
            if (!m_ShockSingletonQuery.TryGetSingletonEntity<ShockStateSingleton>(out var entity))
            {
                Log.Warn("ShockStateSingleton missing during update — UI shock publish skipped");
                return;
            }

            m_ShockSingletonLookup[entity] = new ShockStateSingleton
            {
                ShockLevel = state.ShockLevel,
                CurrentTier = state.CurrentTier,
                CasualtiesThisWeek = state.CasualtiesThisWeek,
                BuildingsDestroyedThisWeek = state.BuildingsDestroyedThisWeek,
                CriticalHitsThisWeek = state.CriticalHitsThisWeek,
                TotalCasualties = state.TotalCasualties,
                TotalBuildingsDestroyed = state.TotalBuildingsDestroyed,
                TotalCivilianBuildingsDestroyed = state.TotalCivilianBuildingsDestroyed,
                TotalCriticalHits = state.TotalCriticalHits
            };
        }

        private Entity EnsureStateEntity()
        {
            return EnsureSingletonFast(ref m_State, WorldShockStateDefault());
        }

        private bool TryGetStateEntity(out Entity entity)
        {
            entity = m_State.Entity;
            if (entity != Entity.Null && SystemAPI.HasComponent<WorldShockState>(entity))
                return true;

            return m_StateQuery.TryGetSingletonEntity<WorldShockState>(out entity);
        }

        private static WorldShockState WorldShockStateDefault()
        {
            return new WorldShockState
            {
                ShockLevel = 0f,
                CurrentTier = AidTier.DeepConcern,
                LastUpdateTime = 0.0,
                DecayPerDay = BalanceConfig.Current.Attention.DecayPerDay,
                CasualtiesThisWeek = 0,
                BuildingsDestroyedThisWeek = 0,
                CriticalHitsThisWeek = 0,
                LastTragedyTime = 0,
                TotalCasualties = 0,
                TotalBuildingsDestroyed = 0,
                TotalCivilianBuildingsDestroyed = 0,
                TotalCriticalHits = 0
            };
        }

        private AidTier CalculateTier(float shockLevel)
        {
            var attnCfg = BalanceConfig.Current.Attention;
            if (shockLevel >= attnCfg.TierGlobalShock)
                return AidTier.GlobalShock;
            if (shockLevel >= attnCfg.TierHeadlines)
                return AidTier.Headlines;
            return AidTier.DeepConcern;
        }

        private void OnTierChanged(AidTier oldTier, AidTier newTier, float shockLevel,
            long totalCasualties, long totalBuildingsDestroyed)
        {
            Log.Info($"Aid tier changed: {oldTier} -> {newTier} (shock: {shockLevel:F1}%)");

            if (EventBus == null)
            {
                Log.Warn("EventBus not ready - event not published");
                return;
            }

            // Telemetry: emit tier transition with shock level and cumulative counters
            EventBus.SafePublish(new ShockTierChangedEvent(
                oldTier, newTier, shockLevel, totalCasualties, totalBuildingsDestroyed));

            switch (newTier)
            {
                case AidTier.GlobalShock:
                    // M3 DOC: Order matters - Breaking shows first (urgent), then UN response (diplomatic)
                    EventBus.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.ShockGlobalBreaking.ToKey()));
                    EventBus.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.ShockGlobalUn.ToKey()));
                    break;

                case AidTier.Headlines:
                    EventBus.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.ShockHeadlines.ToKey()));
                    break;

                case AidTier.DeepConcern:
                    if (oldTier != AidTier.DeepConcern)
                    {
#pragma warning disable CIVIC242 // Multi-publisher by design — each system publishes distinct NarrativeTrigger keys
                        EventBus.SafePublish(new NarrativeTriggerEvent(NarrativeTrigger.ShockStabilizing.ToKey()));
#pragma warning restore CIVIC242
                    }
                    break;

                default:
                    Log.Warn($"Unhandled AidTier: {newTier}");
                    break;
            }
        }

        // ============ T1-6: Rolling 7-Day Window ============

#pragma warning disable CIVIC235 // Stores flag only — no-op when disabled (OnUpdateImpl checks)
        private void OnDayChanged(DayChangedEvent evt)
        {
            m_DayChanged = true;
        }
#pragma warning restore CIVIC235

        /// <summary>
        /// CIVIC243 FIX: Inject shock on First Strike for immediate attention spike.
        /// Design: "WorldShockSystem.AddShock(100) for First Strike"
        /// </summary>
#pragma warning disable CIVIC235 // One-time event: must inject shock regardless of system state
        private void OnFirstStrikeCascade(FirstStrikeCascadeEvent evt)
        {
            AddShock(BalanceConfig.Current.Attention.ShockFirstStrike, "FirstStrike");
#pragma warning restore CIVIC235
        }

        /// <summary>
        /// Advance ring buffer to next day slot.
        /// Called from OnUpdate when m_DayChanged flag is set.
        /// </summary>
        private void AdvanceRingBuffer(WorldShockState state)
        {
            // Snapshot current totals as "start of new day" baseline
            m_PrevTotalCasualties = state.TotalCasualties;
            m_PrevTotalBuildings = state.TotalBuildingsDestroyed;
            m_PrevTotalCritical = state.TotalCriticalHits;

            // Advance to next slot (clears data from 7 days ago)
            m_RingIndex = (m_RingIndex + 1) % ROLLING_WINDOW_DAYS;
            m_DailyCasualties[m_RingIndex] = 0;
            m_DailyBuildings[m_RingIndex] = 0;
            m_DailyCritical[m_RingIndex] = 0;

            Log.Info($"Day changed — ring slot {m_RingIndex}, totals: C={state.TotalCasualties} B={state.TotalBuildingsDestroyed}");
        }

        /// <summary>
        /// Update today's ring buffer slot with delta from TotalX fields.
        /// </summary>
        private void UpdateDailyTracking(WorldShockState state)
        {
            long deltaCasualtiesLong = state.TotalCasualties - m_PrevTotalCasualties;
            long deltaBuildingsLong = state.TotalBuildingsDestroyed - m_PrevTotalBuildings;
            long deltaCriticalLong = state.TotalCriticalHits - m_PrevTotalCritical;

            int deltaCasualties = (int)math.clamp(deltaCasualtiesLong, int.MinValue, int.MaxValue);
            int deltaBuildings = (int)math.clamp(deltaBuildingsLong, int.MinValue, int.MaxValue);
            int deltaCritical = (int)math.clamp(deltaCriticalLong, int.MinValue, int.MaxValue);

            m_DailyCasualties[m_RingIndex] = deltaCasualties;
            m_DailyBuildings[m_RingIndex] = deltaBuildings;
            m_DailyCritical[m_RingIndex] = deltaCritical;
        }

        private static int SumRingBuffer(int[] buffer)
        {
            int sum = 0;
            for (int i = 0; i < ROLLING_WINDOW_DAYS; i++) sum += buffer[i];
            return sum;
        }

        // ============ Public API ============

        public WorldShockState GetCurrentState()
        {
            var stateEntity = m_State.Entity;
            if (!EntityManager.Exists(stateEntity)) return default;
            return EntityManager.GetComponentData<WorldShockState>(stateEntity);
        }

        public float GetShockLevel()
        {
            var stateEntity = m_State.Entity;
            if (!EntityManager.Exists(stateEntity)) return 0f;
            return EntityManager.GetComponentData<WorldShockState>(stateEntity).ShockLevel;
        }

        /// <summary>
        /// Queue shock addition for processing in OnUpdate.
        /// TS-015 FIX: Avoids read-modify-write race with OnUpdate.
        /// </summary>
        public void AddShock(float amount, string source)
        {
            if (amount <= 0f) return;

            // Queue for processing in OnUpdate (single writer pattern)
            m_PendingShock += amount;
            m_PendingShockSource = m_PendingShockSource == null ? source : $"{m_PendingShockSource}+{source}";
        }

#if DEBUG
        public void DebugSetShockLevel(float level, string source)
        {
            var stateEntity = EnsureStateEntity();

            var state = EntityManager.GetComponentData<WorldShockState>(stateEntity);
            var oldTier = state.CurrentTier;
            state.ShockLevel = math.clamp(level, 0f, 100f);
            state.CurrentTier = CalculateTier(state.ShockLevel);
            EntityManager.SetComponentData(stateEntity, state);

            if (oldTier != state.CurrentTier)
                OnTierChanged(oldTier, state.CurrentTier, state.ShockLevel,
                    state.TotalCasualties, state.TotalBuildingsDestroyed);

            m_LastTier = state.CurrentTier;
            UpdateShockSingleton(state);
            Log.Info($"[DEBUG] {source}: shock set to {state.ShockLevel:F1}");
        }

        public void DebugResetShock(string source)
        {
            ResetState();
            var stateEntity = EnsureStateEntity();

            var state = WorldShockStateDefault();
            EntityManager.SetComponentData(stateEntity, state);
            UpdateShockSingleton(state);
            Log.Info($"[DEBUG] {source}: shock reset");
        }
#endif

        /// <summary>
        /// Sync m_LastTier and m_PrevTotal* from authoritative ECS WorldShockState.
        /// Prevents spurious tier-change events and incorrect daily deltas on first tick after load.
        /// </summary>
        public void ValidateAfterLoad()
        {
            var stateEntity = EnsureStateEntity();

            var state = EntityManager.GetComponentData<WorldShockState>(stateEntity);

            if (m_LastTier != state.CurrentTier)
            {
                Log.Warn($"PostLoad: m_LastTier={m_LastTier} diverged from CurrentTier={state.CurrentTier} — corrected");
                m_LastTier = state.CurrentTier;
            }

            // Sync delta baselines — if system deserialization failed, these are 0
            // while ECS totals are non-zero, causing huge false deltas on first day tick
            if (m_PrevTotalCasualties != state.TotalCasualties ||
                m_PrevTotalBuildings != state.TotalBuildingsDestroyed ||
                m_PrevTotalCritical != state.TotalCriticalHits)
            {
                Log.Warn($"PostLoad: m_PrevTotal* diverged (C:{m_PrevTotalCasualties}→{state.TotalCasualties}, " +
                         $"B:{m_PrevTotalBuildings}→{state.TotalBuildingsDestroyed}, " +
                         $"Cr:{m_PrevTotalCritical}→{state.TotalCriticalHits}) — corrected");
                m_PrevTotalCasualties = state.TotalCasualties;
                m_PrevTotalBuildings = state.TotalBuildingsDestroyed;
                m_PrevTotalCritical = state.TotalCriticalHits;
            }
            else
            {
                Log.Info($"PostLoad: WorldShockSystem state consistent (tier={m_LastTier})");
            }

            // S7-04 FIX: Sync UI singleton immediately — eliminates 1-frame stale window
            m_ShockSingletonLookup.Update(this);
            UpdateShockSingleton(state);
        }

        /// <summary>
        /// FIX M-100: Apply world attention increase when shadow import is discovered.
        /// Uses AddShock queue to respect the single-writer pattern (applied in OnUpdate).
        /// </summary>
#pragma warning disable CIVIC235 // Queues m_PendingShock only — applied in OnUpdateImpl when enabled
        private void OnShadowNarrative(ShadowNarrativeEvent evt)
#pragma warning restore CIVIC235
        {
            if (evt.Type != ShadowNarrativeEventType.ImportDiscovered) return;
            if (evt.AttentionIncrease <= 0f) return;

            AddShock(evt.AttentionIncrease, "ImportDiscovered");
            Log.Info($"Import discovered — world attention +{evt.AttentionIncrease:F1}%");
        }

        public void ResetState()
        {
            m_State.Invalidate();
            m_HasSerializedState = false;
            m_SerializedState = default;

            // CIVIC229 FIX: Reset serialized tracking fields
            m_PendingShock = 0f;
            m_PendingShockSource = null!;
            m_DayChanged = false;
            m_LastTier = AidTier.DeepConcern; // CalculateTier(0f) = DeepConcern — no spurious transition on new game
            // Realign to producer epoch (0 on a fresh reaction system) so a post-reset update does not
            // re-apply the last epoch's deltas into the freshly restored shock state.
            m_LastReactionEpoch = m_ReactionSystem?.ProducedEpoch ?? 0u;
            m_RingIndex = 0;
            m_PrevTotalCasualties = 0;
            m_PrevTotalBuildings = 0;
            m_PrevTotalCritical = 0;
            for (int i = 0; i < ROLLING_WINDOW_DAYS; i++)
            {
                m_DailyCasualties[i] = 0;
                m_DailyBuildings[i] = 0;
                m_DailyCritical[i] = 0;
            }
        }
    }
}
