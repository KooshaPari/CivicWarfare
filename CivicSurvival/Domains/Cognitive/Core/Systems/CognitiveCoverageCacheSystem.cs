using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Cognitive;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Per-stratum cognitive-defence coverage projection. Single owner/registrar of the
    /// <see cref="ICognitiveCoverageReader"/> service — modelled on
    /// <c>LiveAACacheSystem</c>/<c>IAirDefenseCoverageReader</c>: it READS three Core singletons
    /// (<see cref="HeroDeploymentState"/>, <see cref="TelemarathonRuntimeState"/>,
    /// <see cref="BuckwheatSingleton"/>) and projects, via <see cref="StratumDefense"/>, the posture
    /// the PSYOPS raid launcher's gap-targeting (and a later UI overlay) read without importing the
    /// Cognitive domain (Axiom 5).
    ///
    /// CIVIC175 untouched — this is a derived managed projection, not an <c>IComponentData</c>
    /// singleton: it writes nothing. Not serialized (rebuilt on the first post-load tick). Unlike
    /// the AA cache there is no order-version cursor: the source is three cheap singleton reads with
    /// no sync point, so the throttle alone suffices.
    /// </summary>
    [ActIndependent]
    public sealed partial class CognitiveCoverageCacheSystem : ThrottledSystemBase, ICognitiveCoverageReader, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CognitiveCoverageCacheSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        private EntityQuery m_HeroQuery;
        private EntityQuery m_TelemarathonQuery;
        private EntityQuery m_BuckwheatQuery;
        private EntityQuery m_CenterQuery;

        // Per-stratum telecom coverage fraction, produced by CognitiveStatsAggregatorSystem
        // (same domain, no Axiom 5 concern). Projected outward on StratumCoverage.CoverageFraction.
        private EntityQuery m_StatsQuery;

        // Precise per-district aided-set (the SAME projection the cognitive resolver reads for
        // Buckwheat's per-household infection effect). Buckwheat covers the poor only where a district
        // is under an ACTIVE food-aid effect — reserve-on-hand alone does not (aid may be undistributed
        // or expired). Reading the buffer keeps the coverage posture in step with infection.
        private BufferLookup<BuckwheatAidedDistrictBuffer> m_BuckwheatAidedLookup;

        // Managed coverage projection (3 strata), rebuilt in place each tick — no per-call alloc,
        // valid for the current frame (mirrors LiveAACacheSystem.m_coverage).
        private readonly List<StratumCoverage> m_coverage = new(3);

        private static readonly SocialStratum[] s_Strata =
        {
            SocialStratum.Poor, SocialStratum.Middle, SocialStratum.Wealthy
        };

        protected override void OnCreate()
        {
            base.OnCreate();

            m_HeroQuery = GetEntityQuery(ComponentType.ReadOnly<HeroDeploymentState>());
            m_TelemarathonQuery = GetEntityQuery(ComponentType.ReadOnly<TelemarathonRuntimeState>());
            m_BuckwheatQuery = GetEntityQuery(ComponentType.ReadOnly<BuckwheatSingleton>());
            m_BuckwheatAidedLookup = GetBufferLookup<BuckwheatAidedDistrictBuffer>(true);
            m_CenterQuery = GetEntityQuery(ComponentType.ReadOnly<PropagandaCenterState>());
            m_StatsQuery = GetEntityQuery(ComponentType.ReadOnly<CognitiveStatsState>());

            // Single owner of the cognitive coverage posture — expose it to the Core/CrossDomain
            // consumers without them importing Domains.Cognitive (Axiom 5). Fail-closed: the
            // null-object projects an empty list.
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<ICognitiveCoverageReader>(this);

            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<ICognitiveCoverageReader>(this);
            base.OnDestroy();
            Log.Info("Destroyed");
        }

        /// <summary>
        /// PLVS reconcile pass. Clears the pre-load city's coverage projection so a warm reload
        /// starts fail-closed ("no posture yet") instead of leaking the previous city's per-stratum
        /// covers into the pause-visible UI overlay and the raid gap-targeting. CS2 reuses system
        /// instances across load, so the managed list survives the load boundary and is not
        /// otherwise reset until the first throttle tick — which never arrives while a city loaded
        /// with pausedAfterLoading sits paused. Mirrors TelecomBroadcastCacheSystem.ValidateAfterLoad.
        /// </summary>
        public void ValidateAfterLoad() => m_coverage.Clear();

        protected override void OnThrottledUpdate() => RebuildCoverage();

        private void RebuildCoverage()
        {
            m_coverage.Clear();

            var cog = BalanceConfig.Current?.Cognitive;
            if (cog == null)
                return;

            // Active hero archetype. While an Arestovych trust-debt collapse window is open the
            // exposed lie loses its bite — the strong counter drops to the floor, same effective
            // value the infection resolver applies, so the posture the enemy gap-targets cannot
            // claim the countered type is covered when it is not. No hero → no collapse.
            var archetype = HeroArchetype.Voice;
            bool heroActive = false;
            bool arestovychSuppressed = false;
            if (m_HeroQuery.TryGetSingleton<HeroDeploymentState>(out var hero))
            {
                archetype = hero.Archetype;
                heroActive = hero.HeroStatus != HeroStatus.Inactive;
                arestovychSuppressed = GameTimeSystem.TryGetGameHours(out var nowGameHour)
                    && hero.IsArestovychDebtCollapsing(nowGameHour);
            }
            float effHeroStrong = StratumDefense.EffectiveHeroCounterStrong(cog.HeroCounterStrong, cog.HeroCounterFloor, arestovychSuppressed);

            // Active telemarathon (counts only while broadcasting and not in shock).
            bool telemarathonActive = m_TelemarathonQuery.TryGetSingleton<TelemarathonRuntimeState>(out var tele)
                && tele.IsActive && !tele.IsInShock;

            // Buckwheat covering a stratum (poor defence / wealthy backfire) — read the PRECISE aided-set
            // the infection path uses (at least one district under an active food-aid effect), NOT the
            // reserve>0 proxy. Reserve on hand with no active distribution protects no household, so the
            // posture must not claim coverage there — this keeps the gap-bias honest with infection.
            m_BuckwheatAidedLookup.Update(this);
            bool buckwheatActive = m_BuckwheatQuery.TryGetSingletonEntity<BuckwheatSingleton>(out var buckEntity)
                && m_BuckwheatAidedLookup.TryGetBuffer(buckEntity, out var aidedBuffer)
                && aidedBuffer.Length > 0;

            // Propaganda Center broadcast reach: the BROADCAST countermeasures (Telemarathon
            // + the hero speaker) only work where the Center reaches — telecom-delivered, capacity-limited.
            // Buckwheat is PHYSICAL aid (food parcels), NOT broadcast, so it is NOT gated by the Center.
            // No Center / offline → reach 0 → broadcast tools contribute nothing (the unlock gate).
            float reach = m_CenterQuery.TryGetSingleton<PropagandaCenterState>(out var center) ? center.CenterReach : 0f;

            // Per-type hero counter (city-wide RPS) — the gap the enemy targets. Scaled by Center reach:
            // uniform scaling preserves the relative gap (the least-covered type is unchanged), while a
            // 0 reach flattens all covers → no gap → the launcher falls back to a random dominant.
            float propagandaCover = reach * StratumDefense.HeroTypeEffectiveness(archetype, PsyOpsAttackType.Propaganda, heroActive, effHeroStrong, cog.HeroCounterFloor);
            float rumorCover = reach * StratumDefense.HeroTypeEffectiveness(archetype, PsyOpsAttackType.Rumor, heroActive, effHeroStrong, cog.HeroCounterFloor);
            float fakeVideoCover = reach * StratumDefense.HeroTypeEffectiveness(archetype, PsyOpsAttackType.FakeVideo, heroActive, effHeroStrong, cog.HeroCounterFloor);

            // Per-stratum telecom coverage fraction from the aggregator's read model (same domain).
            // Default 0 (fail-closed = "in the hole") until the first aggregation tick after load.
            var stats = m_StatsQuery.TryGetSingleton<CognitiveStatsState>(out var statsValue)
                ? statsValue
                : default;

            for (int i = 0; i < s_Strata.Length; i++)
            {
                var stratum = s_Strata[i];
                float coverage = 0f;
                if (telemarathonActive)
                    coverage += reach * StratumDefense.TelemarathonStratumDefense(stratum, cog.TelemarathonStratumStrong, cog.TelemarathonStratumFloor);
                if (buckwheatActive)
                    coverage += StratumDefense.BuckwheatStratumDefense(stratum, cog.BuckwheatStratumPoor, cog.BuckwheatStratumFloor, cog.BuckwheatWealthyBackfire);
                // Hero stratum tilt is a socio-economic side effect (capital flight / pressing the
                // poor), NOT a broadcast — ungated by Center reach, mirroring the infection path
                // (ExposureCalculator leaves heroDefenseTilt outside both the reach and in-coverage
                // gates) and physical Buckwheat above.
                coverage += StratumDefense.HeroStratumTilt(archetype, stratum, heroActive, cog.PatriotWealthyBackfire, cog.PatriotPoorPress);

                // Phase 10 telecom-signal coverage fraction + the per-stratum household count, the
                // player's allocation weight for the Phase 13A capacity combine, and the Phase 14
                // enemy infection (the psy-exodus pressure signal). if/else-if with an explicit final
                // else (matches the StratumDefense convention) — Unknown/unclassified folds to 0
                // without a silent-discard switch arm.
                float signalFraction, stratumHouseholds, allocWeight, stratumInfection;
                if (stratum == SocialStratum.Poor)
                {
                    signalFraction = stats.PoorCoverageFraction;
                    stratumHouseholds = stats.PoorHouseholds;
                    allocWeight = center.AllocWeightPoor;
                    stratumInfection = stats.PoorAvgInfection;
                }
                else if (stratum == SocialStratum.Middle)
                {
                    signalFraction = stats.MiddleCoverageFraction;
                    stratumHouseholds = stats.MiddleHouseholds;
                    allocWeight = center.AllocWeightMiddle;
                    stratumInfection = stats.MiddleAvgInfection;
                }
                else if (stratum == SocialStratum.Wealthy)
                {
                    signalFraction = stats.WealthyCoverageFraction;
                    stratumHouseholds = stats.WealthyHouseholds;
                    allocWeight = center.AllocWeightWealthy;
                    stratumInfection = stats.WealthyAvgInfection;
                }
                else
                {
                    signalFraction = 0f; stratumHouseholds = 0f; allocWeight = 0f; stratumInfection = 0f; // Unknown → in the hole
                }

                // Phase 13A — capacity in HOUSEHOLDS: the Center holds only weight*ceiling households per
                // stratum, so a bigger city strains a fixed ceiling (the scale-dependence). Combined with
                // the telecom signal as the TIGHTER of the two — you need BOTH signal reach AND capacity
                // budget. No Center → ceiling 0 → capacityFraction 0 → no counter-coverage. The Phase-10
                // enemy floor is applied downstream (the resolver), untouched here.
                float capacityFraction = math.min(1f, (allocWeight * center.CoverageCapacityHouseholds) / math.max(1f, stratumHouseholds));
                float coverageFraction = math.min(signalFraction, capacityFraction);

                m_coverage.Add(new StratumCoverage(stratum, coverage, propagandaCover, rumorCover, fakeVideoCover, coverageFraction, stratumInfection, stratumHouseholds));
            }
        }

        /// <summary>
        /// ICognitiveCoverageReader — per-stratum posture rebuilt in lockstep with the throttle
        /// (no per-call alloc; valid for the current frame). Empty until the first tick after load.
        /// </summary>
        public IReadOnlyList<StratumCoverage> GetCoverage() => m_coverage;
    }
}
