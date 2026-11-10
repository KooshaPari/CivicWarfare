using System;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.PsyImpact;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Domain.Population;
using Unity.Collections.LowLevel.Unsafe;
using CivicSurvival.Core.Attributes;
using Unity.Jobs;

namespace CivicSurvival.Core.Features.Wellbeing
{
    /// <summary>
    /// Prepares mod wellbeing inputs; <see cref="WellbeingCitizenApplySystem"/>
    /// invokes the deferred citizen write after vanilla <c>CitizenHappinessSystem</c>.
    ///
    /// Aggregates all penalty sources and applies them once per frame:
    /// - District penalties (Winter, InternetDisabled, VIPVisible, etc.)
    /// - HouseholdPsyState (all pressure, trauma, infection, resistance)
    ///
    /// Safety contract: this system runs after the mod pressure/penalty producers.
    /// The write phase runs from <see cref="WellbeingCitizenApplySystem"/> after
    /// <c>ResidentHouseholdReadyMarker</c>; the Population marker itself is ordered
    /// after <c>CitizenHappinessSystem</c>. WCAS and WRS intentionally keep separate
    /// single anchors; if the marker chain places WCAS before WRS in a tick, WCAS
    /// applies the previous prepared snapshot and WRS's new snapshot is applied on the next tick.
    /// Because vanilla systems also write the <c>Citizen</c> struct, changes here
    /// must preserve read-modify-write behavior for fields outside <c>m_WellBeing</c>.
    ///
    /// DEFERRED SCHEDULE pattern:
    /// Frame N (throttle fire): prepare inputs (district map, config) — NO CitizenLookup
    /// Frame N+1 (next frame): Update lookups (vanilla Citizen job done → ~0ms sync), schedule job
    /// Eliminates 20-43ms CompleteDependencyBeforeRO spike from CitizenHappinessSystem.
    /// </summary>
    [ActIndependent]
    public partial class WellbeingResolverSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("WellbeingResolverSystem");

        private EntityQuery m_HouseholdQuery;


        // Lookups
        private ComponentLookup<Citizen> m_CitizenLookup;
        private BufferLookup<HouseholdCitizen> m_HouseholdCitizenLookup;
        private ComponentLookup<PropertyRenter> m_PropertyRenterLookup;
        private ComponentLookup<CurrentDistrict> m_CurrentDistrictLookup;

        // State service
        private IDistrictStateReader? m_DistrictState;
        // Service handle resolved from ServiceRegistry in OnStartRunning, not gameplay
        // state — re-resolved after load, never persisted.
        [NonSerialized] private IResidentPopulationEligibilityView? m_EligibilityView;
        private DistrictPenaltySystem? m_PenaltySystem;

        // No ECB barrier: direct ComponentLookup write eliminates deferred playback overhead entirely.

        // Diagnostic counters — NativeArray for Interlocked.Increment in ScheduleParallel
        [NonEntityIndex] private NativeArray<int> m_DiagEcbWrites;
        [NonEntityIndex] private NativeArray<int> m_DiagEarlyExits;
        [NonEntityIndex] private NativeArray<int> m_DiagProcessed;
        [NonEntityIndex] private NativeArray<int> m_DiagHouseholds;
        private JobHandle m_LastResolveJobHandle;

        // Static snapshots for PERF.log reporting (read after job complete, 1 frame lag)
#pragma warning disable CIVIC031, S2696 // Cross-system diagnostic read — PerfReportSections
        private static int s_LastEcbWrites;
        private static int s_LastEarlyExits;
        private static int s_LastProcessed;
        public static int LastEcbWrites => s_LastEcbWrites;
        public static int LastEarlyExits => s_LastEarlyExits;
        public static int LastProcessed => s_LastProcessed;
#pragma warning restore CIVIC031, S2696

        // District link update interval in seconds
        private const float DISTRICT_LINK_UPDATE_SECONDS = 5.0f;


        // Throttle (manual — not ThrottledSystemBase, because deferred schedule needs per-frame control)
        private ThrottleHelper m_Throttle;
        private bool m_ThrottleInitialized;

