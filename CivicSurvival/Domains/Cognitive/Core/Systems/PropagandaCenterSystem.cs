using Game.Common;
using Game.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Single writer of <see cref="PropagandaCenterState"/> (CIVIC175) — the Propaganda Center
    /// posture that unlocks the counter-propaganda layer and owns the broadcast-capacity budget.
    /// Rebuilt each throttled tick from the placed <see cref="PropagandaCenterInstallation"/>
    /// entities + the vanilla <c>TelecomCoverage</c> cell map (read-only — no second CellMap, no
    /// vanilla write). The state singleton is not serialized: recreated on load and reseeded in
    /// the post-load reconcile (<see cref="ValidateAfterLoad"/>) from the (serialized)
    /// installations, mirroring the AA live-cache pattern.
    ///
    /// Also the pause-safe host of the Center upgrade / allocation commands
    /// (<see cref="TryUpgradeCenter"/>, <see cref="SetBroadcastAllocation"/>) called synchronously
    /// from the UI thread (precedent: <c>HeroDeploymentSystem.TrySetHeroArchetype</c>). Both gate
    /// on the persisted installation — never on the derived singleton, which freezes while paused
    /// (Axiom 14) — and re-mirror the singleton through <see cref="RefreshDerivedState"/> so the
    /// readout follows the command even with GameSimulation not ticking.
    ///
    /// Installation LIFECYCLE (bulldoze/enemy-destruction cleanup + refund) is owned by
    /// <c>PropagandaCenterDemolitionSystem</c> (ModificationEnd, pause-safe); the scan here only
    /// skips dead installs.
    /// </summary>
    [ActIndependent]
    public sealed partial class PropagandaCenterSystem : ThrottledSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PropagandaCenterSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        // Fallbacks mirror the balance contract defaults (used only before the config loads).
        private const float FALLBACK_TELECOM_SIGNAL_THRESHOLD = 0.15f;
        private const int FALLBACK_UPGRADE_COST = 80000;
        private const int FALLBACK_MAX_TIER = 4;
        private const float BYTE_MAX = 255f;
        private const float ALLOC_EPSILON = 1e-4f;

        private EntityQuery m_InstallQuery;
        private EntityQuery m_StateQuery;
        private EntityQuery m_TelemarathonQuery;
        private EntityQuery m_HeroQuery;
        private EntityQuery m_CognitiveStateQuery;
        // Per-stratum household counts (read model) — the population default for the coverage-allocation
        // split when the player has not set explicit AllocWeight* (Phase 13A).
        private EntityQuery m_StatsQuery;

        private TelecomCoverageSystem? m_TelecomCoverage;

        // Async telecom-sample state: the coverage scan runs as a worker job registered with
        // the vanilla cell-map via AddReader; the main thread only consumes finished results.
        private NativeReference<float> m_TelecomFactorResult;
        private JobHandle m_TelecomSampleHandle;
        [System.NonSerialized] private bool m_TelecomSampleInFlight;
        // Transient cache cleared on load (ValidateAfterLoad) so instance reuse never serves the
        // previous city's factor; repopulated by the first post-load telecom sample.
        [System.NonSerialized] private float m_CachedTelecomFactor;

        // Transition-logging latches so built/tier changes log once, not every tick.
        [System.NonSerialized] private bool m_LoggedBuilt;
        [System.NonSerialized] private int m_LoggedTier = -1;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_InstallQuery = GetEntityQuery(ComponentType.ReadWrite<PropagandaCenterInstallation>());
            m_StateQuery = GetEntityQuery(ComponentType.ReadWrite<PropagandaCenterState>());
            m_TelemarathonQuery = GetEntityQuery(ComponentType.ReadOnly<TelemarathonRuntimeState>());
            m_HeroQuery = GetEntityQuery(ComponentType.ReadOnly<HeroDeploymentState>());
            m_CognitiveStateQuery = GetEntityQuery(ComponentType.ReadOnly<CognitiveState>());
            m_StatsQuery = GetEntityQuery(ComponentType.ReadOnly<CognitiveStatsState>());

            m_TelecomCoverage = World.GetOrCreateSystemManaged<TelecomCoverageSystem>();

            m_TelecomFactorResult = new NativeReference<float>(0f, Allocator.Persistent);

            EnsureStateSingleton();
            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            // Wait for the in-flight sample before releasing its output buffer.
            m_TelecomSampleHandle.Complete();
            if (m_TelecomFactorResult.IsCreated)
                m_TelecomFactorResult.Dispose();
            base.OnDestroy();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // Instance-reuse load skips OnCreate — re-ensure the (non-serialized) state singleton.
            EnsureStateSingleton();
        }

        protected override void OnThrottledUpdate() => Rebuild();

        // Created in OnCreate / OnStartRunning / the post-load reconcile only (never in the
        // throttled update — no structural change in the hot path).
        private void EnsureStateSingleton()
        {
            if (m_StateQuery.IsEmptyIgnoreFilter)
            {
                var e = EntityManager.CreateEntity();
                EntityManager.AddComponentData(e, PropagandaCenterState.Default);
            }
        }

        /// <summary>
        /// Post-load reconcile (Axiom 14 + the post-load seed pattern): the durable installations
        /// are restored by deserialization, but the derived singleton is only rebuilt in
        /// GameSimulation — with the vanilla <c>pausedAfterLoading</c> setting the first sim tick
        /// may be minutes away, and until it runs a Center restored from the save would read as
        /// absent (layer falsely locked). Re-ensure the (non-serialized) singleton and reseed the
        /// mirror from the deserialized installations immediately.
        ///
        /// Warm reload (a save loaded over a running city) reuses this system instance and skips
        /// OnCreate, so the transient caches keep the previous city's values. Clear them here so
        /// the first post-load tick never serves the old telecom factor and the built/tier
        /// transitions log fresh for the loaded city. Fires on both LoadGame and NewGame (the
        /// warm-reload path is LoadGame, which IResettable.ResetState would miss).
        /// </summary>
        public void ValidateAfterLoad()
        {
            ResetTransientCaches();
            EnsureStateSingleton();
            RefreshDerivedState();
        }

        // Reset the [NonSerialized] caches that survive an instance-reuse load. Complete any
        // in-flight sample first so a newly scheduled one never races the shared result buffer
        // (Complete() on a default/finished handle is a no-op).
        private void ResetTransientCaches()
        {
            m_TelecomSampleHandle.Complete();
            m_TelecomSampleInFlight = false;
            m_CachedTelecomFactor = 0f;
            m_LoggedBuilt = false;
            m_LoggedTier = -1;
        }

        private void Rebuild()
        {
            ScanInstallations(out bool built, out int tier, out float wPoor, out float wMiddle, out float wWealthy);

            // Sampled unconditionally (async worker job, no main-thread cost): the rumor-firebreak
            // and the UI readout consume TelecomFactor in cities without a Center — see the
            // SampleTelecomFactor doc for why gating this on `built` would zero them out.
            var cog = BalanceConfig.Current?.Cognitive;
            float telecomFactor = SampleTelecomFactor(cog);

            ComposeAndWriteState(built, tier, wPoor, wMiddle, wWealthy, telecomFactor);
        }

        /// <summary>
        /// Pause-tick rebuild of the derived mirror (Axiom 14). The regular owner of the mirror is
        /// the GameSimulation <see cref="Rebuild"/>, which does not run while paused — yet
        /// pause-safe surfaces keep moving its inputs: the host-direct commands mutate the install,
        /// the pause-safe placement commit creates one, the ModificationEnd demolition system
        /// destroys one. Called from the command tails, from the UI panel poll while paused, and
        /// from the post-load reconcile, so readouts and unlock gates never freeze on a pre-pause
        /// snapshot. The telecom factor is NOT re-sampled here (the sample is a Complete() + full
        /// cell-map scan, and the map does not move while paused) — the last mirrored value is
        /// reused; reach/capacity are recomposed from the fresh built/tier.
        /// </summary>
        public void RefreshDerivedState()
        {
            if (!m_StateQuery.TryGetSingleton<PropagandaCenterState>(out var state))
                return;

            ScanInstallations(out bool built, out int tier, out float wPoor, out float wMiddle, out float wWealthy);
            ComposeAndWriteState(built, tier, wPoor, wMiddle, wWealthy, state.TelecomFactor);
            Log.Debug("RefreshDerivedState (paused mirror)");
        }

        /// <summary>
        /// Scan the placed installations for the aggregate posture: any live Center, the highest
        /// upgrade tier, and that installation's allocation weights. Main-thread-safe (explicit
        /// query + EntityManager, no SystemAPI) so it serves both the GameSimulation
        /// <see cref="Rebuild"/> and the pause-tick <see cref="RefreshDerivedState"/> called from
        /// the UI thread. Dead installs (bulldozed / enemy-destroyed buildings) are skipped, not
        /// destroyed — their lifecycle (destroy + refund) is owned by
        /// <c>PropagandaCenterDemolitionSystem</c> (ModificationEnd).
        /// </summary>
        private void ScanInstallations(out bool built, out int tier, out float wPoor, out float wMiddle, out float wWealthy)
        {
            // A single HQ per city, but tolerate more than one (take the highest upgrade tier).
            built = false;
            tier = 0;
            // Coverage-allocation weights come from the highest-tier live installation — tracked
            // with a -1 sentinel so a tier-0 Center still contributes its split.
            int bestTier = -1;
            wPoor = 0f; wMiddle = 0f; wWealthy = 0f;

            // False positives (CIVIC218/CIVIC051): dual-context main-thread path per Axiom 14 —
            // this scan serves BOTH the GameSimulation rebuild and the pause-tick UI-thread
            // refresh (command tails / panel poll), where SystemAPI and update-context lookups
            // are unavailable. The install set is a handful of entities with no job writers
            // (installs are mutated by pause-safe main-thread commands only), so the Temp
            // ToEntityArray + GetComponentData pair carries no sync-point or structural risk.
#pragma warning disable CIVIC218, CIVIC051
            using var entities = m_InstallQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var install = EntityManager.GetComponentData<PropagandaCenterInstallation>(entities[i]);
#pragma warning restore CIVIC218, CIVIC051
                if (!IsObjectAlive(install.Building.ToEntity()))
                    continue;

                built = true;
                int instTier = install.UpgradeTier;
                if (instTier > tier)
                    tier = instTier;
                if (instTier > bestTier)
                {
                    bestTier = instTier;
                    wPoor = install.AllocWeightPoor;
                    wMiddle = install.AllocWeightMiddle;
                    wWealthy = install.AllocWeightWealthy;
                }
            }
        }

        /// <summary>
        /// Compose the derived posture from the scanned installation aggregate + the broadcast-tool
        /// and internet singletons, and write the state singleton. Runs on the main thread in both
        /// contexts (GameSimulation rebuild and the pause-tick refresh), so the write goes through
        /// <c>EntityManager.SetComponentData</c>. All writes stay inside this system — single
        /// writer preserved (CIVIC175).
        /// </summary>
        private void ComposeAndWriteState(bool built, int tier, float wPoor, float wMiddle, float wWealthy, float telecomFactor)
        {
            // TryGetSingletonEntity (not GetSingleton) — the singleton is created in OnCreate /
            // OnStartRunning, but guard rather than throw if a load hasn't re-ensured it yet.
            if (!m_StateQuery.TryGetSingletonEntity<PropagandaCenterState>(out var stateEntity))
                return;

            // The Telecenter model is a StaticObjectPrefab prop with no vanilla electricity hookup,
            // so "online" (the unlock condition) collapses to "built". If the Center ever becomes a
            // real CityServiceBuilding, gate this on the building's electricity here.
            bool online = built;

            var cog = BalanceConfig.Current?.Cognitive;
            float capacity = 0f, allocated = 0f, reach = 0f;
            if (cog != null)
            {
                capacity = cog.BroadcastCapacityBase + tier * cog.BroadcastCapacityPerTier;

                // Active broadcast tools draw from the capacity budget. Buckwheat is physical aid, not
                // broadcast — it never draws capacity (Decision 1). FIREWALL keeps our reach at an
                // ongoing capacity cost; BLACKOUT silences the Center entirely (both sides go dark).
                bool telemarathonActive = m_TelemarathonQuery.TryGetSingleton<TelemarathonRuntimeState>(out var tele)
                    && tele.IsActive && !tele.IsInShock;
                bool heroActive = m_HeroQuery.TryGetSingleton<HeroDeploymentState>(out var hero)
                    && hero.HeroStatus != HeroStatus.Inactive;

                var internetMode = m_CognitiveStateQuery.TryGetSingleton<CognitiveState>(out var cwState)
                    ? cwState.InternetMode : GlobalInternetMode.Open;

                if (telemarathonActive) allocated += cog.BroadcastToolCost;
                if (heroActive) allocated += cog.BroadcastToolCost;
                if (internetMode == GlobalInternetMode.Firewall) allocated += cog.FirewallCapacityDrain;

                // Guarded division (allocated == 0 → factor 1; the max keeps it finite either way).
                float capacityFactor = math.min(1f, capacity / math.max(ALLOC_EPSILON, allocated));
                float telecomReach = math.max(cog.TelecomReachFloor, telecomFactor);

                if (online && internetMode != GlobalInternetMode.Blackout)
                    reach = math.saturate(cog.CenterReachBase * capacityFactor) * telecomReach;
                // BLACKOUT (or offline) → reach stays 0: the Center cannot broadcast into a silenced city.
            }

            // Phase 13A — coverage-capacity in HOUSEHOLDS (a separate quantity from the abstract budget
            // above) + the effective per-stratum allocation split. The ceiling grows only by upgrade
            // (base + tier), never by city size, so a bigger city strains the same Center. The split is
            // the persisted player weights, or a population default until the player allocates.
            float coverageCapHouseholds = cog != null
                ? cog.CoverageCapacityBaseHouseholds + tier * cog.CoverageCapacityPerTierHouseholds
                : 0f;
            float awPoor = wPoor, awMiddle = wMiddle, awWealthy = wWealthy;
            ResolveAllocationWeights(ref awPoor, ref awMiddle, ref awWealthy);

            // False positive (CIVIC051): non-structural singleton write on the shared main-thread
            // path (Axiom 14) — the pause-tick UI-thread refresh has no ECB/update context, and
            // the GameSimulation rebuild reuses the same composer; single writer (CIVIC175).
#pragma warning disable CIVIC051
            EntityManager.SetComponentData(stateEntity, new PropagandaCenterState
            {
                IsBuilt = built,
                IsOnline = online,
                UpgradeTier = tier,
                BroadcastCapacity = capacity,
                CapacityAllocated = allocated,
                TelecomFactor = telecomFactor,
                CenterReach = reach,
                CoverageCapacityHouseholds = coverageCapHouseholds,
                AllocWeightPoor = awPoor,
                AllocWeightMiddle = awMiddle,
                AllocWeightWealthy = awWealthy
            });
#pragma warning restore CIVIC051

            if (built != m_LoggedBuilt)
            {
                m_LoggedBuilt = built;
                Log.Info($"Propaganda Center {(built ? "online" : "offline")} — capacity={capacity:F1} reach={reach:F2} telecom={telecomFactor:F2}");
            }
            if (tier != m_LoggedTier)
            {
                m_LoggedTier = tier;
                Log.Info($"Propaganda Center tier={tier} capacity={capacity:F1}");
            }
        }

        // Main-thread-safe liveness check (EntityManager, not ComponentLookup — the scan runs from
        // the UI thread too). Building is index+version, so a recycled object fails Exists by
        // version. Destroyed counts as dead: an enemy-destroyed Center must not gate commands open
        // while its rubble awaits the ModificationEnd demolition pass.
        // False positive (CIVIC051): read-only liveness probes on the shared main-thread path
        // (Axiom 14) — the UI-thread refresh cannot use ComponentLookup (no update context to
        // refresh it in), and Exists/HasComponent are non-structural point reads.
