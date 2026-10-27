using Game;
using Game.Areas;
using CivicSurvival.Core.Features.Wellbeing;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Spotters;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.PsyImpact;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Cognitive.Core.Jobs;
using CivicSurvival.Core.Components.Domain.NeighborEnvy;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Central resolver for all mental health effects.
    /// SINGLE WRITER to HouseholdPsyState - eliminates race conditions.
    ///
    /// Logic Composition Pattern:
    /// - Calls 6 calculators in sequence via ResolveHouseholdPsyJob
    /// - BlackoutCalculator: power status → pressure, hours
    /// - EnvyCalculator: neighbor power → pressure
    /// - ExposureCalculator: district/hero/telemarathon → exposure
    /// - ResistanceCalculator: education → resistance (throttled)
    /// - CognitiveCalculator: exposure → infection
    /// - TraumaCalculator: pressure → trauma, inertia
    ///
    /// Execution order: AFTER NeighborEnvySystem (needs EnvyAffected tags)
    ///
    /// Perf: Coverage + IPSO maps built on worker thread via BuildMentalHealthLookupsJob.
    /// ECS chains: WriteSingletonJob → BuildLookupsJob → ResolveHouseholdPsyJob (all workers).
    /// Main thread cost: ~0ms for map builds.
    /// </summary>
#pragma warning disable CIVIC076 // Runs every frame by design (resolver system, checks internally)
    [ActIndependent]
    public partial class MentalHealthResolverSystem : CivicSystemBase, IResettable, IPostLoadValidation
#pragma warning restore CIVIC076
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("MentalHealthResolverSystem");

        // Simulation-tick based throttle (FPS-independent, like vanilla CitizenHappinessSystem)
        private const int SIM_TICK_INTERVAL = 16; // fire every 16 sim ticks
        private const int SLOT_COUNT = 4;         // 4 groups → full cycle = 64 ticks (~0.75s at 85 ticks/s)
        private const byte ALL_IMPACT_SLOT_MASK = (byte)((1 << SLOT_COUNT) - 1);
        // Safety valve: an impact undelivered after this many empty-slot fires is dropped. Unreachable
        // with a live population (a slot only stays empty when the whole city has no households); the
        // drop protects the pending list from unbounded growth on a truly empty world. Accepted as-is.
        private const byte MAX_EMPTY_RECIPIENT_DEFERRALS = 16;
        private const int DISTRICT_LOOKUP_CAPACITY = 10_000;
        private SimulationSystem m_SimulationSystem = null!;
        private uint m_LastSlot = uint.MaxValue; // force fire on first tick
        // Monotonic sim-dt clock: accumulates SystemAPI.Time.DeltaTime once per sim tick, never reset.
        // Same timebase the resolve job has always integrated over (rate constants are tuned against it).
        // double: a float accumulator loses sub-frame increments after hours of session uptime.
        // False positives (CIVIC150/229/305): only DELTAS against m_LastFireClockSeconds matter, never
        // the absolute value — persisting or resetting the clock is meaningless by design. Every reset
        // leg (ClearTransientState) instead re-arms the per-slot stamps to the -1 sentinel, so a value
        // carried across load/reset can never surface as cross-city dt (MAX_SLOT_DT bounds the rest).
#pragma warning disable CIVIC150, CIVIC229, CIVIC305
        private double m_SimClockSeconds;
#pragma warning restore CIVIC150, CIVIC229, CIVIC305
        // Per-slot clock stamp of the last fire that actually scheduled the resolve job.
        // A household in slot S is touched once per SLOT_COUNT fires (full 64-tick cycle), so its dt
        // must span that whole cycle. Integrating the 16-tick inter-fire interval instead made every
        // rate-integrated field (BlackoutHours, Trauma, InfectionLevel, RecoveryInertia) evolve
        // SLOT_COUNT× slower than game time. Absolute clock deltas also absorb skipped fires
        // (config not loaded, empty city) without losing slot time.
        // -1 sentinel = slot not resolved yet this session (its first fire integrates dt = 0, no jump).
        // Managed state, not persisted: after a load into a reused system instance the first fire per
        // slot is bounded by MAX_SLOT_DT, so stale stamps cannot produce a spike.
        private readonly double[] m_LastFireClockSeconds = { -1d, -1d, -1d, -1d }; // length == SLOT_COUNT
        // Lag/load spike protection: clamp per-slot dt to (full slot cycle seconds × max_speed × safety)
        private const float MAX_SLOT_DT = (SLOT_COUNT * SIM_TICK_INTERVAL / 85f) * 3f * 2f;
#pragma warning disable CIVIC031, S2696 // Cross-system sync flag — read by PsyTransientResetSystem
        private static volatile bool s_DidFire;
        public static bool DidFire => s_DidFire;

        // PsySlot rotation: 4 slots, process one per fire (sim-tick driven)
        // CurrentSlot lives on PsySlot (Core) so Core systems can read without Domain dependency.
        /// <summary>Slot index processed on last fire frame. Delegates to PsySlot.CurrentSlot.</summary>
        public static int CurrentSlot => PsySlot.CurrentSlot;