        // FNV-style hash constants (mirrors ThrottledSystemBase)
        private const int HASH_SEED = 17;
        private const int HASH_PRIME = 31;

        /// <summary>
        /// Deterministic hash from type name for stagger phase.
        /// Mirrors ThrottledSystemBase.StableTypeHash (full char iteration, not just Length).
        /// </summary>
        private static int StableTypeHash(string name)
        {
            unchecked
            {
                int hash = HASH_SEED;
                for (int i = 0; i < name.Length; i++)
                    hash = hash * HASH_PRIME + name[i];
                return hash & 0x7FFFFFFF;
            }
        }

        // Deferred schedule state
        private bool m_ReadyToSchedule;
        [NonEntityIndex] private NativeHashMap<int, float> m_PendingDistrictPenalties;

        private float m_PendingStressToWellbeing;
        private float m_PendingInfectionStressWeight;
        private float m_PendingResistanceStressReduction;
        private float m_PendingGlobalPenalty;
        private int m_PendingHouseholdCount;
        private int m_PendingDistrictCount;
        private float m_PendingMaxDistrictPenalty;
        private int m_PendingSlotIndex;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Main query: mod entities with HouseholdPsyState.
            // PsySlot filter applied in ScheduleDeferredJob — process 1/4 per fire (~43K not 172K).
            // Synced with MHR.CurrentSlot so WRS processes the same slot MHR just wrote.
            m_HouseholdQuery = GetEntityQuery(
                ComponentType.ReadWrite<HouseholdPsyState>(),
                ComponentType.ReadOnly<PsyHouseholdLink>(),
                ComponentType.ReadOnly<PsySlot>()
            );

            // Cache lookups
            m_CitizenLookup = GetComponentLookup<Citizen>(false); // RW: direct write, no ECB
            m_HouseholdCitizenLookup = GetBufferLookup<HouseholdCitizen>(true);
            m_PropertyRenterLookup = GetComponentLookup<PropertyRenter>(true);
            m_CurrentDistrictLookup = GetComponentLookup<CurrentDistrict>(true);

            // Diagnostic counters
            m_DiagEcbWrites = new NativeArray<int>(1, Allocator.Persistent);
            m_DiagEarlyExits = new NativeArray<int>(1, Allocator.Persistent);
            m_DiagProcessed = new NativeArray<int>(1, Allocator.Persistent);
            m_DiagHouseholds = new NativeArray<int>(1, Allocator.Persistent);

            // S4-01 FIX: Get penalty system for global happiness penalties.
            // The system can be created later during registration/test-world setup,
            // so OnUpdate retries instead of permanently disabling this resolver.
            m_PenaltySystem = World.GetExistingSystemManaged<DistrictPenaltySystem>();

            Log.Info("Created (deferred wellbeing input prep)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_DistrictState ??= ServiceRegistry.Instance.Require<IDistrictStateReader>();
            m_EligibilityView ??= ServiceRegistry.Instance.Require<IResidentPopulationEligibilityView>();
        }

        // Stats for logging (encapsulated to avoid CA2211)
        private static int s_LastHouseholdCount;
        private static int s_LastDistrictCount;
        private static float s_LastMaxDistrictPenalty;

        public static int LastHouseholdCount => s_LastHouseholdCount;
        public static int LastDistrictCount => s_LastDistrictCount;
        public static float LastMaxDistrictPenalty => s_LastMaxDistrictPenalty;

        private static void SetResolverStats(int householdCount, int districtCount, float maxDistrictPenalty)
        {
            s_LastHouseholdCount = householdCount;
            s_LastDistrictCount = districtCount;
            s_LastMaxDistrictPenalty = maxDistrictPenalty;
        }

        // [POP-READY] Wellbeing gate log (Verification table): proves WRS skips while the
        // selection is not ready (skipped=true) and resumes once ready (skipped=false), so
        // an empty eligibility is never read as "no eligible households".
        private void LogWellbeingGate(int eligibleCount, bool skipped)
        {
            if (!Log.IsDebugEnabled)
                return;

            bool selectionReady = m_EligibilityView != null && m_EligibilityView.IsSelectionReady;
            Log.Info($"[POP-READY] Wellbeing readiness={(selectionReady ? "SelectionReady" : "NotReady")} eligibleCount={eligibleCount} skipped={skipped}");
        }