#pragma warning disable CIVIC051
        private bool IsObjectAlive(Entity building)
            => EntityManager.Exists(building)
               && !EntityManager.HasComponent<Deleted>(building)
               && !EntityManager.HasComponent<Destroyed>(building);
#pragma warning restore CIVIC051

        /// <summary>
        /// City telecom coverage fraction [0..1] sampled from the vanilla <c>TelecomCoverage</c> cell
        /// map (read-only). "Of the area the network touches, how much is strongly covered" — 0 with no
        /// telecom infrastructure (holes = risk zones), rising as the player builds out towers. The map
        /// refreshes every 4096 ticks (near-static), so serving the previous tick's sample (1 s stale)
        /// is indistinguishable to the consumer — the rumor-firebreak and the UI readout both work in
        /// cities without a Center, which is why this is sampled unconditionally, never gated on built.
        /// </summary>
        private float SampleTelecomFactor(CognitiveConfig? cog)
        {
            // PERF-LOCK: async telecom sample — the main thread NEVER blocks on the vanilla
            // telecom job. Consume the finished sample (IsCompleted → Complete() is a no-wait
            // bookkeeping call), then schedule the next one and register it via AddReader so
            // the vanilla writer waits for us. Replacing this with deps.Complete() + a
            // synchronous scan reinstates a periodic main-thread stall on the telecom job.
            if (m_TelecomSampleInFlight && m_TelecomSampleHandle.IsCompleted)
            {
                m_TelecomSampleHandle.Complete();
                m_CachedTelecomFactor = m_TelecomFactorResult.Value;
                m_TelecomSampleInFlight = false;
            }

            if (!m_TelecomSampleInFlight && m_TelecomCoverage != null)
            {
                float threshold = cog?.TelecomSignalThreshold ?? FALLBACK_TELECOM_SIGNAL_THRESHOLD;
                var data = m_TelecomCoverage.GetData(readOnly: true, out JobHandle deps);
                var handle = new SampleTelecomFactorJob
                {
                    Buffer = data.m_Buffer,
                    Threshold = threshold,
                    Result = m_TelecomFactorResult
                }.Schedule(deps);
                m_TelecomCoverage.AddReader(handle);
                m_TelecomSampleHandle = handle;
                m_TelecomSampleInFlight = true;
            }

            return m_CachedTelecomFactor;
        }

        /// <summary>
        /// Worker-side scan of the 128×128 telecom cell map: fraction of touched cells whose
        /// signal clears the defence threshold. Scheduled against the vanilla telecom job's
        /// dependency and registered via <c>AddReader</c>, so neither side races the other.
        /// </summary>
