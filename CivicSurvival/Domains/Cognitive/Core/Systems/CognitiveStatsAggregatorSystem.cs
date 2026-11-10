using Game;
using Colossal.Serialization.Entities;
using Game.Buildings;
using Game.Common;
using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.PsyImpact;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Aggregates household-level PsyImpact statistics for UI display.
    /// Runs throttled (~1 second) to avoid per-frame overhead.
    ///
    /// Uses ASYNC pattern: schedule this frame, read results next frame.
    /// This eliminates sync point (.Complete()) that was blocking main thread.
    ///
    /// Provides:
    /// - Total households tracked
    /// - Average infection level, resistance, trauma
    /// - Counts of households affected by each stress source
    ///
    /// Data stored in CognitiveStatsState singleton, read by CognitiveUIPanel.
    ///
    /// S17b-6 ACCEPTED: District-level integrity is in CognitiveState (different system's responsibility).
    /// S17b-9 ACCEPTED: Default zeros for BlackoutVulnerabilityMult — standard ECS zero-init; recalculated on first update.
    /// </summary>
    [ActIndependent]
    public partial class CognitiveStatsAggregatorSystem : ThrottledSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CognitiveStatsAggregatorSystem");

        private const float FALLBACK_BLACKOUT_VULN_MAX_BONUS = 0.30f;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        private EntityQuery m_HouseholdQuery;
        private ComponentLookup<PropertyRenter> m_PropertyRenterLookup;

        // Async pattern: schedule job, read results next update
        private JobHandle m_PendingJobHandle;
        // PERF-LOCK: per-thread aggregation buckets + ScheduleParallel. The previous shared
        // NativeReference forced single-threaded Schedule — one worker crunched ~170k sidecars
        // for 10-30ms, and because that handle lands in Dependency (registered against every
        // type this system declares, CognitiveStatsState included), EVERY reader of the
        // cognitive singletons stalled for the whole scan (PsyOps/IPSO/Telemarathon/Mobilization,
        // PERF.log 2026-07-14). Do not collapse the buckets back into one shared accumulator.
        // False positive: the buckets ARE zeroed on reset — ResetState() => ClearTransientState(),
        // which completes the pending job and defaults every slot; the analyzer only scans the
        // ResetState body itself and does not follow the call.
#pragma warning disable CIVIC278
        private NativeArray<CognitiveStatsAggregation> m_PendingBuckets;