        protected override void OnUpdateImpl()
        {
            // Lazy init (matches ThrottledSystemBase pattern)
            if (!m_ThrottleInitialized)
            {
                int phase = StableTypeHash(GetType().Name);
                m_Throttle = new ThrottleHelper(Engine.Timing.UPDATE_INTERVAL_500_MS, phase);
                m_ThrottleInitialized = true;
            }

            // ════════════════════════════════════════════════════════════════
            // PHASE 1: Prepare inputs (throttled, no CitizenLookup → no sync point)
            // ════════════════════════════════════════════════════════════════
            if (!m_Throttle.ShouldUpdate())
                return;

            if (m_HouseholdQuery.IsEmptyIgnoreFilter)
                return;

            m_PenaltySystem ??= World.GetExistingSystemManaged<DistrictPenaltySystem>();
            if (m_DistrictState == null || m_PenaltySystem == null)
                return;

            // Readiness gate (A7). The resolve job reads the resident eligibility set
            // (EligibleHouseholds) to decide which households are residents. Before the
            // selection is rebuilt, that set is cold/empty — the job's implicit
            // EligibleHouseholds.Contains() skip would then read every household as
            // "not eligible" and silently apply nothing. Gate explicitly so an empty
            // eligibility is only acted on once it is genuinely ready: skip this throttle
            // fire and retry on the next one (500ms; happiness penalties are per-day rates,
            // a single skip is well inside the latency window). Resolved in OnStartRunning,
            // which always precedes OnUpdateImpl.
            if (!m_EligibilityView!.IsSelectionReady)
            {
                LogWellbeingGate(eligibleCount: 0, skipped: true);
                return;
            }

            // Previous prep still pending consumption. Two ways to land here:
            //   (a) WCAS skipped between two of our throttle fires (resolver wiring
            //       failure, gameplay-ready flipped mid-window) — overwriting now
            //       would leak the prior TempJob map.
            //   (b) WCAS consumed and the apply job is still in flight — disposing
            //       or overwriting now would race the job's read.
            // Both are resolved by skipping this throttle fire and letting the next
            // one find a clean slate. Happiness penalties are per-day rates; a single
            // 500ms throttle skip is well inside the acceptable latency window.
            if (m_PendingDistrictPenalties.IsCreated)
                return;

            // R3-C-8: Snapshot may be up to 500ms stale from DistrictPenaltySystem's throttle.
            // Combined with WRS's own 500ms throttle + 1-frame deferred schedule = ~1s max latency.
            // Acceptable: happiness penalties are per-day rates, 1s window is negligible.
            var snapshot = m_DistrictState.TakeSnapshot();
            m_PendingDistrictPenalties = BuildDistrictPenaltyMap(snapshot);

            try
            {
                // S4-01 FIX: Read global penalties (Winter, InfraCollapse, Conscription, Mourning, PreWarTension)
                m_PendingGlobalPenalty = m_PenaltySystem.GlobalHappinessPenaltyTotal;

                // Save config values for deferred schedule
                var config = BalanceConfig.Current;
                var cwCfg = config.Cognitive;
                var penalties = config.Penalties;
                m_PendingStressToWellbeing = cwCfg.StressToWellbeing;
                m_PendingInfectionStressWeight = cwCfg.InfectionStressWeight;
                m_PendingResistanceStressReduction = cwCfg.ResistanceStressReduction;

                // Stats that do not depend on the slot-filtered query are computed now.
                // The household count is captured in ScheduleDeferredJob after applying
                // the same PsySlot shared-component filter used for the job schedule.
                m_PendingDistrictCount = m_PendingDistrictPenalties.Count;
                m_PendingMaxDistrictPenalty = m_PendingGlobalPenalty; // S4-01: floor = global penalty
                if (snapshot.DistrictPenalties != null)
                {
                    foreach (var kvp in snapshot.DistrictPenalties)
                    {
                        float combined = math.clamp(
                            kvp.Value.TotalHappinessPenalty + m_PendingGlobalPenalty,
                            -penalties.MaxHappinessPenalty,
                            penalties.MaxHappinessPenalty);
                        if (combined > m_PendingMaxDistrictPenalty)
                            m_PendingMaxDistrictPenalty = combined;
                    }
                }

                m_PendingSlotIndex = PsySlot.CurrentSlot;
                m_ReadyToSchedule = true;
            }
            catch
            {
                DisposePendingDistrictPenaltiesNow();
                throw;
            }
        }

