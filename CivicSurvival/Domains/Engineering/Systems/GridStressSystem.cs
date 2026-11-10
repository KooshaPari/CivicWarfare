using System;
using Game;
using Game.Simulation;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Logic;
using CivicSurvival.Domains.Engineering.Logic;
using System.Threading;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Power;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Grid Stress System - tracks stress accumulation during power deficit.
    ///
    /// Frequency Model:
    /// - 50.0 Hz (Green) = Normal, balance >= 0
    /// - 49.5 Hz (Yellow) = Deficit, threshold kicks in
    /// - 49.0 Hz (Red) = Collapse imminent
    /// - 48.5 Hz (Black) = COLLAPSED, full shutdown
    ///
    /// When stress reaches 100% (2 game hours of deficit), triggers Grid Collapse:
    /// - All power plants shutdown for 24 hours
    /// - City goes completely dark
    /// - Cascade failures (water, hospitals)
    ///
    /// This is the "punishment" for not using Load Shedding.
    ///
    /// S13a-3 ACCEPTED: PowerReserve concept was removed; deficit detection uses PowerGridSingleton.Balance directly.
    ///
    /// Owns CollapsedProducer sidecar lifecycle. PowerCapacityPipeline owns
    /// GridStressModifier hydration and final capacity.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(GridStressData))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.None)]
#pragma warning disable CIVIC317 // Modal reset is centralized in PostLoadValidationSystem.OnGameLoaded — GridStressSystem only TryShows in TriggerCollapse, never on load
    public partial class GridStressSystem : ThrottledSystemBase, IPostLoadValidation, ICollapseOwnerVersionReader, ICivicSingletonOwner<GridStressData>