#pragma warning restore CIVIC278
        private bool m_HasPendingJob;

        // DidFire latch REMOVED: CogAgg now reads only persistent fields (BlackoutHours,
        // Trauma, InfectionLevel, Resistance_Value) — valid at any time, not just on MHR
        // fire frames. CogAgg fires on its own 1-second throttle independently of MHR.

        // FIX H2: Cache config for ApplyAggregationResults (same snapshot as job scheduling)
        private float m_PendingVulnThreshold;
        private float m_PendingVulnMaxHours;
        private float m_PendingVulnMaxBonus;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Ensure singleton exists
            CognitiveStatsState.EnsureExists(EntityManager);

            // Query all mod entities — persistent fields are valid on all entities.
            m_HouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<HouseholdPsyState>(),
                ComponentType.ReadOnly<PsyHouseholdLink>(),
                ComponentType.Exclude<Deleted>()
            );

            // Cache lookup for homeless filter
            m_PropertyRenterLookup = GetComponentLookup<PropertyRenter>(true);

            // Per-thread aggregation buckets (see PERF-LOCK on the field). ThreadIndexCount is
            // Unity's strict upper bound for [NativeSetThreadIndex] values (MaxJobThreadCount is not).
            m_PendingBuckets = new NativeArray<CognitiveStatsAggregation>(JobsUtility.ThreadIndexCount, Allocator.Persistent);

            // Pressure pipeline registration: consume channels for stats UI
            PressureRegistry.RegisterConsumer(PressureChannel.Blackout, nameof(CognitiveStatsAggregatorSystem));
            PressureRegistry.RegisterConsumer(PressureChannel.Envy, nameof(CognitiveStatsAggregatorSystem));
            PressureRegistry.RegisterConsumer(PressureChannel.Impact, nameof(CognitiveStatsAggregatorSystem));

            Log.Info("Created (async pattern, uses HouseholdPsyState)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            CognitiveStatsState.EnsureExists(EntityManager);
        }

        protected override void OnDestroy()
        {
            // Complete any pending job before disposal
            m_PendingJobHandle.Complete();
            m_HasPendingJob = false;
            m_PendingJobHandle = default;

            if (m_PendingBuckets.IsCreated)
                m_PendingBuckets.Dispose();

            // FIX L5: Symmetric deregister (matches OnCreate registrations)
            PressureRegistry.DeregisterConsumer(PressureChannel.Blackout, nameof(CognitiveStatsAggregatorSystem));
            PressureRegistry.DeregisterConsumer(PressureChannel.Envy, nameof(CognitiveStatsAggregatorSystem));
            PressureRegistry.DeregisterConsumer(PressureChannel.Impact, nameof(CognitiveStatsAggregatorSystem));

            base.OnDestroy();
        }

        // ShouldSkipUpdate REMOVED: no longer depends on MHR.DidFire latch.
        // Persistent fields are always valid — standard ThrottledSystemBase timing is sufficient.

        /// <summary>
        /// Single-source transient reset — drops the async aggregation in flight so the previous
        /// city's result can't be applied over a fresh CognitiveStatsState in STEP 1. Complete
        /// without Apply: the first post-load tick starts from a clean slate.
        /// </summary>
        private void ClearTransientState()
        {
            if (m_HasPendingJob)
            {
                m_PendingJobHandle.Complete();
                m_HasPendingJob = false;
            }
            m_PendingJobHandle = default;
            if (m_PendingBuckets.IsCreated)
            {
                for (int i = 0; i < m_PendingBuckets.Length; i++)
                    m_PendingBuckets[i] = default;
            }
            // Config snapshot cached alongside the in-flight job; drop it with the job so a stale
            // vulnerability threshold from the previous city can't be applied post-load.
            m_PendingVulnThreshold = 0f;
            m_PendingVulnMaxHours = 0f;
            m_PendingVulnMaxBonus = 0f;
        }

        void IResettable.ResetState() => ClearTransientState();

        public void ValidateAfterLoad() => ClearTransientState();

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // Load-boundary guard (crash class of dump c79a5ce5): never leave the aggregation job
            // in flight while the load path restructures the world — an async chunk-iterating job
            // caught by a load-time structural change dereferences stale chunk pointers. ResetState /
            // ValidateAfterLoad complete the handle too, but they run later in the load sequence;
            // this closes the window before deserialization starts.
            if (m_HasPendingJob)
            {
                m_PendingJobHandle.Complete();
                m_HasPendingJob = false;
                m_PendingJobHandle = default;
            }
        }

        protected override void OnThrottledUpdate()
        {
            // ============================================================
            // STEP 1: Complete PREVIOUS job and apply results
            // ============================================================
            if (m_HasPendingJob)
            {
                m_PendingJobHandle.Complete();
                m_HasPendingJob = false;
                ApplyAggregationResults();
            }

            // DidFire latch removed: CogAgg now reads persistent fields only.
            // Persistent fields are valid at any time — no dependency on MHR fire frame.

            // ============================================================
            // STEP 2: Schedule NEW job (no .Complete() - runs async)
            // ============================================================
            if (m_HouseholdQuery.IsEmptyIgnoreFilter)
            {
                // Reset stats if no households
                if (SystemAPI.TryGetSingletonRW<CognitiveStatsState>(out var stateRef))
                {
                    stateRef.ValueRW = default;
                    // FIX M5: BlackoutVulnerabilityMult neutral is 1.0f, not 0f
                    stateRef.ValueRW.BlackoutVulnerabilityMult = 1.0f;
                }
                return;
            }

            m_PropertyRenterLookup.Update(this);
            var balance = BalanceConfig.Current;
            if (balance == null) return;
            var cwCfg = balance.Cognitive;

            // FIX H2: Cache config snapshot — ApplyAggregationResults must use same values as job
            m_PendingVulnThreshold = cwCfg.BlackoutVulnThresholdHours;
            m_PendingVulnMaxHours = cwCfg.BlackoutVulnMaxHours;
            m_PendingVulnMaxBonus = cwCfg.BlackoutVulnMaxBonus;

            var job = new AggregateStatsJob
            {
                Buckets = m_PendingBuckets,
                PropertyRenterLookup = m_PropertyRenterLookup,
                StressThreshold = cwCfg.StressThreshold,
                InfectionThreshold = cwCfg.InfectionThreshold,
                BlackoutVulnThreshold = cwCfg.BlackoutVulnThresholdHours
            };

            // Schedule WITHOUT .Complete() - main thread is free
            // Uses Dependency as input (waits for upstream writes to HouseholdPsyState)
            // Must assign back to Dependency so PsyTransientResetSystem waits for our read
            // Reset immediately before scheduling because the job read-modify-writes its bucket slots.
            // Safe: the pending job was completed in STEP 1, so nothing is touching the buckets here.
            for (int i = 0; i < m_PendingBuckets.Length; i++)
                m_PendingBuckets[i] = default;
            if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre AggregateStatsJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} buckets={m_PendingBuckets.IsCreated}/{m_PendingBuckets.Length}");
            // PERF-LOCK: ScheduleParallel + per-thread buckets (see the field) — a single-thread
            // Schedule here re-creates the 10-30ms stall for every cognitive-singleton reader.
            Dependency = m_PendingJobHandle = job.ScheduleParallel(m_HouseholdQuery, Dependency);
            if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post AggregateStatsJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} buckets={m_PendingBuckets.IsCreated}/{m_PendingBuckets.Length}");
            m_HasPendingJob = true;
        }

        private void ApplyAggregationResults()
        {
            if (!SystemAPI.TryGetSingletonRW<CognitiveStatsState>(out var state))
                return;

            // Fold the per-thread buckets into one aggregate (job is complete — main-thread read).
            var agg = default(CognitiveStatsAggregation);
            for (int i = 0; i < m_PendingBuckets.Length; i++)
                agg.Add(m_PendingBuckets[i]);
            int count = agg.HouseholdCount;

            state.ValueRW.TotalHouseholds = count;

#pragma warning disable CIVIC190 // Division guarded by count > 0 check above
            if (count > 0)
            {
                state.ValueRW.AvgInfectionLevel = agg.TotalInfection / count;
                state.ValueRW.AvgResistance = agg.TotalResistance / count;
                state.ValueRW.AvgTrauma = agg.TotalTrauma / count;
            }
#pragma warning restore CIVIC190
            else
            {
                state.ValueRW.AvgInfectionLevel = 0f;
                state.ValueRW.AvgResistance = 0f;
                state.ValueRW.AvgTrauma = 0f;
            }

            state.ValueRW.HouseholdsUnderBlackout = agg.BlackoutCount;
            state.ValueRW.HouseholdsWithEnvy = agg.EnvyCount;
            state.ValueRW.HouseholdsUnderImpact = agg.ImpactCount;
            state.ValueRW.HouseholdsInfected = agg.InfectedCount;

            // CDI-7: Calculate blackout vulnerability stats
            state.ValueRW.VulnerableHouseholds = agg.VulnerableHouseholdCount;
            if (agg.VulnerableHouseholdCount > 0)
            {
                // Average blackout hours among vulnerable households
                float avgBlackoutHours = agg.TotalBlackoutHours / agg.VulnerableHouseholdCount;
                state.ValueRW.AvgBlackoutHours = avgBlackoutHours;

                // Calculate vulnerability multiplier (must match CognitiveCalculator.cs formula):
                // FIX W2-M4 + W3-M3: Use config divisor + cap (was hardcoded 24f, no cap)
                // FIX H2: Use cached config from scheduling frame (not fresh BalanceConfig.Current)
                float vulnThreshold = m_PendingVulnThreshold > 0f ? m_PendingVulnThreshold : 4f;
                float vulnMaxHours = m_PendingVulnMaxHours > 0f ? m_PendingVulnMaxHours : GameRate.HOURS_PER_DAY;
                // Clamp to [0,1] exactly as CognitiveCalculator.cs does (maxVulnBonus), so the displayed
                // multiplier can never overstate vulnerability past the gameplay cap for a config bonus > 1.
                float vulnBonus = Unity.Mathematics.math.clamp(
                    m_PendingVulnMaxBonus > 0f ? m_PendingVulnMaxBonus : FALLBACK_BLACKOUT_VULN_MAX_BONUS, 0f, 1f);
                float excessHours = Unity.Mathematics.math.max(0f, avgBlackoutHours - vulnThreshold);
                float safeMaxHours = Unity.Mathematics.math.max(vulnMaxHours, 0.001f);
                float vulnRatio = Unity.Mathematics.math.min(excessHours / safeMaxHours * vulnBonus, vulnBonus);
                state.ValueRW.BlackoutVulnerabilityMult = 1.0f + vulnRatio;
            }
            else
            {
                state.ValueRW.AvgBlackoutHours = 0f;
                state.ValueRW.BlackoutVulnerabilityMult = 1.0f;
            }

            // Per-stratum read model
            state.ValueRW.PoorHouseholds = agg.PoorCount;
            state.ValueRW.MiddleHouseholds = agg.MiddleCount;
            state.ValueRW.WealthyHouseholds = agg.WealthyCount;
#pragma warning disable CIVIC190 // Each division guarded by its own per-stratum count > 0 ternary
            state.ValueRW.PoorAvgInfection = agg.PoorCount > 0 ? agg.PoorInfection / agg.PoorCount : 0f;
            state.ValueRW.MiddleAvgInfection = agg.MiddleCount > 0 ? agg.MiddleInfection / agg.MiddleCount : 0f;
            state.ValueRW.WealthyAvgInfection = agg.WealthyCount > 0 ? agg.WealthyInfection / agg.WealthyCount : 0f;
            state.ValueRW.PoorAvgResistance = agg.PoorCount > 0 ? agg.PoorResistance / agg.PoorCount : 0f;
            state.ValueRW.MiddleAvgResistance = agg.MiddleCount > 0 ? agg.MiddleResistance / agg.MiddleCount : 0f;
            state.ValueRW.WealthyAvgResistance = agg.WealthyCount > 0 ? agg.WealthyResistance / agg.WealthyCount : 0f;

            // Per-stratum telecom coverage fraction (covered / classified count).
            state.ValueRW.PoorCoverageFraction = agg.PoorCount > 0 ? (float)agg.PoorCovered / agg.PoorCount : 0f;
            state.ValueRW.MiddleCoverageFraction = agg.MiddleCount > 0 ? (float)agg.MiddleCovered / agg.MiddleCount : 0f;
            state.ValueRW.WealthyCoverageFraction = agg.WealthyCount > 0 ? (float)agg.WealthyCovered / agg.WealthyCount : 0f;
#pragma warning restore CIVIC190

            // Runtime verification hook: per-stratum breakdown (Debug — enable [CognitiveStatsAggregatorSystem]).
            // classified = Poor+Middle+Wealthy; remainder up to count = households still Unknown (slot not yet fired).
            if (Log.IsDebugEnabled)
            {
                int classified = agg.PoorCount + agg.MiddleCount + agg.WealthyCount;
                Log.Debug($"[StratumBreakdown] total={count} classified={classified} poor={agg.PoorCount} middle={agg.MiddleCount} wealthy={agg.WealthyCount} (unknown={count - classified})");
                // Runtime verification: per-stratum telecom coverage (covered / count → fraction).
                Log.Debug($"[TelecomCoverage] poor={agg.PoorCovered}/{agg.PoorCount} ({state.ValueRO.PoorCoverageFraction:F2}) " +
                          $"middle={agg.MiddleCovered}/{agg.MiddleCount} ({state.ValueRO.MiddleCoverageFraction:F2}) " +
                          $"wealthy={agg.WealthyCovered}/{agg.WealthyCount} ({state.ValueRO.WealthyCoverageFraction:F2})");
            }
        }
    }

    /// <summary>
    /// Burst job for aggregating PsyImpact statistics across worker threads.
    /// Each thread accumulates into its own bucket ([NativeSetThreadIndex] slot), so the
    /// parallel writes are disjoint by construction — no atomics, no shared accumulator.
    /// The system folds the buckets after Complete (ApplyAggregationResults).
    /// </summary>