#if ENABLE_BURST
        [BurstCompile]
#endif
        private struct SampleTelecomFactorJob : IJob
        {
            [ReadOnly] public NativeArray<TelecomCoverage> Buffer;
            public float Threshold;
            public NativeReference<float> Result;

            public void Execute()
            {
                int touched = 0, covered = 0;
                for (int i = 0; i < Buffer.Length; i++)
                {
                    float s = Buffer[i].m_SignalStrength / BYTE_MAX;
                    if (s > 0f)
                    {
                        touched++;
                        if (s >= Threshold)
                            covered++;
                    }
                }
                Result.Value = touched > 0 ? (float)covered / touched : 0f;
            }
        }

        /// <summary>
        /// Pause-safe host-direct Center upgrade (UI thread): grows broadcast capacity by one tier at a
        /// budget cost (growth = upgrades, not copies). Rejects when no live Center exists, already at
        /// max tier, or funds are short. Mirrors the synchronous <c>TrySetHeroArchetype</c> command.
        /// </summary>
        public bool TryUpgradeCenter(out ReasonId reasonId)
        {
            // Gate on the persisted installation, NOT the derived state singleton: the singleton
            // is rebuilt only in GameSimulation, which does not tick while paused (Axiom 14) — a
            // state-based gate both re-admits a stale tier cap (repeat-click tier inflation while
            // paused) and falsely rejects a Center placed during the pause. Placeholder model:
            // online == built == live install, so "has a live installation" IS the online check;
            // when a real electricity model arrives (online != built), revisit this gate.
            if (!TryGetPrimaryInstallation(out var installEntity, out var install))
            {
                reasonId = ReasonIds.CenterRequired;
                return false;
            }

            var cog = BalanceConfig.Current?.Cognitive;
            int maxTier = cog?.PropagandaCenterMaxTier ?? FALLBACK_MAX_TIER;
            if (install.UpgradeTier >= maxTier) // cap on the install, not the pause-frozen mirror
            {
                reasonId = ReasonIds.CenterMaxTier;
                return false;
            }

            int cost = cog?.PropagandaCenterUpgradeCost ?? FALLBACK_UPGRADE_COST;
            if (cost > 0 && CityBudgetService.TryDeduct(World, cost, BudgetCategory.CognitiveOps) != BudgetResult.Ok)
            {
                reasonId = ReasonIds.CenterBudgetFailed;
                return false;
            }

            install.UpgradeTier += 1;
            EntityManager.SetComponentData(installEntity, install);
            RefreshDerivedState(); // immediate pause-safe mirror — the UI sees the new tier even while paused
            ForceNextUpdate();     // unpaused path: the next GameSim tick re-verifies the telecom sample

            EventBus?.SafePublish(new PropagandaCenterUpgradedEvent(install.UpgradeTier, cost), nameof(PropagandaCenterSystem));
            Log.Info($"Propaganda Center upgraded to tier={install.UpgradeTier} (cost={cost})");
            reasonId = ReasonId.None;
            return true;
        }

        /// <summary>
        /// Resolve the effective coverage-allocation split (Phase 13A): normalize the persisted player
        /// weights to sum 1, or — when the player has never allocated (all-zero) — fall back to the
        /// population share from the per-stratum household read model, or an even third when nothing is
        /// classified yet. This is the single place the "default = by-population" rule lives.
        /// </summary>
        private void ResolveAllocationWeights(ref float poor, ref float middle, ref float wealthy)
        {
            poor = math.max(0f, poor);
            middle = math.max(0f, middle);
            wealthy = math.max(0f, wealthy);
            float sum = poor + middle + wealthy;
            if (sum > ALLOC_EPSILON)
            {
                float inv = 1f / sum;
                poor *= inv; middle *= inv; wealthy *= inv;
                return;
            }

            // Never allocated → population default from the household read model.
            if (m_StatsQuery.TryGetSingleton<CognitiveStatsState>(out var stats))
            {
                float pop = stats.PoorHouseholds + stats.MiddleHouseholds + stats.WealthyHouseholds;
                if (pop > 0f)
                {
                    float inv = 1f / pop;
                    poor = stats.PoorHouseholds * inv;
                    middle = stats.MiddleHouseholds * inv;
                    wealthy = stats.WealthyHouseholds * inv;
                    return;
                }
            }

            // Nothing classified yet → even split.
            poor = middle = wealthy = 1f / 3f;
        }

        /// <summary>
        /// Pause-safe host-direct coverage-allocation command (UI thread, Axiom 14): set how the
        /// Center's household coverage ceiling splits across Poor/Middle/Wealthy. Input is normalized to
        /// sum 1 (a degenerate all-zero input falls back to an even third). Persisted on the primary
        /// installation so the split survives save/load; re-mirrored onto the state singleton immediately.
        /// Mirrors the synchronous <see cref="TryUpgradeCenter"/> / <c>TrySetHeroArchetype</c> commands —
        /// no ECB, so it lands even while the simulation is paused. Rejects only when no live Center
        /// installation exists.
        /// </summary>
        public bool SetBroadcastAllocation(float poor, float middle, float wealthy, out ReasonId reasonId)
        {
            // Same persisted-source gate as TryUpgradeCenter (Axiom 14): the derived singleton
            // freezes while paused, the installation does not.
            if (!TryGetPrimaryInstallation(out var installEntity, out var install))
            {
                reasonId = ReasonIds.CenterRequired;
                return false;
            }

            float p = math.max(0f, poor);
            float m = math.max(0f, middle);
            float w = math.max(0f, wealthy);
            float sum = p + m + w;
            if (sum > ALLOC_EPSILON)
            {
                float inv = 1f / sum;
                p *= inv; m *= inv; w *= inv;
            }
            else
            {
                p = m = w = 1f / 3f;
            }

            install.AllocWeightPoor = p;
            install.AllocWeightMiddle = m;
            install.AllocWeightWealthy = w;
            EntityManager.SetComponentData(installEntity, install);
            RefreshDerivedState(); // immediate pause-safe mirror — the UI sees the split even while paused
            ForceNextUpdate();     // unpaused path: the next GameSim tick re-verifies the telecom sample

            Log.Info($"Broadcast allocation set poor={p:F2} middle={m:F2} wealthy={w:F2}");
            reasonId = ReasonId.None;
            return true;
        }

        private bool TryGetPrimaryInstallation(out Entity installEntity, out PropagandaCenterInstallation install)
        {
            // Called from the UI thread (not this system's update) — use an explicit query, not
            // SystemAPI.Query. Pick the highest-tier live installation (a single HQ in practice);
            // IsObjectAlive also rejects Destroyed, so an enemy-destroyed Center cannot be
            // upgraded or re-allocated while its rubble awaits the demolition pass.
            installEntity = Entity.Null;
            install = default;
            int bestTier = -1;
            using var entities = m_InstallQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var inst = EntityManager.GetComponentData<PropagandaCenterInstallation>(entities[i]);
                if (!IsObjectAlive(inst.Building.ToEntity()))
                    continue;
                if (inst.UpgradeTier > bestTier)
                {
                    bestTier = inst.UpgradeTier;
                    installEntity = entities[i];
                    install = inst;
                }
            }
            return installEntity != Entity.Null;
        }
    }
}