        [CompletesDependency("WRS diagnostic snapshot: previous ResolveWellbeingJob writes NativeArray counters with safety disabled; complete its handle before reading/resetting counters for the next fire.")]
        internal void TrySchedulePendingCitizenWrite()
        {
            if (!m_ReadyToSchedule)
                return;

            m_ReadyToSchedule = false;

            // PERF-LOCK: counters use unsafe interlocked writes inside the parallel job.
            // The deferred frame normally makes this complete already, but the explicit
            // handle join is the race-free contract before reading/resetting them.
            m_LastResolveJobHandle.Complete();

            // Snapshot previous fire's diagnostics.
            if (m_DiagProcessed.IsCreated)
            {
#pragma warning disable S2696 // Static diagnostic snapshot
                s_LastEcbWrites = m_DiagEcbWrites[0];
                s_LastEarlyExits = m_DiagEarlyExits[0];
                s_LastProcessed = m_DiagProcessed[0];
                s_LastHouseholdCount = m_DiagHouseholds[0];
#pragma warning restore S2696
            }

            try
            {
                using (PerformanceProfiler.Measure("WRS.DeferredSchedule"))
                {
                    ScheduleDeferredJob();
                }
            }
            catch
            {
                DisposePendingDistrictPenaltiesNow();
                throw;
            }
        }