#if ENABLE_BURST
    [BurstCompile]
#endif
    public partial struct AggregateStatsJob : IJobEntity
    {
        // Disjoint per-thread slots; the attribute only lifts the too-coarse parallel-write
        // safety check (same justification as FillPsyModEntitiesJob's unique-index writes).
        [NativeDisableParallelForRestriction]
        public NativeArray<CognitiveStatsAggregation> Buckets;
        // Injected by the job system on each worker; always < JobsUtility.ThreadIndexCount.
        // CS0649 false positive: no C# code assigns the field — [NativeSetThreadIndex] makes the
        // job scheduler patch it per worker thread at execution time.
#pragma warning disable CS0649
        [NativeSetThreadIndex] private int m_ThreadIndex;
#pragma warning restore CS0649

        // Lookup to skip homeless households (mod entity exists but household lost PropertyRenter)
        [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenterLookup;

        // Config values (from BalanceConfig.Cognitive)
        public float StressThreshold;
        public float InfectionThreshold;
        // FIX W2-M4: Was hardcoded 4f
        public float BlackoutVulnThreshold;

        public void Execute(in PsyHouseholdLink link, in HouseholdPsyState psy)
        {
            // Skip homeless: mod entity exists but household no longer has PropertyRenter
            Entity household = link.GetHouseholdEntity();
            // FIX L3: Entity.Null guard — HasComponent(Entity.Null) is undefined in Burst
            if (household == Entity.Null || !PropertyRenterLookup.HasComponent(household))
                return;

            // Read-modify-write of THIS thread's own bucket — parallel-safe (slots are disjoint
            // by thread index; a worker never interleaves two Execute calls).
            var agg = Buckets[m_ThreadIndex];

            agg.HouseholdCount++;
            agg.TotalInfection += psy.InfectionLevel;
            agg.TotalResistance += psy.Resistance_Value;
            agg.TotalTrauma += psy.Trauma;

            // Switched from transient fields to persistent fields:
            // With PsySlot(16), only 1/16 entities have non-zero transients at any time.
            // Persistent fields are valid across all slots.
            // WARM-UP: HasImpactPressure / HasEnvyPressure / InCoverage / CachedStratum are cached latches
            // that Deserialize resets and MentalHealthResolverSystem re-resolves one slot at a time, so for
            // the first ~1 slot cycle (<1.5s) after a load these read low and impact/coverage counts can
            // briefly under-count. It self-heals once every slot has fired; a warm-up gate would be a
            // band-aid over a transient display glitch, so it is intentionally left as-is.
            if (psy.BlackoutHours > 0.001f)
                agg.BlackoutCount++;

            if (psy.HasImpactPressure)
                agg.ImpactCount++;

            if (psy.HasEnvyPressure)
                agg.EnvyCount++;

            if (psy.InfectionLevel > InfectionThreshold)
                agg.InfectedCount++;

            // CDI-7: Track blackout vulnerability
            // FIX W2-M4: Use config threshold (was hardcoded 4f)
            if (psy.BlackoutHours > BlackoutVulnThreshold)
            {
                agg.TotalBlackoutHours += psy.BlackoutHours;
                agg.VulnerableHouseholdCount++;
            }

            // Per-stratum read model. Unknown (not yet classified) excluded.
            // Per-stratum telecom-coverage tally — Covered / Count = the stratum's coverage fraction
            // (which class sits in the holes). Covered counted inside the same bucket so it can never
            // exceed Count.
            switch (psy.CachedStratum)
            {
                case SocialStratum.Poor:
                    agg.PoorCount++;
                    agg.PoorInfection += psy.InfectionLevel;
                    agg.PoorResistance += psy.Resistance_Value;
                    if (psy.InCoverage) agg.PoorCovered++;
                    break;
                case SocialStratum.Middle:
                    agg.MiddleCount++;
                    agg.MiddleInfection += psy.InfectionLevel;
                    agg.MiddleResistance += psy.Resistance_Value;
                    if (psy.InCoverage) agg.MiddleCovered++;
                    break;
                case SocialStratum.Wealthy:
                    agg.WealthyCount++;
                    agg.WealthyInfection += psy.InfectionLevel;
                    agg.WealthyResistance += psy.Resistance_Value;
                    if (psy.InCoverage) agg.WealthyCovered++;
                    break;
                default:
                    // Unknown (not yet classified) — intentionally excluded from per-stratum buckets.
                    break;
            }

            Buckets[m_ThreadIndex] = agg;
        }
    }

    /// <summary>
    /// Intermediate aggregation struct for job.
    /// </summary>
    public struct CognitiveStatsAggregation
    {
        public int HouseholdCount;
        public float TotalInfection;
        public float TotalResistance;
        public float TotalTrauma;
        public int BlackoutCount;
        public int EnvyCount;
        public int ImpactCount;
        public int InfectedCount;

        // CDI-7: Blackout vulnerability aggregation
        public float TotalBlackoutHours;       // Sum of BlackoutHours for households > 4h
        public int VulnerableHouseholdCount;   // Count of households with BlackoutHours > 4h

        // Per-stratum buckets (counts + sums for averaging)
        public int PoorCount;
        public int MiddleCount;
        public int WealthyCount;
        public float PoorInfection;
        public float MiddleInfection;
        public float WealthyInfection;
        public float PoorResistance;
        public float MiddleResistance;
        public float WealthyResistance;

        // Per-stratum telecom-covered counts (Covered / Count = coverage fraction).
        public int PoorCovered;
        public int MiddleCovered;
        public int WealthyCovered;

        /// <summary>Field-wise sum — folds the per-thread buckets after the parallel job completes.</summary>
        public void Add(in CognitiveStatsAggregation o)
        {
            HouseholdCount += o.HouseholdCount;
            TotalInfection += o.TotalInfection;
            TotalResistance += o.TotalResistance;
            TotalTrauma += o.TotalTrauma;
            BlackoutCount += o.BlackoutCount;
            EnvyCount += o.EnvyCount;
            ImpactCount += o.ImpactCount;
            InfectedCount += o.InfectedCount;
            TotalBlackoutHours += o.TotalBlackoutHours;
            VulnerableHouseholdCount += o.VulnerableHouseholdCount;
            PoorCount += o.PoorCount;
            MiddleCount += o.MiddleCount;
            WealthyCount += o.WealthyCount;
            PoorInfection += o.PoorInfection;
            MiddleInfection += o.MiddleInfection;
            WealthyInfection += o.WealthyInfection;
            PoorResistance += o.PoorResistance;
            MiddleResistance += o.MiddleResistance;
            WealthyResistance += o.WealthyResistance;
            PoorCovered += o.PoorCovered;
            MiddleCovered += o.MiddleCovered;
            WealthyCovered += o.WealthyCovered;
        }
    }

    /// <summary>
    /// Singleton component storing aggregated cognitive stats for UI.
    /// </summary>
    public struct CognitiveStatsState : IComponentData
    {
        public int TotalHouseholds;
        public float AvgInfectionLevel;
        public float AvgResistance;
        public float AvgTrauma;
        public int HouseholdsUnderBlackout;
        public int HouseholdsWithEnvy;
        public int HouseholdsUnderImpact;
        public int HouseholdsInfected;

        // CDI-7: Blackout vulnerability stats
        /// <summary>
        /// Average blackout hours for vulnerable households (those with >4h blackout).
        /// 0 if no households are vulnerable.
        /// </summary>
        public float AvgBlackoutHours;

        /// <summary>
        /// Number of households with extended blackout (>4h), making them propaganda-vulnerable.
        /// </summary>
        public int VulnerableHouseholds;

        /// <summary>
        /// Current blackout vulnerability multiplier (1.0 = no effect, 1.3 = +30% propaganda effectiveness).
        /// Calculated from average excess blackout hours of vulnerable households.
        /// </summary>
        public float BlackoutVulnerabilityMult;

        // ════════════════════════════════════════════════════════════════
        // Per-stratum read model (internal — consumed by later cognitive
        // phases, NOT a UI-DTO). Counts sum to TotalHouseholds minus still-Unknown
        // households (those whose resolver slot hasn't fired since load).
        // ════════════════════════════════════════════════════════════════
        public int PoorHouseholds;
        public int MiddleHouseholds;
        public int WealthyHouseholds;
        public float PoorAvgInfection;
        public float MiddleAvgInfection;
        public float WealthyAvgInfection;
        public float PoorAvgResistance;
        public float MiddleAvgResistance;
        public float WealthyAvgResistance;

        // ════════════════════════════════════════════════════════════════
        // Per-stratum telecom coverage fraction [0..1] (covered households / stratum households).
        // Which class sits in the coverage holes — the spatial-defence read model. Exposed outward
        // via ICognitiveCoverageReader (CognitiveCoverageCacheSystem reads these).
        // ════════════════════════════════════════════════════════════════
        public float PoorCoverageFraction;
        public float MiddleCoverageFraction;
        public float WealthyCoverageFraction;

        /// <summary>
        /// Ensure singleton entity exists.
        /// </summary>
        public static void EnsureExists(EntityManager em)
        {
            CivicSingleton.Ensure(em, new CognitiveStatsState { BlackoutVulnerabilityMult = 1.0f });
        }
    }
}