#pragma warning restore CIVIC317
    {
        public void PurgeAfterLoad() { }

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private static readonly LogContext Log = new("GridStressSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_2500_MS;

        private EntityQuery m_StressDataQuery;
        private EntityQuery m_PowerGridQuery;
        private EntityQuery m_ScenarioQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        // ComponentLookups
        private ComponentLookup<GridStressData> m_StressDataLookup;

        // Frame-local map of CollapsedProducer entities by building (Index,Version) composite key.
        // H1: Index-only key caused orphan shadowing — old version wins TryAdd, new entity leaks.
        [NonEntityIndex] private NativeHashMap<long, Entity> m_CollapsedByBuilding;

        // Tracks the per-building collapsed identity set across ticks so the
        // snapshot revision bumps on rotation (A restored / B collapses in
        // the same tick, count unchanged). Compared via cardinality + Contains
        // against the previous tick's keys; swap on diff.
        [NonEntityIndex] private NativeHashSet<long> m_CurCollapseOwnerKeys;
        [NonEntityIndex] private NativeHashSet<long> m_PrevCollapseOwnerKeys;
#pragma warning disable CIVIC241 // Revision is ephemeral — never persisted; ResetStress/PendingResetPublish rebases it.
        [NonSerialized] private int m_CollapseOwnerRevision;
#pragma warning restore CIVIC241

        private double m_LastGameHour = -1.0;
        private GridStressZone m_LastZone = GridStressZone.Normal;

        // One-shot latch for the pre-collapse "GridCritical" modal: shown once when
        // the grid first enters Red during a stress episode, reset when the episode
        // resolves to Normal (so a fresh buildup warns again). Not persisted — after
        // load ModalCoordinator.Reset clears the slot; the always-on InfoSection
        // timer carries post-load awareness (same contract as the collapse modal).
        [NonSerialized] private bool m_CriticalModalShown;

        // M2 FIX: Cache ModSettings (avoid lookup in hot path)
        private ModSettings? m_Settings;
        [NonSerialized] private bool m_PendingCollapseOwnerResetPublish;
        // Debug commands arrive from ScenarioDebugActionSystem's context; defer execution to
        // OnThrottledUpdate so collapse query iteration binds to this system, not the caller.
        // DEBUG-only: the sole producer (ScenarioDebugActionSystem) is #if DEBUG, so the whole
        // deferred-debug-command pathway compiles away in Release.
#if DEBUG
        [NonSerialized] private bool m_DebugForceCollapsePending;
        [NonSerialized] private bool m_DebugResetStressPending;
        [NonSerialized] private bool m_DebugSetStressHoursPending;
        [NonSerialized] private float m_DebugSetStressHoursValue;
        [NonSerialized] private string m_DebugCommandSource = "";
#endif
        // ActChangedEvent fires in the publisher's context; defer the collapse reset to OnThrottledUpdate.
        [NonSerialized] private bool m_PendingActChangeReset;

        // H-05 fix: track known act to distinguish real transitions from post-load re-publish
        [NonSerialized] private Act m_KnownAct;
        private readonly VersionedView<CollapseOwnerSnapshot> m_CollapseOwnerView = new(CollapseOwnerSnapshot.Empty);
        public IVersionedView<CollapseOwnerSnapshot> CollapseOwnerView => m_CollapseOwnerView;

        protected override void OnCreate()
        {
            base.OnCreate();
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<ICollapseOwnerVersionReader>(this);
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            Log.Info("Created");

            m_PowerGridQuery = GetEntityQuery(ComponentType.ReadOnly<PowerGridSingleton>());

            // Core cross-domain singleton (Axiom 5: read-only Core type, no Scenario-domain using).
            // Drives population-scaled collapse grace (ScaledCollapseThreshold).
            m_ScenarioQuery = GetEntityQuery(ComponentType.ReadOnly<ScenarioSingleton>());

            m_StressDataQuery = GetEntityQuery(ComponentType.ReadWrite<GridStressData>());

            // ComponentLookups
            m_StressDataLookup = GetComponentLookup<GridStressData>(false);

            // Frame-local map for CollapsedProducer entities
            m_CollapsedByBuilding = new NativeHashMap<long, Entity>(64, Allocator.Persistent);
            m_CurCollapseOwnerKeys = new NativeHashSet<long>(64, Allocator.Persistent);
            m_PrevCollapseOwnerKeys = new NativeHashSet<long>(64, Allocator.Persistent);

            // Create GridStressData singleton immediately (in OnCreate, not OnStartRunning)
            // UI systems need this data available from the start
            GridStressData.EnsureExists(EntityManager);

            // S8-03: Post-load reconciliation (m_LastZone sync with GridStressData.Zone)

            SubscribeRequired<ActChangedEvent>(OnActChanged);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            GridStressData.EnsureExists(EntityManager);
        }

        /// <summary>
        /// S8-03: Sync m_LastZone from authoritative GridStressData.Zone singleton.
        /// Prevents spurious GridStressWarning or GridRecovery events on first tick after load.
        /// Order 30: after all split capacity modifier writers (OperationalDamage=10, Disaster=20, Wear=20).
        /// </summary>
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_READER;
        public void ValidateAfterLoad()
        {
            m_StressDataLookup.Update(this);
            if (!m_StressDataQuery.TryGetSingletonEntity<GridStressData>(out var stressEntity)
                || !m_StressDataLookup.HasComponent(stressEntity))
            {
                Log.Warn("S8-03: GridStressData singleton not found — skipping m_LastZone sync");
                return;
            }

            var stressData = m_StressDataLookup[stressEntity];
            var recomputed = GridZoneCalculator.CalculateZoneAndFrequency(stressData.StressPercent, stressData.IsCollapsed);
            if (stressData.Zone != recomputed.zone)
            {
                Log.Info($"S8-03: Grid zone recomputed after load ({stressData.Zone} -> {recomputed.zone})");
                stressData.Zone = recomputed.zone;
                stressData.CurrentFrequency = recomputed.frequency;
                m_StressDataLookup[stressEntity] = stressData;
            }

            if (m_LastZone != stressData.Zone)
            {
                Log.Warn($"S8-03: m_LastZone={m_LastZone} diverged from GridStressData.Zone={stressData.Zone} — corrected");
                m_LastZone = stressData.Zone;
            }
            else
            {
                Log.Info($"S8-03: m_LastZone consistent ({m_LastZone})");
            }

            // H-05 fix: sync known act from CurrentActSingleton so OnActChanged can detect re-publish
            if (SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
                m_KnownAct = actSingleton.CurrentAct;

            if (m_LastGameHour < 0.0 && stressData.StressHours > 0f)
            {
                var timeProvider = GameTimeSystem.Instance;
                if (timeProvider != null)
                {
                    m_LastGameHour = timeProvider.Current.TotalGameHours;
                    Log.Warn($"S8-03: m_LastGameHour not initialized, synced from GameTimeSystem={m_LastGameHour:F1}");
                }
            }
        }

        protected override void OnThrottledUpdate()
        {
            if (m_Settings == null) { Log.Error("[GridStressSystem] ModSettings unavailable"); return; }

            if (m_PendingCollapseOwnerResetPublish)
            {
                ForceCollapseOwnerReset();
                m_PendingCollapseOwnerResetPublish = false;
            }

            // Update lookups for current frame
            m_StressDataLookup.Update(this);

            // Deferred debug commands run in this system's context (not the caller's).
            if (ProcessDebugCommands())
                return;

            if (!m_StressDataQuery.TryGetSingletonEntity<GridStressData>(out var stressEntity))
                return;
            var stressData = m_StressDataLookup[stressEntity];

            int collapseOwnerCount = RebuildCollapseOwnerMap();
            BumpCollapseOwnerRevisionIfRotated();
            ObserveCollapseOwnerVersion(collapseOwnerCount);

            // FIX #243: Lazy ECB creation — only allocate when structural changes needed
            EntityCommandBuffer? ecb = null;
            EntityCommandBuffer GetEcb() => ecb ??= m_GameSimulationEndBarrier.CreateCommandBuffer();

            // Check if enabled
            bool isEnabled = m_Settings.GridStressEnabled;
            if (!isEnabled)
            {
                ResetStress();
                ForceCollapseOwnerReset();
                return;
            }

            // Get current game time
            if (!TryGetGameHour(out var gameHour))
                return;
            float deltaHours = CalculateDeltaHours(gameHour);

            // Deficit detection reads PowerGridSingleton.RawBalance (kW): delivered production flow
            // (Σ ElectricityProducer.m_LastProduction) minus active scheduled load. RawBalance < 0 is
            // the only signal that converges under load shedding — when a district is shed, active
            // Consumption drops with it, so RawBalance climbs back toward 0 and RECOVERY can fire.
            // Demand (wanted) does NOT react to shedding, so "Production < Demand" stays true forever
            // after the first shed and pins the grid in permanent deficit. vanilla fulfilledConsumption
            // is also unusable: BlackoutJob.cs:110/118 force-zeros it for shed buildings, so it is
            // polluted by our own shedding (death spiral). RawBalance is the pre-regression working
            // semantic (commit 4212a3e44, which used Balance); regression a0efe1d79 replaced it with
            // snapshot.DispatchableMW (plant CAPACITY). We use RawBalance (Production − active load)
            // rather than Balance so the grid loop is pure-physical and shadow export is decoupled
            // into an economy/morale layer; at the deficit boundary the two are identical
            // (shadow export VOLUME is capped by the capacity headroom via PowerHeadroomMath, while
            // its Balance DRAIN stays clamped to the flow surplus — Balance and RawBalance both
            // remain flow-physical). The all-plants-gone + all-shed sub-case inverts correctly here:
            // RawBalance = 0 − 0 = 0 → isDeficit false (nothing is starving, stress should decay);
            // all-plants-gone + load NOT shed → RawBalance = 0 − activeLoad < 0 → isDeficit true.
            // Caveat: only a full district shed lifts active consumption unconditionally; the first
            // PANIC step (Q1) drops a district only during its cyclic schedule-OFF hours, so a single
            // Q1 step oscillates with the schedule rather than raising the balance steadily.
            //
            // Dead zone: vanilla generation is demand-following (m_LastProduction chases active load),
            // so RawBalance hovers around zero by construction and sits a hair NEGATIVE whenever load
            // is growing (production lags consumption by a tick). A bare `balance < 0` integrates that
            // −0.01..−0.06% noise into permanent stress and drives a city with a 7× capacity reserve
            // into the Red zone (session 2026-06-11). Honest shortfalls (a downed plant, saturation
            // inertia, fuel starvation) are 5%+ of active load — orders of magnitude past the zone.
            // The zone scales with Consumption, so a shed (which lowers active load) also narrows it:
            // shed convergence and the all-plants-gone cases above keep their semantics.
            int balance = 0;
            int activeLoadKW = 0;
#pragma warning disable CIVIC070 // Power data changes gradually; 1-frame lag invisible for stress calc
            if (m_PowerGridQuery.TryGetSingleton<PowerGridSingleton>(out var singleton))
#pragma warning restore CIVIC070
            {
                balance = singleton.RawBalance;  // physical: Production(flow) − active load. Export excluded.
                activeLoadKW = singleton.Consumption;
            }
            var gsCfg = GetGridStressConfig();
            // Deficit decision is the shared rule (GridStressMath); runtime feeds its own input —
            // vanilla RawBalance (kW) as the signed balance, Consumption (kW) as the active load.
            // The forecast feeds productionFull−active (MW) into the same rule (different unit, one rule).
            float deadZoneKW = GridStressMath.DeadZone(gsCfg.DeficitDeadZoneMinKW, gsCfg.DeficitDeadZoneFraction, activeLoadKW);
            bool isDeficit = GridStressMath.IsDeficit(balance, gsCfg.DeficitDeadZoneMinKW, gsCfg.DeficitDeadZoneFraction, activeLoadKW);

            if (Log.IsDebugEnabled)
                Log.Debug($"[GridStress] balance={balance}kW deadZone={deadZoneKW:F0}kW isDeficit={isDeficit} stressHours={stressData.StressHours:F2} stressPct={stressData.StressPercent:P0} collapsed={stressData.IsCollapsed}");

            using (Core.Utils.PerformanceProfiler.Measure("GridStress.OnUpdate"))
            {
                // FIX S5-03: Sync cached threshold from live config (prevents UI StressPercent desync after hot-reload).
                // Population-scaled grace: cache the SCALED threshold so it is the single source for both the
                // collapse check (UpdateStressAccumulation reads data.CollapseThresholdHours) and UI StressPercent.
                int populationPeak = m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var scenarioSingleton)
                    ? scenarioSingleton.PopulationPeak
                    : 0;
                stressData.CollapseThresholdHours = GridStressLogic.ScaledCollapseThreshold(GetGridStressConfig(), populationPeak);

                // Handle collapsed state — ECB only allocated on actual state transition
                if (stressData.IsCollapsed)
                {
                    if (UpdateCollapseRecovery(ref stressData, deltaHours))
                        RestoreAllProducers(GetEcb());
                }
                else
                {
                    if (UpdateStressAccumulation(ref stressData, isDeficit, deltaHours))
                        TriggerCollapse(ref stressData, GetEcb());
                }

                // Update zone based on stress
                UpdateZone(ref stressData);

                // Publish events on zone change
                if (stressData.Zone != m_LastZone)
                {
                    OnZoneChanged(m_LastZone, stressData.Zone, stressData.StressPercent);
                    m_LastZone = stressData.Zone;
                }

                // Write back via ComponentLookup (not EntityManager in OnUpdate)
                m_StressDataLookup[stressEntity] = stressData;
                m_LastGameHour = gameHour;
            }

            if (ecb.HasValue)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        /// <returns>true if collapse should be triggered (caller must call TriggerCollapse)</returns>
        private bool UpdateStressAccumulation(ref GridStressData data, bool isDeficit, float deltaHours)
        {
            var gs = GetGridStressConfig();
            // Use data.CollapseThresholdHours (population-scaled, cached in OnThrottledUpdate),
            // not gs.CollapseThresholdHours — collapse must fire on the same value the UI shows.
            data.StressHours = GridStressLogic.StepStressHours(
                data.StressHours, data.CollapseThresholdHours, isDeficit,
                deltaHours, gs.StressDecayRate, out bool collapsed);
            return collapsed;
        }

        private void TriggerCollapse(ref GridStressData data, EntityCommandBuffer ecb)
        {
            Log.Warn("GRID COLLAPSE! All power plants shutting down for emergency recovery.");

            var gs = GetGridStressConfig();
            data.IsCollapsed = true;
            data.RecoveryHoursRemaining = gs.RecoveryDurationHours;
            // Zone/Frequency set by UpdateZone() after this call (single source of truth)

            // Disable all power plants
            DisableAllProducers(ecb);

            // Publish collapse event (lazy-cached via CivicSystemBase)
            EventBus?.SafePublish(new InfraEvent(InfraEventType.GridCollapse), "GridStressSystem");

            // Surface the collapse modal (what happened / recovery / what to do).
            // ModalCoordinator is a Core slot service (Engineering→Core is allowed) and
            // works regardless of Narrative being gated — unlike the toast/news/chirp,
            // which route through InfraNarrativeResolver (Narrative feature). TryShow is
            // idempotent and fires once per collapse episode (this runs once per entry).
#pragma warning disable CIVIC239 // Modal slot is best-effort; lower-priority modal queues, higher keeps slot
#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
            ModalCoordinator.Instance.TryShow("GridCollapse");
            // Collapse supersedes the pre-collapse warning: clear GridCritical from the
            // slot/queue so it does not re-pop after the player dismisses GridCollapse.
            ModalCoordinator.Instance.Dismiss("GridCritical");
#pragma warning restore CIVIC098
#pragma warning restore CIVIC239
        }

#if DEBUG
        public bool DebugForceCollapse(string source)
        {
            m_DebugForceCollapsePending = true;
            m_DebugCommandSource = source;
            ForceNextUpdate();
            return true;
        }

        public bool DebugResetStress(string source)
        {
            m_DebugResetStressPending = true;
            m_DebugCommandSource = source;
            ForceNextUpdate();
            return true;
        }

        public bool DebugSetStressHours(float hours, string source)
        {
            m_DebugSetStressHoursPending = true;
            m_DebugSetStressHoursValue = hours;
            m_DebugCommandSource = source;
            ForceNextUpdate();
            return true;
        }
#endif

        /// <summary>
        /// Executes deferred debug commands inside this system's update context. Returns true when a
        /// command ran so the normal stress simulation is skipped for that tick (debug forces state).
        /// Bodies mirror the former synchronous debug methods verbatim, only gated by pending flags.
        /// </summary>
        private bool ProcessDebugCommands()
        {
            bool processed = false;

            if (m_PendingActChangeReset)
            {
                m_PendingActChangeReset = false;
                ResetStress();
                m_PendingCollapseOwnerResetPublish = true;
                Log.Info($"[GridStress] Collapse reset on act change to {m_KnownAct}");
                processed = true;
            }

#if DEBUG
            if (m_DebugResetStressPending)
            {
                m_DebugResetStressPending = false;
                ResetStress();
                ForceCollapseOwnerReset();
                Log.Info($"[DEBUG] Reset grid stress via {m_DebugCommandSource}");
                processed = true;
            }

            if (m_DebugForceCollapsePending
                && m_StressDataQuery.TryGetSingletonEntity<GridStressData>(out var fcEntity))
            {
                m_DebugForceCollapsePending = false;
                var stressData = m_StressDataLookup[fcEntity];
                if (!stressData.IsCollapsed)
                {
                    RebuildCollapseOwnerMap();
                    var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    TriggerCollapse(ref stressData, ecb);
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
                }
                else
                {
                    stressData.RecoveryHoursRemaining = GetGridStressConfig().RecoveryDurationHours;
                }

                stressData.StressHours = stressData.CollapseThresholdHours;
                UpdateZone(ref stressData);
                m_LastZone = stressData.Zone;
                m_StressDataLookup[fcEntity] = stressData;
                Log.Info($"[DEBUG] Forced grid collapse via {m_DebugCommandSource}");
                processed = true;
            }

            if (m_DebugSetStressHoursPending
                && m_StressDataQuery.TryGetSingletonEntity<GridStressData>(out var ssEntity))
            {
                m_DebugSetStressHoursPending = false;
                var stressData = m_StressDataLookup[ssEntity];
                stressData.StressHours = math.max(0f, m_DebugSetStressHoursValue);
                UpdateZone(ref stressData);
                m_LastZone = stressData.Zone;
                m_StressDataLookup[ssEntity] = stressData;
                Log.Info($"[DEBUG] Set grid stress hours to {stressData.StressHours:F2} via {m_DebugCommandSource}");
                processed = true;
            }
#endif

            return processed;
        }

        /// <returns>true if recovery is complete (caller must call RestoreAllProducers)</returns>
        private bool UpdateCollapseRecovery(ref GridStressData data, float deltaHours)
        {
            data.RecoveryHoursRemaining -= deltaHours;

            if (data.RecoveryHoursRemaining <= 0f)
            {
                // Recovery complete
                Log.Info("Grid recovery complete. Power plants restarting.");

                // Grace period: negative stress means player has buffer time before re-collapse.
                // Prevents death spiral when damaged capacity < demand after recovery.
                const float POST_RECOVERY_GRACE_HOURS = 1f;
                data.IsCollapsed = false;
                data.StressHours = -POST_RECOVERY_GRACE_HOURS;
                data.RecoveryHoursRemaining = 0f;
                // Zone/Frequency set by UpdateZone() after this call (single source of truth)

                // Publish recovery event (lazy-cached via CivicSystemBase)
                EventBus?.SafePublish(new InfraEvent(InfraEventType.GridRecovery), "GridStressSystem");
                return true;
            }
            return false;
        }

        private void UpdateZone(ref GridStressData data)
        {
            // Delegate to pure logic calculator
            (data.Zone, data.CurrentFrequency) = GridZoneCalculator.CalculateZoneAndFrequency(
                data.StressPercent,
                data.IsCollapsed);
        }

        private void OnZoneChanged(GridStressZone oldZone, GridStressZone newZone, float stressPercent)
        {
            Log.Info($"Grid zone changed: {oldZone} -> {newZone} (stress: {stressPercent:P0})");

            // EventBus lazy-cached via CivicSystemBase
            if (newZone == GridStressZone.Yellow && oldZone == GridStressZone.Normal)
            {
                EventBus?.SafePublish(new InfraEvent(InfraEventType.GridStressWarning, StressPercent: stressPercent, StressZone: GridStressZone.Yellow), "GridStressSystem");
            }
            else if (newZone == GridStressZone.Red)
            {
                EventBus?.SafePublish(new InfraEvent(InfraEventType.GridStressWarning, StressPercent: stressPercent, StressZone: GridStressZone.Red), "GridStressSystem");

                // Surface the pre-collapse warning modal once per stress episode.
                // ModalCoordinator is a Core slot service (Engineering→Core allowed) and
                // works regardless of Narrative gating — same route as the collapse modal.
                // Runs inside this GameSimulation tick (unpaused), so the show is pause-safe.
                if (!m_CriticalModalShown)
                {
                    m_CriticalModalShown = true;
#pragma warning disable CIVIC239 // Modal slot is best-effort; lower-priority modal queues, higher keeps slot
#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
                    ModalCoordinator.Instance.TryShow("GridCritical");
#pragma warning restore CIVIC098
#pragma warning restore CIVIC239
                }
            }
            else if (newZone == GridStressZone.Yellow && (oldZone == GridStressZone.Red || oldZone == GridStressZone.Collapsed))
            {
                // De-escalation feedback — player partially fixed deficit
                EventBus?.SafePublish(new InfraEvent(InfraEventType.GridStressWarning, StressPercent: stressPercent, StressZone: GridStressZone.Yellow), "GridStressSystem");
            }
            else if (newZone == GridStressZone.Normal && (oldZone == GridStressZone.Yellow || oldZone == GridStressZone.Red || oldZone == GridStressZone.Collapsed))
            {
                // Stress resolved — reset zone tracking so next Yellow entry triggers a fresh warning
                EventBus?.SafePublish(new InfraEvent(InfraEventType.GridStressWarning, StressPercent: stressPercent, StressZone: GridStressZone.Normal), "GridStressSystem");
                // Episode over — re-arm the critical warning for the next stress buildup.
                m_CriticalModalShown = false;
            }
        }

        private void DisableAllProducers(EntityCommandBuffer ecb)
        {
            int disabledCount = 0;

            // H3: Build set of buildings already disabled by disaster (mod entities, not on building)
            // DisableAllProducers runs only during grid collapse — inline set is fine.
            // Composite key (Index<<32|Version) to avoid entity slot recycling false matches.
            using var disasterDisabled = new NativeHashSet<long>(16, Allocator.Temp);
            foreach (var disaster in SystemAPI.Query<RefRO<DisabledByDisaster>>().WithNone<Deleted>())
            {
                if (disaster.ValueRO.CreatedHour > 0.0
                    && disaster.ValueRO.RepairedThroughHour >= disaster.ValueRO.CreatedHour)
                    continue;

                disasterDisabled.Add(((long)disaster.ValueRO.Building.Index << 32) | (uint)disaster.ValueRO.Building.Version);
            }
            using var underConstruction = new NativeHashSet<long>(16, Allocator.Temp);
            foreach (var construction in SystemAPI.Query<RefRO<UnderConstruction>>().WithNone<Deleted>())
            {
                underConstruction.Add(((long)construction.ValueRO.Building.Index << 32) | (uint)construction.ValueRO.Building.Version);
            }

            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<PlantBaseCapacity>>()
                .WithAll<Building, ElectricityProducer>()
                .WithNone<OutsideConnection, Deleted>()
                .WithEntityAccess())
            {
                // Skip plants already disabled (under construction, already collapsed, or disaster-disabled)
                // Note: plants under repair are NOT skipped — they need CollapsedProducer so that
                // completing repair during collapse doesn't restore capacity prematurely.
                long entityKey = ((long)entity.Index << 32) | (uint)entity.Version;
                bool isUnderConstruction = underConstruction.Contains(entityKey);
                bool isAlreadyCollapsed = m_CollapsedByBuilding.ContainsKey(entityKey);
                bool isDisasterDisabled = disasterDisabled.Contains(entityKey); // H3: composite key

                if (isUnderConstruction || isAlreadyCollapsed || isDisasterDisabled)
                    continue;

                // Skip plants already collapsed (CollapsedProducer on separate mod entity)
                // H1: composite key eliminates orphan shadowing — no separate version check needed
                if (m_CollapsedByBuilding.TryGetValue(entityKey, out var existingCollapsedEntity)
                    && SystemAPI.HasComponent<CollapsedProducer>(existingCollapsedEntity))
                    continue;

                // Create separate mod entity for CollapsedProducer (avoids archetype change on vanilla)
                var collapsedEntity = ecb.CreateEntity();
                ecb.AddComponent(collapsedEntity, new CollapsedProducer
                {
                    Building = BuildingRef.FromEntity(entity)
                });
                IncrementEcbCount();

                disabledCount++;
            }

            Log.Info($"Disabled {disabledCount} power plants (grid collapse)");
        }

        private void RestoreAllProducers(EntityCommandBuffer ecb)
        {
            int restoredCount = 0;

            foreach (var (_, collapsedEntity) in
                SystemAPI.Query<RefRO<CollapsedProducer>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                ecb.AddComponent<Deleted>(collapsedEntity);
                IncrementEcbCount();
                restoredCount++;
            }

            Log.Info($"Restored {restoredCount} power plants (grid recovery)");
        }

        private void ObserveCollapseOwnerVersion(int collapseOwnerCount)
        {
            m_CollapseOwnerView.Publish(new CollapseOwnerSnapshot(collapseOwnerCount, m_CollapseOwnerRevision));
        }

        private int RebuildCollapseOwnerMap()
        {
            // Build frame-local map of CollapsedProducer entities by building identity.
            // PowerCapacityPipeline hydrates GridStressModifier from this sidecar.
            m_CollapsedByBuilding.Clear();
            m_CurCollapseOwnerKeys.Clear();
            int collapseOwnerCount = 0;
            foreach (var (collapsed, collapsedEntity) in
                SystemAPI.Query<RefRO<CollapsedProducer>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                collapseOwnerCount++;
                long packedKey = ((long)collapsed.ValueRO.Building.Index << 32) | (uint)collapsed.ValueRO.Building.Version;
                m_CollapsedByBuilding.TryAdd(packedKey, collapsedEntity);
                m_CurCollapseOwnerKeys.Add(packedKey);
            }

            return collapseOwnerCount;
        }

        // Reset path: clear prev set + bump revision so the empty publish is
        // not equal to any previous Empty(count=0, rev=N). Otherwise consumers
        // that cursored on a prior reset would miss the post-reset republish.
        private void ForceCollapseOwnerReset()
        {
            if (m_PrevCollapseOwnerKeys.IsCreated)
                m_PrevCollapseOwnerKeys.Clear();
#pragma warning disable CIVIC226 // Monotonic version stamp — overflow wraps, equality-only consumer.
            unchecked { m_CollapseOwnerRevision++; }
#pragma warning restore CIVIC226
            m_CollapseOwnerView.Publish(new CollapseOwnerSnapshot(0, m_CollapseOwnerRevision));
        }

        // Diff m_CurCollapseOwnerKeys against m_PrevCollapseOwnerKeys. Same
        // cardinality + every cur-key already in prev => no change; otherwise
        // a rotation, addition, or removal happened — bump revision and swap.
        private void BumpCollapseOwnerRevisionIfRotated()
        {
            bool rotated = m_CurCollapseOwnerKeys.Count != m_PrevCollapseOwnerKeys.Count;
            if (!rotated)
            {
                foreach (var key in m_CurCollapseOwnerKeys)
                {
                    if (!m_PrevCollapseOwnerKeys.Contains(key))
                    {
                        rotated = true;
                        break;
                    }
                }
            }
            if (rotated)
            {
#pragma warning disable CIVIC226 // Monotonic version stamp — overflow wraps, equality-only consumer.
                unchecked { m_CollapseOwnerRevision++; }
#pragma warning restore CIVIC226
                (m_PrevCollapseOwnerKeys, m_CurCollapseOwnerKeys) = (m_CurCollapseOwnerKeys, m_PrevCollapseOwnerKeys);
            }
        }

        private static GridStressConfig GetGridStressConfig()
        {
            return BalanceConfig.Current?.GridStress ?? new GridStressConfig();
        }

        private void ResetStress()
        {
            if (!m_StressDataQuery.TryGetSingletonEntity<GridStressData>(out var stressEntity))
                return;
            var stressData = m_StressDataLookup[stressEntity];

            if (stressData.IsCollapsed)
            {
                var ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                RestoreAllProducers(ecb);
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            }

            m_StressDataLookup[stressEntity] = GridStressData.CreateDefault();
            m_CollapsedByBuilding.Clear();
            m_LastZone = GridStressZone.Normal;
            m_LastGameHour = -1.0;
        }

        private static bool TryGetGameHour(out float gameHour)
            => GameTimeSystem.TryGetGameHours(out gameHour);

        private float CalculateDeltaHours(float currentHour)
        {
            if (m_LastGameHour < 0.0)
                return 0f;

            // TotalGameHours is monotonic — no day-wrap needed
            float delta = (float)(currentHour - m_LastGameHour);
            return math.clamp(delta, 0f, 4f); // 4h ceiling covers CS2 max speed (~3x = ~3h per 2.5s tick)
        }

        private void OnActChanged(ActChangedEvent evt)
        {
            ForceNextUpdate();

            // H-05 fix: skip reset when act hasn't actually changed from what we know.
            // CrisisActCoordinator re-publishes on load with PreviousAct=PreWar even in Crisis,
            // so event payload PreviousAct != NewAct doesn't distinguish real transitions from re-publish.
            // Instead, compare against our own m_KnownAct (synced in ValidateAfterLoad).
            if (evt.NewAct != m_KnownAct)
            {
                m_KnownAct = evt.NewAct;
                // Handler runs in the publisher's context; defer the reset (touches RestoreAllProducers
                // query iteration) to OnThrottledUpdate so it binds to this system.
                m_PendingActChangeReset = true;
                Log.Info($"[GridStress] Collapse reset queued on act change to {evt.NewAct}");
            }
            else
            {
                Log.Info($"[GridStress] Act re-published ({evt.NewAct}), stress preserved");
            }
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<ICollapseOwnerVersionReader>(this);

            UnsubscribeSafe<ActChangedEvent>(OnActChanged);

            if (m_CollapsedByBuilding.IsCreated)
                m_CollapsedByBuilding.Dispose();
            if (m_CurCollapseOwnerKeys.IsCreated)
                m_CurCollapseOwnerKeys.Dispose();
            if (m_PrevCollapseOwnerKeys.IsCreated)
                m_PrevCollapseOwnerKeys.Dispose();

            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }
}