        /// <summary>
        /// Deferred schedule: runs on frame N+1 after throttle fire on frame N.
        /// CitizenLookup.Update() sync point is near-zero because vanilla Citizen writers
        /// from frame N have completed on worker threads during the intervening frame.
        /// </summary>
        private void ScheduleDeferredJob()
        {
            if (PsySlot.CurrentSlot != m_PendingSlotIndex)
            {
                DisposePendingDistrictPenaltiesNow();
                return;
            }

            m_HouseholdQuery.ResetFilter();
            m_HouseholdQuery.AddSharedComponentFilter(new PsySlot { SlotIndex = m_PendingSlotIndex });
            if (m_HouseholdQuery.IsEmptyIgnoreFilter)
            {
                DisposePendingDistrictPenaltiesNow();
                return;
            }

            var eligibilityView = m_EligibilityView!;

            // Pull resident eligibility from the canonical Population owner.
            // WRS applies effects; it does not define which households are resident.
            Dependency = JobHandle.CombineDependencies(Dependency, eligibilityView.GetReadJobHandle());
            NativeParallelHashSet<Entity>.ReadOnly eligibleHouseholds = eligibilityView.EligibleHouseholds;
            LogWellbeingGate(eligibleCount: eligibleHouseholds.Count(), skipped: false);

            // Update lookups — vanilla Citizen job from PREVIOUS frame is done → ~0ms sync
            using (PerformanceProfiler.Measure("SP:WRS.LookupSync"))
            {
                m_CitizenLookup.Update(this);
                // PERF-LOCK: deferred-frame schedule means vanilla HouseholdCitizen buffer writers are complete -> near-zero sync.
                // Do not move into throttled prep phase (Frame N) - that resurrects the SP:Citizen sync.
                m_HouseholdCitizenLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                m_CurrentDistrictLookup.Update(this);
            }

            if (!GameTimeSystem.TryGetGameHours(out var gameHours))
            {
                DisposePendingDistrictPenaltiesNow();
                return;
            }
            float currentTime = gameHours * GameRate.SECONDS_PER_HOUR;

            // Reset diagnostic counters
            m_DiagEcbWrites[0] = 0;
            m_DiagEarlyExits[0] = 0;
            m_DiagProcessed[0] = 0;
            m_DiagHouseholds[0] = 0;

            // Schedule main job with pending inputs
            var penalties = BalanceConfig.Current.Penalties;
            var resolveJob = new ResolveWellbeingJob
            {
                HouseholdCitizenLookup = m_HouseholdCitizenLookup,
                EligibleHouseholds = eligibleHouseholds,
                DistrictPenalties = m_PendingDistrictPenalties,
                GlobalPenalty = m_PendingGlobalPenalty,
                CitizenLookup = m_CitizenLookup,
                PropertyRenterLookup = m_PropertyRenterLookup,
                CurrentDistrictLookup = m_CurrentDistrictLookup,
                CurrentTime = currentTime,
                DistrictLinkUpdateInterval = DISTRICT_LINK_UPDATE_SECONDS,
                StressToWellbeing = m_PendingStressToWellbeing,
                InfectionStressWeight = m_PendingInfectionStressWeight,
                ResistanceStressReduction = m_PendingResistanceStressReduction,
                MaxDistrictPenalty = penalties.MaxHappinessPenalty,
                DecayRate = penalties.WellbeingDecayRate,
                RecoveryRate = penalties.WellbeingRecoveryRate,
                WellbeingBaseline = penalties.WellbeingBaseline,
                DiagEcbWrites = m_DiagEcbWrites,
                DiagEarlyExits = m_DiagEarlyExits,
                DiagProcessed = m_DiagProcessed,
                DiagHouseholds = m_DiagHouseholds
            };

            bool jobScheduled = false;
            try
            {
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre ResolveWellbeingJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} districtPenalties={m_PendingDistrictPenalties.IsCreated}/count={m_PendingDistrictPenalties.Count}/capacity={m_PendingDistrictPenalties.Capacity} diagEcb={m_DiagEcbWrites.IsCreated}/{m_DiagEcbWrites.Length} diagEarly={m_DiagEarlyExits.IsCreated}/{m_DiagEarlyExits.Length} diagProcessed={m_DiagProcessed.IsCreated}/{m_DiagProcessed.Length} diagHouseholds={m_DiagHouseholds.IsCreated}/{m_DiagHouseholds.Length}");
                Dependency = resolveJob.ScheduleParallel(m_HouseholdQuery, Dependency);
                if (Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post ResolveWellbeingJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} districtPenalties={m_PendingDistrictPenalties.IsCreated}/count={m_PendingDistrictPenalties.Count}/capacity={m_PendingDistrictPenalties.Capacity} diagEcb={m_DiagEcbWrites.IsCreated}/{m_DiagEcbWrites.Length} diagEarly={m_DiagEarlyExits.IsCreated}/{m_DiagEarlyExits.Length} diagProcessed={m_DiagProcessed.IsCreated}/{m_DiagProcessed.Length} diagHouseholds={m_DiagHouseholds.IsCreated}/{m_DiagHouseholds.Length}");
                m_LastResolveJobHandle = Dependency;
                jobScheduled = true;

                if (m_PendingDistrictPenalties.IsCreated)
                {
                    var penaltiesToDispose = m_PendingDistrictPenalties;
                    m_PendingDistrictPenalties = default;
                    Dependency = penaltiesToDispose.Dispose(Dependency);
                }
                eligibilityView.AddEligibilityReader(Dependency);
            }
            catch
            {
                if (!jobScheduled && m_PendingDistrictPenalties.IsCreated)
                    DisposePendingDistrictPenaltiesNow();
                throw;
            }

            m_PendingHouseholdCount = s_LastHouseholdCount;
            int districtCount = m_PendingDistrictCount;

            SetResolverStats(m_PendingHouseholdCount, districtCount, m_PendingMaxDistrictPenalty);