#pragma warning restore CIVIC031, S2696

        private EntityQuery m_HouseholdQuery;
        private EntityQuery m_SpotterCmQuery;

        // Untagged housed households — no sidecar yet. Creation itself lives in
        // PsySidecarCreateSystem (write-fence split); MHR only consults this query for the
        // impact-discard decision (no recipients now and none coming).
        private EntityQuery m_UntaggedHouseholdQuery;
        private EntityQuery m_BackupPowerStateQuery;

        // Cached lookups (PERF: create in OnCreate, update in OnUpdate)
        private ComponentLookup<PropertyRenter> m_PropertyRenterLookup;
        private ComponentLookup<ElectricityConsumer> m_ElectricityLookup;
        private ComponentLookup<CurrentDistrict> m_DistrictLookup;
        private ComponentLookup<EnvyAffected> m_EnvyAffectedLookup;
        private BufferLookup<HouseholdCitizen> m_HouseholdCitizenLookup;
        private ComponentLookup<Citizen> m_CitizenLookup;

        // Social stratum classification (read-only vanilla wealth)
        private ComponentLookup<Household> m_HouseholdLookup;
        private BufferLookup<Game.Economy.Resources> m_ResourcesLookup;
        private EntityQuery m_HappinessParamQuery;

        // Vanilla telecom coverage grid (read-only) + building position lookup.
        // The 128² signal map is handed to the parallel resolver via GetMap(readOnly:true) + AddReader
        // (no .Complete() — no new sync point; the map refreshes every 4096 ticks, near-static).
        private TelecomCoverageSystem m_TelecomCoverage = null!;
        private ComponentLookup<Game.Objects.Transform> m_BuildingTransformLookup;

        private struct PendingImpactEntry
        {
            public ImpactDistrictEntry Impact;
            public byte PendingSlotMask;
            public byte EmptyRecipientDeferrals;
        }

        // Impact snapshots are slot-addressed: every impact is delivered exactly once to each PsySlot.
        // The pending list is main-thread only; each fired slot gets an owned TempJob snapshot.
        [NonEntityIndex] private NativeList<PendingImpactEntry> m_PendingImpacts;

        // Impact pressure buffer (snapshot from ImpactPressureSingleton)
        private EntityQuery m_ImpactSingletonQuery;

        // Live discrete PSYOPS attacks (PsyOpsLaunchSystem owns their lifecycle). A LANDED attack
        // is standing pressure, not an event: each fire reads the landed set fresh and the resolve
        // job integrates it over the slot dt — unlike the pending-impact events above, nothing is
        // drained or delivered-once. Persisted entities → the pressure self-restores after load.
        private EntityQuery m_LiveRaidQuery;

        // Worker-thread buffer lookups (read-only — no main-thread sync point)
        private BufferLookup<DistrictBatteryCoverage> m_CoverageLookup;
        private BufferLookup<IPSODistrictExposureBuffer> m_IPSOExposureLookup;
        private BufferLookup<InternetDisabledBuffer> m_InternetLookup;
        private BufferLookup<BuckwheatAidedDistrictBuffer> m_BuckwheatAidedLookup;

        // Persistent containers (reused via Clear() in BuildJob — no per-frame alloc/dispose)
        // Keys are district indices (stable spatial index)
        [NonEntityIndex] private NativeHashMap<int, DistrictBatteryCoverage> m_CoverageMap;
        [NonEntityIndex] private NativeHashMap<int, float> m_IpsoMap;
        [NonEntityIndex] private NativeParallelHashSet<int> m_InternetDisabledSet;
        [NonEntityIndex] private NativeParallelHashSet<int> m_BuckwheatAidedSet;

        protected override string ProfileName => "MentalHealth.OnUpdate";

        protected override void OnCreate()
        {
            base.OnCreate();

            // PsySlot MUST be in query definition — without it, AddSharedComponentFilter
            // silently matches 0 chunks and ScheduleParallel processes 0 entities.
            m_HouseholdQuery = GetEntityQuery(
                ComponentType.ReadWrite<HouseholdPsyState>(),
                ComponentType.ReadOnly<PsyHouseholdLink>(),
                ComponentType.ReadOnly<PsySlot>()
            );

            m_SpotterCmQuery = GetEntityQuery(
                ComponentType.ReadOnly<SpotterCountermeasuresState>()
            );

            // Untagged housed households — impact-discard decision only (creation lives in
            // PsySidecarCreateSystem). No RW lookup of PsyHouseholdLink is registered here:
            // that registration stamped the resolve chain as the link's write-fence every tick
            // and poisoned every link reader (write-fence split, 2026-07-21).
            m_UntaggedHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<HasPsyState>(),
                ComponentType.Exclude<Deleted>()
            );
            m_BackupPowerStateQuery = GetEntityQuery(ComponentType.ReadOnly<BackupPowerStateSingleton>());

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            // Cache lookups
            m_PropertyRenterLookup = GetComponentLookup<PropertyRenter>(true);
            m_ElectricityLookup = GetComponentLookup<ElectricityConsumer>(true);
            m_DistrictLookup = GetComponentLookup<CurrentDistrict>(true);
            m_EnvyAffectedLookup = GetComponentLookup<EnvyAffected>(true);
            m_HouseholdCitizenLookup = GetBufferLookup<HouseholdCitizen>(true);
            m_CitizenLookup = GetComponentLookup<Citizen>(true);

            // Social stratum: vanilla household wealth (Household.m_Resources + Money buffer)
            m_HouseholdLookup = GetComponentLookup<Household>(true);
            m_ResourcesLookup = GetBufferLookup<Game.Economy.Resources>(true);
            m_HappinessParamQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Prefabs.CitizenHappinessParameterData>());

            // Vanilla telecom coverage system + building transform lookup for the per-household
            // "in coverage" sample. Managed system reference resolved once; the map itself is fetched
            // per-fire in OnUpdateImpl (read-only handshake, no .Complete()).
            m_TelecomCoverage = World.GetOrCreateSystemManaged<TelecomCoverageSystem>();
            m_BuildingTransformLookup = GetComponentLookup<Game.Objects.Transform>(true);

            // Read-only buffer lookups for worker-thread map building
            m_CoverageLookup = GetBufferLookup<DistrictBatteryCoverage>(true);
            m_IPSOExposureLookup = GetBufferLookup<IPSODistrictExposureBuffer>(true);
            m_InternetLookup = GetBufferLookup<InternetDisabledBuffer>(true);
            m_BuckwheatAidedLookup = GetBufferLookup<BuckwheatAidedDistrictBuffer>(true);

            // Fixed high-water capacity avoids main-thread resize/sync before worker lookup builds.
            m_CoverageMap = new NativeHashMap<int, DistrictBatteryCoverage>(DISTRICT_LOOKUP_CAPACITY, Allocator.Persistent);
            m_IpsoMap = new NativeHashMap<int, float>(DISTRICT_LOOKUP_CAPACITY, Allocator.Persistent);
            m_PendingImpacts = new NativeList<PendingImpactEntry>(64, Allocator.Persistent);
            m_InternetDisabledSet = new NativeParallelHashSet<int>(DISTRICT_LOOKUP_CAPACITY, Allocator.Persistent);
            m_BuckwheatAidedSet = new NativeParallelHashSet<int>(DISTRICT_LOOKUP_CAPACITY, Allocator.Persistent);

            m_ImpactSingletonQuery = GetEntityQuery(ComponentType.ReadOnly<ImpactPressureSingleton>());
            m_LiveRaidQuery = GetEntityQuery(
                ComponentType.ReadOnly<PsyOpsAttack>(),
                ComponentType.Exclude<Deleted>());

            // Pressure pipeline registration: MHR is consumer only (reads blackout/envy/impact for wellbeing)
            PressureRegistry.RegisterConsumer(PressureChannel.Blackout, nameof(MentalHealthResolverSystem));
            PressureRegistry.RegisterConsumer(PressureChannel.Envy, nameof(MentalHealthResolverSystem));
            PressureRegistry.RegisterConsumer(PressureChannel.Impact, nameof(MentalHealthResolverSystem));

            Log.Info("Created (Logic Composition pattern, all lookups on worker)");
        }

        private void DrainImpactProducerBuffer(bool discard)
        {
            if (!m_ImpactSingletonQuery.TryGetSingletonEntity<ImpactPressureSingleton>(out var impactEntity)
                || !EntityManager.HasBuffer<ImpactDistrictEntry>(impactEntity))
                return;

            var impactBuffer = SystemAPI.GetBuffer<ImpactDistrictEntry>(impactEntity);
            if (impactBuffer.Length == 0)
                return;

            if (!discard)
            {
                for (int i = 0; i < impactBuffer.Length; i++)
                {
                    m_PendingImpacts.Add(new PendingImpactEntry
                    {
                        Impact = impactBuffer[i],
                        PendingSlotMask = ALL_IMPACT_SLOT_MASK,
                        EmptyRecipientDeferrals = 0
                    });
                }
            }

            impactBuffer.Clear();
        }

        private NativeArray<ImpactDistrictEntry> BuildImpactSnapshotForSlot(int slot, int recipientCount, Allocator allocator)
        {
            byte slotBit = (byte)(1 << slot);
            if (recipientCount <= 0)
            {
                ExpireImpactSnapshotForEmptySlot(slot, slotBit);
                return default;
            }

            int impactCount = 0;
            for (int i = 0; i < m_PendingImpacts.Length; i++)
                if ((m_PendingImpacts[i].PendingSlotMask & slotBit) != 0)
                    impactCount++;

            if (impactCount == 0)
                return default;

            var snapshot = new NativeArray<ImpactDistrictEntry>(impactCount, allocator, NativeArrayOptions.UninitializedMemory);
            int writeIndex = 0;
            for (int i = m_PendingImpacts.Length - 1; i >= 0; i--)
            {
                var pending = m_PendingImpacts[i];
                if ((pending.PendingSlotMask & slotBit) != 0)
                {
                    snapshot[writeIndex++] = pending.Impact;
                    pending.PendingSlotMask = (byte)(pending.PendingSlotMask & ~slotBit);
                }

                if (pending.PendingSlotMask == 0)
                    m_PendingImpacts.RemoveAtSwapBack(i);
                else
                    m_PendingImpacts[i] = pending;
            }

            return snapshot;
        }

        /// <summary>
        /// One entry per landed attack per pressed stratum (primary + rumor-spread secondary),
        /// carrying the per-stratum effective pressure (vuln bias + rumor confirmation). Tiny
        /// (raids cap at 4 contacts) and rebuilt every fire, so expiry/spread/interception all
        /// reflect within one fire. The RefRO query fences only against PsyOpsAttack writers —
        /// the launcher's main-thread lifecycle pass; no job ever writes the component.
        /// </summary>
        private NativeArray<ImpactDistrictEntry> BuildLiveRaidPressures(CognitiveConfig cwCfg, Allocator allocator)
        {
            var pressures = new NativeList<ImpactDistrictEntry>(8, Allocator.Temp);
            foreach (var atkRef in SystemAPI.Query<RefRO<PsyOpsAttack>>().WithNone<Deleted>())
            {
                ref readonly var atk = ref atkRef.ValueRO;
                if (atk.Phase != PsyOpsAttackPhase.Landed)
                    continue;
                pressures.Add(ImpactDistrictEntry.Create(
                    DistrictUtils.UNZONED_AREA_INDEX,
                    PsyOpsRaidPressure.EffectiveIntensity(in atk, atk.Target, cwCfg),
                    atk.Target, atk.Type));
                if (atk.HasSpread && atk.SecondaryTarget != SocialStratum.Unknown)
                    pressures.Add(ImpactDistrictEntry.Create(
                        DistrictUtils.UNZONED_AREA_INDEX,
                        PsyOpsRaidPressure.EffectiveIntensity(in atk, atk.SecondaryTarget, cwCfg),
                        atk.SecondaryTarget, atk.Type));
            }

            if (pressures.Length == 0)
            {
                pressures.Dispose();
                return default;
            }
            var result = pressures.ToArray(allocator);
            pressures.Dispose();
            return result;
        }

        private void ExpireImpactSnapshotForEmptySlot(int slot, byte slotBit)
        {
            for (int i = m_PendingImpacts.Length - 1; i >= 0; i--)
            {
                var pending = m_PendingImpacts[i];
                if ((pending.PendingSlotMask & slotBit) == 0)
                    continue;

                pending.EmptyRecipientDeferrals++;
                if (pending.EmptyRecipientDeferrals >= MAX_EMPTY_RECIPIENT_DEFERRALS)
                {
                    pending.PendingSlotMask = (byte)(pending.PendingSlotMask & ~slotBit);
                    Log.Warn($"Dropped impact pressure delivery for empty PsySlot {slot} after {MAX_EMPTY_RECIPIENT_DEFERRALS} deferrals");
                }

                if (pending.PendingSlotMask == 0)
                    m_PendingImpacts.RemoveAtSwapBack(i);
                else
                    m_PendingImpacts[i] = pending;
            }
        }

        [CompletesDependency("MentalHealthResolverSystem.OnUpdateImpl: impact-recipient CalculateEntityCount runs only when PendingImpacts non-empty (rare), drives BuildImpactSnapshotForSlot capacity. Not a [HotPathSystem]; mid-frequency resolver, throttled by the sim-tick slot cycle")]
        protected override void OnUpdateImpl()
        {
            // Advance the monotonic sim-dt clock every tick (before any early return)
            float frameDt = SystemAPI.Time.DeltaTime;
            // False positive (CIVIC226): a double accumulating seconds cannot meaningfully overflow
            // (precision holds to ~2^53 s); consumers take bounded deltas clamped by MAX_SLOT_DT.
#pragma warning disable CIVIC226
            m_SimClockSeconds += frameDt;
#pragma warning restore CIVIC226

            // Sim-tick based throttle (FPS-independent, like vanilla CitizenHappinessSystem)
            // GetUpdateFrameWithInterval: (frameIndex / interval) & (slotCount - 1)
            uint currentSlot = SimulationUtils.GetUpdateFrameWithInterval(
                m_SimulationSystem.frameIndex, (uint)SIM_TICK_INTERVAL, SLOT_COUNT);
#pragma warning disable S2696 // Cross-system sync flag
            if (currentSlot == m_LastSlot)
            {
                s_DidFire = false;
                return;
            }
            m_LastSlot = currentSlot;
            // DidFire flips true only when the resolve job is actually scheduled (below).
            // Early-outs (no households, config not loaded) must not signal "transients written"
            // to PsyTransientResetSystem / other DidFire consumers.
            // PsySlot.CurrentSlot must advance here (WRS reads it before any early return).
            s_DidFire = false;
            PsySlot.CurrentSlot = (int)currentSlot;
            m_HouseholdQuery.ResetFilter();
            m_HouseholdQuery.AddSharedComponentFilter(new PsySlot { SlotIndex = (int)currentSlot });
#pragma warning restore S2696

            // One-time validation: all systems have registered by first fire frame
            PressureRegistry.Validate();

            bool noPsyStateAndNoHouseholds = m_HouseholdQuery.IsEmptyIgnoreFilter && m_UntaggedHouseholdQuery.IsEmptyIgnoreFilter;
            DrainImpactProducerBuffer(discard: noPsyStateAndNoHouseholds);

            // Sidecar creation lives in PsySidecarCreateSystem (write-fence split, registered
            // after MHR); with no sidecars there is nothing to resolve this tick.
            if (m_HouseholdQuery.IsEmptyIgnoreFilter)
                return;

            // Update lookups
            using (PerformanceProfiler.Measure("SP:MH.LookupSync"))
            {
                m_PropertyRenterLookup.Update(this);
                m_ElectricityLookup.Update(this);
                m_DistrictLookup.Update(this);
                m_EnvyAffectedLookup.Update(this);
                m_HouseholdCitizenLookup.Update(this);
                m_CitizenLookup.Update(this);
                m_HouseholdLookup.Update(this);
                m_ResourcesLookup.Update(this);
                m_BuildingTransformLookup.Update(this);
            }

            // Worker-thread buffer lookups (read-only update — instant, no sync)
            m_CoverageLookup.Update(this);
            m_IPSOExposureLookup.Update(this);
            m_InternetLookup.Update(this);
            m_BuckwheatAidedLookup.Update(this);

            // FIX W2-H1: Use TotalGameHours for persisted Resistance_LastUpdateTime
            // ElapsedTime resets on load → persisted timestamp blocks recalc for minutes
            bool forceResistanceRecalculate = !GameTimeSystem.TryGetTotalGameSeconds(out var currentSeconds);
            float currentTime = forceResistanceRecalculate
                ? 0f
                : (float)currentSeconds;

            // Config
            var balance = BalanceConfig.Current;
            if (balance == null) return;

            // Per-slot dt: sim time elapsed since THIS slot was last resolved (full 64-tick cycle),
            // not the 16-tick interval between fires of neighbouring slots. Clamped against
            // lag spikes and stale post-load stamps; monotonic clock ⇒ delta is never negative.
            float dt;
            double lastFire = m_LastFireClockSeconds[currentSlot];
            if (lastFire >= 0d)
            {
                dt = (float)(m_SimClockSeconds - lastFire);
                if (dt > MAX_SLOT_DT)
                    dt = MAX_SLOT_DT;
            }
            else
            {
                dt = 0f; // first resolve of this slot this session — nothing to integrate yet
            }
            m_LastFireClockSeconds[currentSlot] = m_SimClockSeconds; // stamp only when the job is scheduled
#pragma warning disable S2696 // Cross-system sync flag (see s_DidFire declaration)
            s_DidFire = true; // committed: resolve job is scheduled below, transients will be written
#pragma warning restore S2696
            float dtHours = GameRate.HoursDelta(dt);
            var cwCfg = balance.Cognitive;

            // ════════════════════════════════════════════════════════════════
            // IMPACT PRESSURE: each impact is delivered once to each PsySlot.
            // Rapid impacts append to pending state instead of replacing the active snapshot.
            // ════════════════════════════════════════════════════════════════
            NativeArray<ImpactDistrictEntry> recentImpacts = default;
            if (m_PendingImpacts.Length > 0)
            {
                int impactRecipientCount = m_HouseholdQuery.CalculateEntityCount();
                recentImpacts = BuildImpactSnapshotForSlot((int)currentSlot, impactRecipientCount, Allocator.TempJob);
            }

            // ════════════════════════════════════════════════════════════════
            // LIVE RAID PRESSURE: landed discrete PSYOPS attacks are standing pressure for their
            // whole landed window (height × duration is the balance profile). Read fresh every
            // fire; the resolve job integrates over the slot dt — never delivered-once.
            // ════════════════════════════════════════════════════════════════
            NativeArray<ImpactDistrictEntry> liveRaidPressures = default;
            if (!m_LiveRaidQuery.IsEmptyIgnoreFilter)
                liveRaidPressures = BuildLiveRaidPressures(cwCfg, Allocator.TempJob);

            // recentImpacts is a TempJob snapshot consumed by the resolve job and released by the
            // deferred dispose after scheduling. Own its lifetime across the whole schedule path so an
            // exception between here and that dispose cannot leak the allocation (mirrors the try/finally
            // that already guards the bulk-create TempJob arrays above). The block body keeps the file's
            // existing flat indentation for this scope.
            try
            {

            // Get singletons
            var backupPolicy = BackupPolicy.Reserve;
            var heroStatus = HeroStatus.Inactive;
            var heroArchetype = HeroArchetype.Voice;
            var heroDeployState = default(HeroDeploymentState); // DebtCollapseEndHour 0 → no collapse window
            bool isGlobalBlackout = false;
            float infectionRate = cwCfg.InfectionRateBase;
            float recoveryRate = cwCfg.RecoveryRateBase;
            bool telemarathonActive = false;
            bool telemarathonInShock = false;
            float telemarathonTrust = 0f;
            float telemarathonEffectiveness = 0f;
            float telemarathonModeBonus = 1f;
            float alarmistStressRate = 0f;
            Unity.Mathematics.int4 wealthThresholds = default;
            bool hasWealthThresholds = false;

            using (PerformanceProfiler.Measure("MentalHealth.Singletons"))
            {
#pragma warning disable CIVIC211 // Gameplay dependency: mental health affected by backup power policy
                backupPolicy = (m_BackupPowerStateQuery.TryGetSingleton<BackupPowerStateSingleton>(out var bp)
                        ? bp : BackupPowerStateSingleton.Default)
                    .Policy;
#pragma warning restore CIVIC211

                if (SystemAPI.TryGetSingleton<CognitiveState>(out var cwState))
                {
                    var hs = SystemAPI.TryGetSingleton<HeroDeploymentState>(out var heroState)
                        ? heroState : HeroDeploymentState.Default;
                    heroStatus = hs.HeroStatus;
                    heroArchetype = hs.Archetype;
                    heroDeployState = hs;
                    infectionRate = CognitiveRates.EffectiveInfectionRate(cwState, hs);
                    recoveryRate = CognitiveRates.EffectiveRecoveryRate(cwState, hs);
                    // T4-7 FIX: Pass global Internet Blackout mode to ExposureCalculator
                    isGlobalBlackout = cwState.InternetMode == GlobalInternetMode.Blackout;
                }

                if (SystemAPI.TryGetSingleton<TelemarathonRuntimeState>(out var telemarathon))
                {
                    telemarathonActive = telemarathon.IsActive;
                    telemarathonInShock = telemarathon.IsInShock;
                    telemarathonTrust = telemarathon.Trust;
                    telemarathonEffectiveness = telemarathon.EffectivenessMult;

                    if (telemarathon.IsActive && !telemarathon.IsInShock)
                    {
                        telemarathonModeBonus = telemarathon.Mode switch
                        {
                            NarrativeMode.Realistic => cwCfg.ModeBonusRealistic,
                            NarrativeMode.Alarmist => cwCfg.ModeBonusAlarmist,
                            NarrativeMode.Soothing => cwCfg.ModeBonusSoothing,
                            _ => 1f
                        };
                        alarmistStressRate = telemarathon.StressRate * telemarathon.EffectivenessMult;
                    }
                }

                // Vanilla wealth thresholds for social-stratum classification (read-only prefab singleton)
                if (m_HappinessParamQuery.TryGetSingleton<Game.Prefabs.CitizenHappinessParameterData>(out var happinessParams))
                {
                    wealthThresholds = happinessParams.m_WealthyMoneyAmount;
                    hasWealthThresholds = true;
                }
            }

            // ════════════════════════════════════════════════════════════════
            // WORKER-THREAD MAP BUILDS (all 3 lookups on worker — zero main-thread sync)
            // ECS chains: WriteSingletonJob/WriteIPSOStateJob → BuildLookupsJob → ResolveJob
            // ════════════════════════════════════════════════════════════════

            // Check entity availability (read-only — no sync point)
            bool hasIpsoData = false;
            var ipsoEntity = Entity.Null;
            if (SystemAPI.TryGetSingletonEntity<IPSOState>(out var ipsoEntityOut))
            {
                ipsoEntity = ipsoEntityOut;
                if (SystemAPI.TryGetSingleton<IPSOState>(out var ipso) && ipso.IsActive)
                    hasIpsoData = true;
            }

            var coverageEntity = SystemAPI.TryGetSingletonEntity<BackupPowerStateSingleton>(out var coverageEntityOut)
                ? coverageEntityOut
                : Entity.Null;
            bool hasInternetEntity = m_SpotterCmQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var internetEntity);
            bool hasBuckwheatEntity = SystemAPI.TryGetSingletonEntity<BuckwheatSingleton>(out var buckwheatEntity);

            // Persistent containers (cleared inside BuildJob.Execute)
            bool hasInternetData = hasInternetEntity; // job will fill the set; if entity missing, set stays empty

            // Schedule BuildMentalHealthLookupsJob (IJob — runs on worker, waits for buffer writers)
            JobHandle buildHandle;
            using (PerformanceProfiler.Measure("MentalHealth.ScheduleBuild"))
            {
                var buildJob = new BuildMentalHealthLookupsJob
                {
                    CoverageLookup = m_CoverageLookup,
                    CoverageEntity = coverageEntity,
                    IPSOLookup = m_IPSOExposureLookup,
                    IPSOEntity = ipsoEntity,
                    InternetLookup = m_InternetLookup,
                    InternetEntity = internetEntity,
                    BuckwheatLookup = m_BuckwheatAidedLookup,
                    BuckwheatEntity = hasBuckwheatEntity ? buckwheatEntity : Entity.Null,
                    CoverageMap = m_CoverageMap,
                    IPSOExposureMap = m_IpsoMap,
                    InternetDisabledSet = m_InternetDisabledSet,
                    BuckwheatAidedSet = m_BuckwheatAidedSet
                };
                if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre BuildMentalHealthLookupsJob.Schedule coverageEntity={coverageEntity} ipsoEntity={ipsoEntity} internetEntity={internetEntity} hasInternetData={hasInternetData} hasIpsoData={hasIpsoData} coverageMap={m_CoverageMap.IsCreated}/capacity={m_CoverageMap.Capacity} ipsoMap={m_IpsoMap.IsCreated}/capacity={m_IpsoMap.Capacity} internetSet={m_InternetDisabledSet.IsCreated}/capacity={m_InternetDisabledSet.Capacity}");
                buildHandle = buildJob.Schedule(Dependency);
                if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                    CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post BuildMentalHealthLookupsJob.Schedule coverageEntity={coverageEntity} ipsoEntity={ipsoEntity} internetEntity={internetEntity} hasInternetData={hasInternetData} hasIpsoData={hasIpsoData} coverageMap={m_CoverageMap.IsCreated}/capacity={m_CoverageMap.Capacity} ipsoMap={m_IpsoMap.IsCreated}/capacity={m_IpsoMap.Capacity} internetSet={m_InternetDisabledSet.IsCreated}/capacity={m_InternetDisabledSet.Capacity}");
                // FIX H41: Defensive — keep Dependency in sync with buildHandle.
                // Chain is buildHandle → resolveJob → Dependency, but if resolveJob
                // scheduling is skipped, buildHandle would be orphaned.
                Dependency = buildHandle;
            }

            var bpCfg = balance.BackupPower;

            // While an Arestovych trust-debt collapse window is open the exposed lie
            // loses its bite — his counter effectiveness drops to the floor. Resolve the suppression
            // here (this path's only game-hour read) via the shared predicate/helper and pass the
            // EFFECTIVE strong value to the job, so StratumDefense stays a pure Burst function with
            // no time/singleton dependency. Time unavailable → suppressed=false (short-circuit).
            bool arestovychSuppressed = GameTimeSystem.TryGetGameHours(out var nowGameHour)
                && heroDeployState.IsArestovychDebtCollapsing(nowGameHour);
            float effectiveHeroCounterStrong = StratumDefense.EffectiveHeroCounterStrong(cwCfg.HeroCounterStrong, cwCfg.HeroCounterFloor, arestovychSuppressed);

            // ════════════════════════════════════════════════════════════════
            // RESOLVE JOB (parallel, depends on buildJob)
            // ════════════════════════════════════════════════════════════════

            using (PerformanceProfiler.Measure("MentalHealth.Schedule"))
            {
            // Read-only handle on the vanilla telecom signal grid (128²). GetMap(readOnly:true)
            // returns the map + the writer dependency; we fold that into the resolve deps and AddReader
            // the resulting handle below — no .Complete(), so no new main-thread sync point.
            var telecomSignal = m_TelecomCoverage.GetMap(readOnly: true, out var telecomDeps);
            // Gate on the EXACT grid size the job indexes into (cellIdx = x + GRID*y assumes a GRID
            // stride): a larger vanilla grid would pass a `>=` gate but be sampled at the wrong stride,
            // silently reading the wrong cell. `==` makes the feature fail-closed on ANY resolution
            // change (InCoverage=false everywhere) instead of returning wrong coverage — matching the
            // "exact size" contract the job's TELECOM_GRID_SIZE comment promises.
            bool hasTelecomData = telecomSignal.IsCreated
                && telecomSignal.Length == ResolveHouseholdPsyJob.TELECOM_GRID_SIZE * ResolveHouseholdPsyJob.TELECOM_GRID_SIZE;

            var resolveJob = new ResolveHouseholdPsyJob
            {
                // Lookups
                PropertyRenterLookup = m_PropertyRenterLookup,
                ElectricityLookup = m_ElectricityLookup,
                DistrictLookup = m_DistrictLookup,
                EnvyAffectedLookup = m_EnvyAffectedLookup,
                HouseholdCitizenLookup = m_HouseholdCitizenLookup,
                CitizenLookup = m_CitizenLookup,

                // Social stratum classification (read-only vanilla wealth)
                HouseholdLookup = m_HouseholdLookup,
                ResourcesLookup = m_ResourcesLookup,
                WealthThresholds = wealthThresholds,
                HasWealthThresholds = hasWealthThresholds,

                // Telecom coverage sample (building position → vanilla signal grid)
                BuildingTransformLookup = m_BuildingTransformLookup,
                TelecomSignal = telecomSignal,
                TelecomThreshold = cwCfg.CognitiveDefenseSignalThreshold,
                HasTelecomData = hasTelecomData,

                // Internet disabled
                InternetDisabledDistricts = m_InternetDisabledSet,
                HasInternetData = hasInternetData,

                // Buckwheat aided districts (Buckwheat per-stratum infection effect)
                BuckwheatAidedDistricts = m_BuckwheatAidedSet,
                HasBuckwheatData = hasBuckwheatEntity,

                // IPSO per-district exposure (built by worker job)
                IPSODistrictExposure = m_IpsoMap,
                HasIpsoData = hasIpsoData,

                // T4-7 FIX: Global Internet Blackout mode
                IsGlobalBlackout = isGlobalBlackout,

                // Singletons
                BackupPolicy = backupPolicy,
                HeroStatus = heroStatus,
                Archetype = heroArchetype,

                // Three-layer battery coverage (built by worker job)
                DistrictCoverageMap = m_CoverageMap,
                MitigationWeightHospital = bpCfg.MitigationWeightHospital,
                MitigationWeightSchool = bpCfg.MitigationWeightSchool,
                MitigationWeightPrivate = bpCfg.MitigationWeightPrivate,
                MitigationMin = bpCfg.MitigationMin,

                // Telemarathon
                TelemarathonActive = telemarathonActive,
                TelemarathonInShock = telemarathonInShock,
                TelemarathonTrust = telemarathonTrust,
                TelemarathonEffectiveness = telemarathonEffectiveness,
                TelemarathonModeBonus = telemarathonModeBonus,
                AlarmistStressRate = alarmistStressRate,

                // Cognitive config
                EnemyInternetWeight = cwCfg.EnemyInternetWeight,
                EnemyIpsoWeight = cwCfg.EnemyIpsoWeight,
                CounterOpsMultiplier = cwCfg.CounterOpsMultiplier,
                SkepticismFactor = cwCfg.SkepticismFactor,
                InfectionRate = infectionRate,
                RecoveryRate = recoveryRate,
                BlackoutVulnThreshold = cwCfg.BlackoutVulnThresholdHours,
                BlackoutVulnMaxHours = cwCfg.BlackoutVulnMaxHours,
                BlackoutVulnMaxBonus = cwCfg.BlackoutVulnMaxBonus,

                // Trauma config
                EnvyStress = cwCfg.EnvyStress,
                TraumaGainRate = cwCfg.TraumaGainRate,
                TraumaDecayRate = cwCfg.TraumaDecayRate,
                InertiaGainRate = cwCfg.InertiaGainRate,
                InertiaDecayRate = cwCfg.InertiaDecayRate,

                // Impact pressure
                RecentImpacts = recentImpacts,
                LiveRaidPressures = liveRaidPressures,
                ImpactDistantFactor = cwCfg.ImpactDistantFactor,
                ImpactInfectionWeight = cwCfg.ImpactInfectionWeight,
                RaidInfectionRate = cwCfg.RaidInfectionRate,

                // Hero-by-type + tool-by-stratum effectiveness
                HeroCounterStrong = effectiveHeroCounterStrong,
                HeroCounterFloor = cwCfg.HeroCounterFloor,
                PatriotResistanceBonus = cwCfg.PatriotResistanceBonus,
                TelemarathonStratumStrong = cwCfg.TelemarathonStratumStrong,
                TelemarathonStratumFloor = cwCfg.TelemarathonStratumFloor,
                PatriotWealthyBackfire = cwCfg.PatriotWealthyBackfire,
                PatriotPoorPress = cwCfg.PatriotPoorPress,
                BuckwheatStratumPoor = cwCfg.BuckwheatStratumPoor,
                BuckwheatStratumFloor = cwCfg.BuckwheatStratumFloor,
                BuckwheatWealthyBackfire = cwCfg.BuckwheatWealthyBackfire,

                // Time
                DeltaTime = dt,
                DeltaHours = dtHours,
                CurrentTime = currentTime,
                ForceResistanceRecalculate = forceResistanceRecalculate
            };

            // Chain: buildJob → resolveJob (parallel)
            // CIVIC184 suppressed: Dependency is kept in sync with buildHandle above (FIX H41),
            // and the resolve schedule below folds buildHandle + telecomDeps. No lost handles.
#pragma warning disable CIVIC184
            if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] pre ResolveHouseholdPsyJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} recentImpacts={recentImpacts.IsCreated}/{recentImpacts.Length} hasInternetData={hasInternetData} hasIpsoData={hasIpsoData} coverageMap={m_CoverageMap.IsCreated}/capacity={m_CoverageMap.Capacity} ipsoMap={m_IpsoMap.IsCreated}/capacity={m_IpsoMap.Capacity} internetSet={m_InternetDisabledSet.IsCreated}/capacity={m_InternetDisabledSet.Capacity}");
            Dependency = resolveJob.ScheduleParallel(m_HouseholdQuery, JobHandle.CombineDependencies(buildHandle, telecomDeps));
            if (CivicSurvival.Core.Diagnostics.BurstLogBootstrap.MarkersEnabled)
                CivicSurvival.Core.Diagnostics.BurstLogBootstrap.Info($"[BURSTMARK] post ResolveHouseholdPsyJob.ScheduleParallel queryEmpty={m_HouseholdQuery.IsEmptyIgnoreFilter} recentImpacts={recentImpacts.IsCreated}/{recentImpacts.Length} hasInternetData={hasInternetData} hasIpsoData={hasIpsoData} coverageMap={m_CoverageMap.IsCreated}/capacity={m_CoverageMap.Capacity} ipsoMap={m_IpsoMap.IsCreated}/capacity={m_IpsoMap.Capacity} internetSet={m_InternetDisabledSet.IsCreated}/capacity={m_InternetDisabledSet.Capacity}");
            // Register our read of the telecom grid so the next vanilla telecom write waits
            // for the resolver (read-only handshake, no .Complete()).
            m_TelecomCoverage.AddReader(Dependency);
            if (recentImpacts.IsCreated)
                Dependency = recentImpacts.Dispose(Dependency);
            if (liveRaidPressures.IsCreated)
                Dependency = liveRaidPressures.Dispose(Dependency);