            if (Log.IsDebugEnabled) Log.Debug($"Resolved wellbeing: {m_PendingHouseholdCount} households, {districtCount} districts, max district penalty: {m_PendingMaxDistrictPenalty:P0}");
        }

        /// <summary>
        /// Build NativeHashMap of district happiness penalties.
        /// Only includes district-level penalties (NOT Blackout/Envy - those go through PsyPressure).
        /// </summary>
        private NativeHashMap<int, float> BuildDistrictPenaltyMap(DistrictStateSnapshot snapshot)
        {
            var map = new NativeHashMap<int, float>(64, Allocator.TempJob);

            if (snapshot.DistrictPenalties == null)
                return map;

            foreach (var kvp in snapshot.DistrictPenalties)
            {
                float penalty = kvp.Value.TotalHappinessPenalty;
                // IMPORTANT: pass BOTH positive penalties AND negative bonuses (FoodAidProvided = -0.15).
                // Do NOT change to > 0f; that kills food aid happiness bonus silently.
                // Linked: WRS job clamp (allows negative), BlackoutSystem.Serialization bounds (allows negative).
                if (math.abs(penalty) > float.Epsilon)
                {
                    map.TryAdd(kvp.Key, penalty);
                }
            }

            return map;
        }

        protected override void OnDestroy()
        {
            ResetPendingDeferredStateForLoad();
            if (m_DiagEcbWrites.IsCreated) m_DiagEcbWrites.Dispose();
            if (m_DiagEarlyExits.IsCreated) m_DiagEarlyExits.Dispose();
            if (m_DiagProcessed.IsCreated) m_DiagProcessed.Dispose();
            if (m_DiagHouseholds.IsCreated) m_DiagHouseholds.Dispose();

            base.OnDestroy();
        }

        public void ValidateAfterLoad()
        {
            ResetPendingDeferredStateForLoad();
        }

        private void ResetPendingDeferredStateForLoad()
        {
            // LOAD-INVARIANT: pending TempJob state from one city cannot be scheduled in the next city.
            m_LastResolveJobHandle.Complete();
            Dependency.Complete();
            m_ReadyToSchedule = false;
            DisposePendingDistrictPenaltiesNow();
            m_PendingStressToWellbeing = 0f;
            m_PendingInfectionStressWeight = 0f;
            m_PendingResistanceStressReduction = 0f;
            m_PendingGlobalPenalty = 0f;
            m_PendingHouseholdCount = 0;
            m_PendingDistrictCount = 0;
            m_PendingMaxDistrictPenalty = 0f;
            m_PendingSlotIndex = 0;
        }

        private void DisposePendingDistrictPenaltiesNow()
        {
            if (m_PendingDistrictPenalties.IsCreated)
                m_PendingDistrictPenalties.Dispose();
            m_PendingDistrictPenalties = default;
        }
    }

    /// <summary>
    /// Burst job that resolves all wellbeing penalties and applies the single
    /// mod-owned <c>Citizen.m_WellBeing</c> value while preserving the rest of
    /// the vanilla <c>Citizen</c> struct.
    ///
    /// Thread safety: Each citizen belongs to exactly ONE household.
    /// </summary>
#if ENABLE_BURST
    [BurstCompile]
#endif
    public partial struct ResolveWellbeingJob : IJobEntity
    {
        private const float MAX_WELLBEING = 100f;
        private const int MAX_STRESS_PENALTY = 50;

        // District penalties (happiness only, excludes Blackout/Envy)
        [ReadOnly] public BufferLookup<HouseholdCitizen> HouseholdCitizenLookup;
        [ReadOnly] public NativeParallelHashSet<Entity>.ReadOnly EligibleHouseholds;
        [ReadOnly] public NativeHashMap<int, float> DistrictPenalties;

        // S4-01 FIX: Global penalties (Winter, InfraCollapse, Conscription, Mourning, PreWarTension)
        public float GlobalPenalty;

        // For district link updates
        [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenterLookup;
        [ReadOnly] public ComponentLookup<CurrentDistrict> CurrentDistrictLookup;

        // Direct write to Citizen — eliminates deferred ECB playback cost entirely.
        // [NativeDisableParallelForRestriction] safe: each citizen belongs to exactly ONE household,
        // ScheduleParallel guarantees each entity processed by one thread.
        // Deferred schedule (Frame N+1) ensures vanilla CitizenHappinessSystem job is complete → ~0ms sync.
        [NativeDisableParallelForRestriction]
        public ComponentLookup<Citizen> CitizenLookup;

        // Diagnostic: count ECB writes and early-exits per fire.
        // Atomic via Interlocked.Increment (NativeArray for ref access in Burst).
        [NativeDisableParallelForRestriction]
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> DiagEcbWrites;
        [NativeDisableParallelForRestriction]
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> DiagEarlyExits;
        [NativeDisableParallelForRestriction]
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> DiagProcessed;
        [NativeDisableParallelForRestriction]
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<int> DiagHouseholds;

        public float CurrentTime;
        public float DistrictLinkUpdateInterval;

        // Config
        public float StressToWellbeing;
        public float InfectionStressWeight;
        public float ResistanceStressReduction;
        public float MaxDistrictPenalty;
        public byte DecayRate;
        public byte RecoveryRate;
        public byte WellbeingBaseline;

        public void Execute(in PsyHouseholdLink link, ref HouseholdPsyState psy)
        {
            // Reconstruct vanilla household from the sidecar's identity link.
            Entity householdEntity = link.GetHouseholdEntity();
            if (!EligibleHouseholds.Contains(householdEntity))
                return;

            unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagHouseholds.GetUnsafePtr())[0]); }

            if (!HouseholdCitizenLookup.TryGetBuffer(householdEntity, out var citizens))
                return;

            // ================================================================
            // EARLY-EXIT: Burst-friendly, minimal branching.
            // District link refresh is part of the predicate: after load/new-game
            // the cached link can be unresolved even when the household has no
            // trauma/infection/blackout, and district-only penalties must still bind.
            // ================================================================
            bool hasStateStress =
                psy.Trauma >= 0.001f ||
                psy.InfectionLevel >= 0.001f ||
                psy.BlackoutHours >= 0.001f ||
                psy.RecoveryInertia >= 0.001f;

            bool districtLinkNeedsRefresh =
                psy.DistrictLink_Index < 0 ||
                CurrentTime - psy.DistrictLink_LastUpdateTime >= DistrictLinkUpdateInterval;

            float districtPenalty = CalculateDistrictPenalty(psy.DistrictLink_Index);

            if (!hasStateStress &&
                !districtLinkNeedsRefresh &&
                districtPenalty > -0.001f && districtPenalty < 0.001f)
            {
                unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagEarlyExits.GetUnsafePtr())[0]); }
                return; // zero stress + zero district → vanilla handles wellbeing
            }

            // Skip if household has no PropertyRenter (homeless)
            if (!PropertyRenterLookup.HasComponent(householdEntity))
            {
                if (districtLinkNeedsRefresh)
                {
                    psy.DistrictLink_LastUpdateTime = CurrentTime;
                    psy.DistrictLink_Index = -1;
                }
                unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagEarlyExits.GetUnsafePtr())[0]); }
                return;
            }

            // ================================================================
            // STEP 1: Update district link if stale (refresh districtPenalty after)
            // ================================================================
            if (districtLinkNeedsRefresh)
            {
                UpdateDistrictLink(householdEntity, ref psy);
                districtPenalty = CalculateDistrictPenalty(psy.DistrictLink_Index);
            }

            if (!hasStateStress &&
                districtPenalty > -0.001f && districtPenalty < 0.001f)
            {
                unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagEarlyExits.GetUnsafePtr())[0]); }
                return; // refreshed link still has no district effect
            }
            unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagProcessed.GetUnsafePtr())[0]); }

            // District penalty caps wellbeing (e.g., 15% penalty → cap 85/100).
            // IMPORTANT: lower bound is NEGATIVE to allow bonuses (FoodAidProvided = -0.15 → +15% wellbeing).
            // Data flow: DistrictPenaltySystem (-0.15) → BuildDistrictPenaltyMap (filter != 0f) → HERE → clamp.
            // If you change this clamp to [0, max] — bonuses silently die. Also update BlackoutSystem.Serialization
            // ReadSafeFloat bounds (must allow negative) and DistrictPenaltyCalculator math.min (already allows negative).
            int districtCap = (int)math.round(MAX_WELLBEING * (1f - math.clamp(districtPenalty, -MaxDistrictPenalty, MaxDistrictPenalty)));
            byte targetFromDistrict = (byte)math.clamp(districtCap, 0, (int)MAX_WELLBEING);

            // ================================================================
            // STEP 3: Calculate household stress (from HouseholdPsyState)
            // T4-2 fix: Use ONLY persistent fields (Trauma, InfectionLevel).
            // Transient Pressure_* fields are input signals for MHR — WRS reads MHR's
            // processed output. Trauma already accumulates pressure effects via TraumaCalculator.
            // Reading transients here was dead code (always 0 due to deferred schedule timing).
            // ================================================================
            float totalStress = psy.Trauma +
                               (psy.InfectionLevel * InfectionStressWeight);

            // ================================================================
            // STEP 4: Apply resistance and inertia
            // ================================================================
            float inertiaFactor = 1f + psy.RecoveryInertia;
            float effectiveStress = totalStress *
                (1f - psy.Resistance_Value * ResistanceStressReduction) *
                inertiaFactor;

            // Convert to wellbeing penalty (capped at MAX_STRESS_PENALTY)
            byte stressPenalty = (byte)math.round(math.clamp(effectiveStress * StressToWellbeing, 0, MAX_STRESS_PENALTY));

            // ================================================================
            // STEP 5: Apply to all citizens in household
            // ================================================================
            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizenEntity = citizens[i].m_Citizen;
                if (!CitizenLookup.HasComponent(citizenEntity))
                    continue;

                var citizen = CitizenLookup[citizenEntity];

                // Soft Target: baseline (config, ~75) minus stress penalty.
                // Stable: baseline is constant → no spiral. Citizen decays toward soft target, never below.
                // E.g. baseline=75, stressPenalty=20 → softTarget=55. Citizen at 80 → decays to 55.
                int softTarget = math.max(0, WellbeingBaseline - stressPenalty);
                byte target = (byte)math.min(targetFromDistrict, softTarget);

                // Gradual decay toward target (direct write, no ECB)
                if (citizen.m_WellBeing > target)
                {
                    int newWellbeing = citizen.m_WellBeing - DecayRate;
                    citizen.m_WellBeing = (byte)math.max(newWellbeing, target);
                    CitizenLookup[citizenEntity] = citizen;
                    unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagEcbWrites.GetUnsafePtr())[0]); }
                }
                // Recovery toward district cap when bonus active (FoodAid)
                else if (districtPenalty < 0f && citizen.m_WellBeing < targetFromDistrict)
                {
                    int newWellbeing = citizen.m_WellBeing + RecoveryRate;
                    citizen.m_WellBeing = (byte)math.min(newWellbeing, targetFromDistrict);
                    CitizenLookup[citizenEntity] = citizen;
                    unsafe { System.Threading.Interlocked.Increment(ref ((int*)DiagEcbWrites.GetUnsafePtr())[0]); }
                }
            }
        }

        private void UpdateDistrictLink(Entity householdEntity, ref HouseholdPsyState psy)
        {
            psy.DistrictLink_LastUpdateTime = CurrentTime;
            psy.DistrictLink_Index = -1;

            // Household → PropertyRenter → Building → CurrentDistrict
            if (!PropertyRenterLookup.TryGetComponent(householdEntity, out var renter))
                return;

            Entity building = renter.m_Property;
            if (building == Entity.Null)
                return;

            if (!CurrentDistrictLookup.TryGetComponent(building, out var district))
                return;

            psy.DistrictLink_Index = district.m_District.Index;
        }

        private float CalculateDistrictPenalty(int districtIndex)
        {
            float penalty = GlobalPenalty;
            if (districtIndex >= 0 &&
                DistrictPenalties.TryGetValue(districtIndex, out float districtPenalty))
            {
                penalty += districtPenalty;
            }

            return penalty;
        }
    }
}