#pragma warning restore CIVIC184

            // NOTE: Transient reset moved to PsyTransientResetSystem
            // (runs AFTER WellbeingResolverSystem reads the values)

            // No Dispose — containers are persistent (cleared in BuildJob each frame)
            } // MentalHealth.Schedule
            }
            finally
            {
                // If an exception unwound the schedule path before the deferred dispose above ran, the
                // TempJob snapshot is still live. A scheduled resolve job may already be reading it, so
                // complete outstanding work before the synchronous free. On the happy path the deferred
                // dispose already invalidated recentImpacts, so this is a no-op (no double free, and the
                // exception still propagates — finally does not swallow it).
                if (recentImpacts.IsCreated || liveRaidPressures.IsCreated)
                {
                    Dependency.Complete();
                    if (recentImpacts.IsCreated) recentImpacts.Dispose();
                    if (liveRaidPressures.IsCreated) liveRaidPressures.Dispose();
                }
            }
        }

        /// <summary>
        /// Single-source transient/latch reset — the only place MHR's non-persisted state is
        /// listed. Data-only (no EnsureExists / EntityManager / SystemAPI): safe from every
        /// reset leg. MHR persists nothing, so this is the whole reset.
        /// </summary>
        private void ClearTransientState()
        {
            // LOAD-INVARIANT: the persistent lookup maps below are shared with the in-flight
            // BuildMentalHealthLookupsJob (writer) and ResolveHouseholdPsyJob (reader). They are
            // system-owned NativeContainers, not ECS-tracked components, so ECS auto-dependency
            // completion never covers them — a stale job from the previous city can still be reading
            // or writing them while we Clear() on the main thread. Complete our own job handle first,
            // exactly as OnDestroy does before Dispose and WellbeingResolverSystem does before its
            // load-reset. This runs only on the load boundary (ResetState / ValidateAfterLoad), never
            // on the hot path.
            Dependency.Complete();
            if (m_PendingImpacts.IsCreated && m_PendingImpacts.Length > 0)
            {
                Log.Info($"[LOAD-RESET] MHR dropped {m_PendingImpacts.Length} stale pending impacts"); // Axiom 1
                m_PendingImpacts.Clear();
            }
            // Persistent lookup maps are rebuilt each fire in BuildMentalHealthLookupsJob; clear them on
            // reset so no stale district data survives the reused instance before the first post-load fire.
            if (m_CoverageMap.IsCreated) m_CoverageMap.Clear();
            if (m_IpsoMap.IsCreated) m_IpsoMap.Clear();
            if (m_InternetDisabledSet.IsCreated) m_InternetDisabledSet.Clear();
            if (m_BuckwheatAidedSet.IsCreated) m_BuckwheatAidedSet.Clear();
            m_LastSlot = uint.MaxValue;      // force fire on first tick of the new city
            // Per-slot fire stamps back to the sentinel: the new city's first resolve per slot
            // integrates dt = 0 instead of clamped cross-city time (the clock itself is monotonic
            // process time and intentionally never reset).
            for (int i = 0; i < m_LastFireClockSeconds.Length; i++)
                m_LastFireClockSeconds[i] = -1d;
#pragma warning disable S2696 // Cross-system sync flag + process-global slot index reset on load
            s_DidFire = false;
            PsySlot.CurrentSlot = 0;         // closes LOW S-PSY-15 (process-global static leak across cities)
#pragma warning restore S2696
        }

        void IResettable.ResetState() => ClearTransientState();

        // Reconcile pass on every LoadGame/NewGame: MHR has no persisted baseline, so this only
        // drops stale in-flight transients (Invariant 3), never rebuilds durable state.
        public void ValidateAfterLoad() => ClearTransientState();

        protected override void OnDestroy()
        {
            Dependency.Complete();
            if (m_CoverageMap.IsCreated) m_CoverageMap.Dispose();
            if (m_IpsoMap.IsCreated) m_IpsoMap.Dispose();
            if (m_InternetDisabledSet.IsCreated) m_InternetDisabledSet.Dispose();
            if (m_BuckwheatAidedSet.IsCreated) m_BuckwheatAidedSet.Dispose();
            if (m_PendingImpacts.IsCreated) m_PendingImpacts.Dispose();

            // Symmetric deregister (matches OnCreate consumer registrations)
            PressureRegistry.DeregisterConsumer(PressureChannel.Blackout, nameof(MentalHealthResolverSystem));
            PressureRegistry.DeregisterConsumer(PressureChannel.Envy, nameof(MentalHealthResolverSystem));
            PressureRegistry.DeregisterConsumer(PressureChannel.Impact, nameof(MentalHealthResolverSystem));

            base.OnDestroy();
        }
    }
}
