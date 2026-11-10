using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Prefabs;
using Game.Common;
using Unity.Entities;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using System.Threading;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Construction Delay System - power plants take time to reach full output.
    /// A new plant ramps its capacity linearly from 0 to nameplate over its build
    /// window (PowerCapacityMath.ComputeConstructionProgress); it is NOT a
    /// hard zero. Build durations per plant type come from balance config
    /// (Construction.*Days via PowerPlantUtils.GetConstructionDays) — not hardcoded here,
    /// so the values are not duplicated in this comment (they drift).
    ///
    /// Owns UnderConstruction sidecar lifecycle only. PowerCapacityPipeline owns
    /// ConstructionModifier hydration and final capacity.
    ///
    /// Plant tracking uses a stable Index-keyed registry (StablePlantIdentityRegistry) for
    /// new-vs-existing detection; identity survives damage-driven structural churn, no double-init possible.
    /// </summary>
    [ActIndependent]
    public partial class ConstructionDelaySystem : ThrottledSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private static readonly LogContext Log = new("ConstructionDelaySystem");

        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private PrefabSystem m_PrefabSystem = null!;

        // ComponentLookups
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<ElectricityProducer> m_ProducerLookup;
        private ComponentLookup<PowerCapacityIndexState> m_IndexStateLookup;

        // Transient last-seen nameplate + upgrade-hash per stable building Index. Drives
        // upgrade-delta detection on Known plants: a hash change ⇒ the InstalledUpgrade buffer
        // changed, delta>0 ⇒ open a build window for the increment, delta<=0 ⇒ instant. Seeded
        // without acting on first sight so the initial classification scan never fires a window.
        // [NonSerialized] in-memory only: CS2 reuses the system instance across an in-session load,
        // so stale entries from a prior city would mis-baseline upgrades — cleared in Deserialize,
        // ResetState, and ClearConstructionState (see the [NonSerialized]-persists-across-load hazard).
        [System.NonSerialized] private System.Collections.Generic.Dictionary<int, (int cap, int hash)> m_LastKnownNameplate = new();

        // Frame-local map of UnderConstruction mod entities by stable building Index
        [NonEntityIndex] private NativeHashMap<int, Entity> m_ConstructionByBuilding;

        // Persistent registry of known plants keyed by stable building Index (+ prefab guard).
        // Replaces the old Index|Version NativeHashSet that desynced under damage-driven structural
        // churn and re-flagged live plants as new. Entries persist across ticks; a plant is removed
        // ONLY when a confirmed demolition drops it from the live producer query (see prune below).
#pragma warning disable CIVIC221 // Transient — rebuilt from the live producer query each session, cleared in Deserialize (S4-04). Entity-Index records are not portable across save/load.
        private StablePlantIdentityRegistry m_PlantRegistry;
#pragma warning restore CIVIC221
#pragma warning disable CIVIC221 // Reset to false in Deserialize — forces fresh scan after load (S4-04)
        private bool m_InitialBatchRecorded;
#pragma warning restore CIVIC221

        [System.NonSerialized] private float m_CurrentGameDay = 0f;
        [System.NonSerialized] private bool m_InitialBatchZeroScanSeen;
        [System.NonSerialized] private float m_InitialBatchZeroScanDay;

        // Resolved in OnCreate so ValidateAfterLoad (post-load barrier, before OnStartRunning)
        // can read ConstructionDelayEnabled. Avoids a lookup in the hot path.
        private ModSettings m_Settings = null!;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND; // Check every ~1 second

        protected override bool ShouldSkipUpdate()
        {
            return !m_Settings.ConstructionDelayEnabled;
        }

        /// <summary>
        /// Clear stale IsUnderConstruction when feature disabled.
        /// Prevents plants stuck at 0 MW after runtime toggle-off.
        /// </summary>
        protected override void OnBecameDisabled()
        {
            ClearConstructionState("ConstructionDelay disabled");
        }

        private void ClearConstructionState(string reason)
        {
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<UnderConstruction>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Delete orphaned UnderConstruction entity to prevent re-hydration on re-enable
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                ecb.AddComponent<Deleted>(entity);
                IncrementEcbCount();
            }
            // Drop all classifications so a later re-enable re-runs the scan cleanly and the
            // resolver gate does not falsely treat plants as classified across a disabled interval.
            ConstructionClassifiedState.Clear();
            m_ConstructionByBuilding.Clear();
            m_LastKnownNameplate.Clear();
            int clearedModifiers = ClearConstructionModifiers();
            // Reset initial-batch state so re-enable treats all current plants as pre-existing
            // (prevents construction delay being applied to plants built while system was disabled)
            m_PlantRegistry.Clear();
            m_InitialBatchRecorded = false;
            m_InitialBatchZeroScanSeen = false;
            m_InitialBatchZeroScanDay = 0f;
            Log.Info($"{reason} — cleared all flags + deleted UnderConstruction entities + reset {clearedModifiers} construction modifiers + cleared classification set");
        }

        private int ClearConstructionModifiers()
        {
            int cleared = 0;
            foreach (var modifier in
                SystemAPI.Query<RefRW<ConstructionModifier>>()
                .WithNone<Deleted>())
            {
                if (!modifier.ValueRO.IsUnderConstruction)
                    continue;
#pragma warning disable CIVIC259 // Owner-boundary reset: ConstructionDelaySystem owns the construction modifier lifecycle.
                modifier.ValueRW.IsUnderConstruction = false;
#pragma warning restore CIVIC259
                cleared++;
            }

            return cleared;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            Log.Info("Created");

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

#pragma warning disable CIVIC403 // Mod.OnLoad registers ModSettings before SystemRegistrar.RegisterAll; ValidateAfterLoad (post-load, before OnStartRunning) reads ConstructionDelayEnabled, so it must be resolved in OnCreate.
            m_Settings = ServiceRegistry.Instance.Require<ModSettings>();
#pragma warning restore CIVIC403

            // Initialize ComponentLookups
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_ProducerLookup = GetComponentLookup<ElectricityProducer>(true);
            m_IndexStateLookup = GetComponentLookup<PowerCapacityIndexState>(true);

            // Frame-local map for UnderConstruction mod entities
            m_ConstructionByBuilding = new NativeHashMap<int, Entity>(32, Allocator.Persistent);

            // Pre-existing plant tracking (stable Index-keyed registry, persists across ticks)
            m_PlantRegistry = new StablePlantIdentityRegistry(32, Allocator.Persistent);
            m_InitialBatchRecorded = false;
            // Reset the classification side-set together with m_PlantRegistry so a fresh world
            // starts un-classified (the static set would otherwise carry keys from a prior city).
            // On an in-session load the instance is reused (OnCreate not re-run), so the set
            // persists and keeps classifications — exactly what the afterLoad gate-bypass expects.
            ConstructionClassifiedState.Clear();
        }


        protected override void OnThrottledUpdate()
        {
            // Update lookups for current frame
            m_BaseCapacityLookup.Update(this);
            m_ProducerLookup.Update(this);
            m_IndexStateLookup.Update(this);

            // Build frame-local map of UnderConstruction mod entities by building index
            m_ConstructionByBuilding.Clear();
            foreach (var (construction, constructionEntity) in
                SystemAPI.Query<RefRO<UnderConstruction>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                // Keyed by stable building Index (matches m_PlantRegistry / the resolver gate);
                // the version is still checked at the lookup site below.
                m_ConstructionByBuilding.TryAdd(construction.ValueRO.Building.Index, constructionEntity);
            }

#pragma warning disable CIVIC256 // Static singleton — null before GameTimeSystem.OnCreate
            if (GameTimeSystem.Instance == null)
                return;
#pragma warning restore CIVIC256

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            UpdateGameDay();
            ProcessNewBuildings(ref ecb, ref hasEcb);
            UpdateConstructionProgress(ref ecb, ref hasEcb);

            if (hasEcb)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        // Runs after PowerCapacityIndexSystem (POWER_MODIFIERS_FIRST−1 = 9) has re-added the
        // index/modifier components on the post-load barrier, and before PowerCapacityResolverSystem
        // (DEFAULT = 100) reconciles capacity — so every loaded plant is classified before the
        // resolver's afterLoad pass reads the classification gate.
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_MID;

        // Reconstruct the initial classification batch on the post-load barrier instead of lazily on
        // the first throttled tick. The lazy path fired ~18 s after load (GameSimulation is paused
        // during load + intro, then the 1 s throttle + GameTimeSystem gate), and any plant the player
        // placed in that window was wrongly folded into the pre-existing batch (no sidecar → instant
        // full MW instead of a build ramp). Running here — before the first GameSimulation frame and
        // before the player can place anything — guarantees every post-load placement reaches
        // ProcessNewBuildings with m_InitialBatchRecorded already true, i.e. the genuine New path.
        public void ValidateAfterLoad()
        {
            // Feature off ⇒ nothing to classify; the resolver's feature-off fallthrough serves every
            // plant full MW. Leave m_InitialBatchRecorded false so a later runtime enable runs the
            // lazy batch (OnBecameEnabled path) cleanly.
            if (!m_Settings.ConstructionDelayEnabled)
                return;

            RecordInitialBatchImmediate();
        }

        /// <summary>
        /// Pre-frame snapshot of every plant present at load as pre-existing: classify + register it
        /// with NO sidecar and NO GameTimeSystem access (a pre-existing plant needs neither a
        /// CompletionDay nor a build window). Mirrors the immediate-seed pattern
        /// EquipmentWearAssignSystem.ValidateAfterLoad uses (direct lookups, no ECB, no time base).
        /// </summary>
        private void RecordInitialBatchImmediate()
        {
            // ValidateAfterLoad runs outside the normal update, so lookup safety handles are stale —
            // refresh before any read (same contract as the OnThrottledUpdate head).
            m_BaseCapacityLookup.Update(this);
            m_ProducerLookup.Update(this);
            m_IndexStateLookup.Update(this);

            int recorded = 0;
            foreach (var (prefabRefRO, entity) in
                SystemAPI.Query<RefRO<PrefabRef>>()
                .WithAll<Building, ElectricityProducer, PlantBaseCapacity>()
                .WithNone<OutsideConnection, Deleted>()
                .WithEntityAccess())
            {
                Entity prefab = prefabRefRO.ValueRO.m_Prefab;
                m_PlantRegistry.ClassifyAndRegister(entity, prefab);
                ConstructionClassifiedState.Mark(StablePlantIdentityRegistry.ClassificationKey(entity));

                // Seed the upgrade-delta baseline (seed-without-acting) so the first steady-state
                // tick does not mis-read this pre-existing nameplate as a fresh +delta window.
                int newPlantHash = m_IndexStateLookup.TryGetComponent(entity, out var indexState)
                    ? indexState.UpgradeHash
                    : 0;
                int originalCapacity = m_BaseCapacityLookup.TryGetComponent(entity, out var baseCap)
                    ? baseCap.OriginalCapacity
                    : 0;
#pragma warning disable CIVIC097 // Stable building Index key by design — mirrors m_PlantRegistry/m_ConstructionByBuilding; a reused slot re-classifies as New/ReusedSlot and prune drops demolished entries.
                m_LastKnownNameplate[entity.Index] = (originalCapacity, newPlantHash);
#pragma warning restore CIVIC097
                recorded++;
            }

            m_InitialBatchRecorded = true;
            Log.Info($"Initial batch (post-load barrier): {recorded} pre-existing plants recorded");
        }

        private void UpdateGameDay()
        {
            // BUG-EN-004 FIX: Use TotalGameHours/24f for consistency with other systems
            var timeProvider = GameTimeSystem.Instance;
            m_CurrentGameDay = timeProvider != null ? timeProvider.Current.TotalGameHours / GameRate.HOURS_PER_DAY : 0f;
        }

        private void ProcessNewBuildings(ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            int newPlantsSkipped = 0;
            bool sawAnyPlant = false;
            // Collect current live plant Indices for post-loop prune of demolished plants.
            var currentIndices = new NativeHashSet<int>(32, Allocator.Temp);
            try
            {

            foreach (var (prefabRefRO, entity) in
                SystemAPI.Query<RefRO<PrefabRef>>()
                .WithAll<Building, ElectricityProducer, PlantBaseCapacity>()
                .WithNone<OutsideConnection, Deleted>()
                .WithEntityAccess())
            {
                // Identity is the stable building Index; the prefab guards against a slot reused by
                // a different building after demolition. Version is no longer part of the key — that
                // is what stops damage-driven structural churn from re-flagging a live plant as new.
                long classifyKey = StablePlantIdentityRegistry.ClassificationKey(entity);
                Entity prefab = prefabRefRO.ValueRO.m_Prefab;
                currentIndices.Add(entity.Index);
                sawAnyPlant = true;

                if (m_ConstructionByBuilding.TryGetValue(entity.Index, out var existingConstructionEntity)
                    && SystemAPI.HasComponent<UnderConstruction>(existingConstructionEntity))
                {
                    int sidecarVersion = SystemAPI.GetComponent<UnderConstruction>(existingConstructionEntity).Building.Version;
                    if (sidecarVersion == entity.Version)
                    {
                        // Live sidecar for THIS plant ⇒ classified (under construction). Mark every
                        // tick so a saved mid-construction plant — which always skips here on its
                        // post-load ticks and never reaches the marking branches below — keeps the
                        // resolver gate open.
                        ConstructionClassifiedState.Mark(classifyKey);
                        m_PlantRegistry.ClassifyAndRegister(entity, prefab);
                        // A further upgrade can land while this sidecar is still ramping; let the
                        // upgrade-delta path re-baseline it (hash changed ⇒ lock the already-ramped
                        // portion as new base, ramp the fresh delta). It also seeds the last-seen map.
                        TryHandleUpgradeDelta(entity, prefab, existingConstructionEntity, ref ecb, ref hasEcb);
                        continue;
                    }

                    // Index slot reused by a different building (this plant's Version differs from
                    // the sidecar's). The Index-keyed prune never reaches this orphan because the
                    // slot is still live for the new occupant, so delete the stale sidecar here,
                    // before classifying the new plant — otherwise two sidecars share one Index and
                    // the resolver could ramp the new plant against the demolished one's base/progress.
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(existingConstructionEntity);
                    IncrementEcbCount();
                    m_ConstructionByBuilding.Remove(entity.Index);
                }

                var identity = m_PlantRegistry.ClassifyAndRegister(entity, prefab);
                if (identity == PlantIdentityClass.Known)
                {
                    // Already-tracked plant surviving churn — never re-flag as new construction.
                    // But a capacity-increasing upgrade lands on the SAME entity (no prefab/Index
                    // change), so detect it here and apply a build window to the increment only.
                    TryHandleUpgradeDelta(entity, prefab, Entity.Null, ref ecb, ref hasEcb);
                    continue;
                }
                if (!m_InitialBatchRecorded)
                {
                    // Pre-existing plant (first scan after load/enable): classified as
                    // not-under-construction. Mark it so the resolver resolves it to full MW.
                    ConstructionClassifiedState.Mark(classifyKey);
                    newPlantsSkipped++;
                    continue;
                }

                var plantType = PowerPlantUtils.GetPlantType(m_PrefabSystem, prefabRefRO.ValueRO);

                // Unknown means the prefab lookup itself failed (transient entity state during a
                // vanilla swap-and-pop), NOT "a real plant whose name matched no substring" — that
                // resolves to Generic, a distinct value. Committing a durable UnderConstruction off a
                // failed lookup would lock in the wrong (Generic) duration forever, since a plant is
                // flagged once and never re-evaluated. Drop it from the registry so the next tick
                // re-resolves once the prefab slot is stable.
                if (plantType == PowerPlantUtils.PlantType.Unknown)
                {
                    currentIndices.Remove(entity.Index);
#pragma warning disable CIVIC097 // Stable Entity.Index registry key by design — prefab-guarded in StablePlantIdentityRegistry.
                    m_PlantRegistry.Remove(entity.Index);
#pragma warning restore CIVIC097
                    continue;
                }

                // Known prefab ⇒ classified. Mark now, BEFORE the construction-days gate below:
                // a plant with no build window (constructionDays==0) never gets a sidecar, so if
                // it were marked only alongside sidecar creation it would stay un-marked and the
                // resolver gate would strand it at 0 MW. Marking here resolves it to full MW.
                ConstructionClassifiedState.Mark(classifyKey);

                int constructionDays = PowerPlantUtils.GetConstructionDays(plantType);
                string plantTypeName = EnumName<PowerPlantUtils.PlantType>.Get(plantType);

                if (!m_BaseCapacityLookup.TryGetComponent(entity, out var baseCap))
                    continue;

                int originalCapacity = baseCap.OriginalCapacity;

                // Seed last-seen for this newly-tracked plant so a LATER upgrade deltas off its
                // built nameplate (and the whole-nameplate ramp below is not re-detected as a
                // delta on the next tick). A plant placed with upgrades already in its buffer
                // ramps the whole thing once here; only subsequent buffer changes open delta windows.
                int newPlantHash = m_IndexStateLookup.TryGetComponent(entity, out var newPlantIndexState)
                    ? newPlantIndexState.UpgradeHash
                    : 0;
#pragma warning disable CIVIC097 // Stable building Index key by design — mirrors m_PlantRegistry/m_ConstructionByBuilding. A reused slot classifies as New/ReusedSlot (this seed runs only after ClassifyAndRegister on this live plant), and prune drops demolished entries, so a recycled Index can never be mistaken for the old plant.
                m_LastKnownNameplate[entity.Index] = (originalCapacity, newPlantHash);
#pragma warning restore CIVIC097

                if (constructionDays > 0 && originalCapacity > 0)
                {
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    var constructionEntity = ecb.CreateEntity();
                    ecb.AddComponent(constructionEntity, new UnderConstruction
                    {
                        Building = BuildingRef.FromEntity(entity),
                        OriginalCapacity = originalCapacity,
                        CompletionDay = m_CurrentGameDay + constructionDays,
                        TotalDays = constructionDays,
                        PlantType = plantTypeName,
                        // New plant: whole nameplate ramps from 0, so base is 0 and the delta is the
                        // full nameplate. UpgradeHash records any upgrades present at placement.
                        BaseCapacityKW = 0,
                        UpgradeHash = newPlantHash
                    });
                    IncrementEcbCount();

                    Log.Info($"Commissioning: {plantTypeName} online in {constructionDays} days (day {m_CurrentGameDay + constructionDays:F0})");
                }
            }

            // Prune confirmed demolitions only: a plant present in the registry but absent from the
            // live producer query this tick was demolished (the live entity Index is gone). Drop its
            // registry record AND its resolver-gate classification so a future building reusing the
            // Index slot is not falsely treated as the old plant. A plant that merely churned keeps
            // the same live Index, so it stays present here — no transient re-derive.
            if (m_InitialBatchRecorded)
                PruneDemolishedPlants(currentIndices, ref ecb, ref hasEcb);
            if (!m_InitialBatchRecorded)
            {
                if (sawAnyPlant)
                {
                    m_InitialBatchRecorded = true;
                    if (newPlantsSkipped > 0)
                        Log.Info($"Initial scan: {newPlantsSkipped} pre-existing plants recorded (skipped commissioning delay)");
                }
                else
                {
                    const float InitialEmptyScanGraceDays = 0.05f;
                    if (!m_InitialBatchZeroScanSeen)
                    {
                        m_InitialBatchZeroScanSeen = true;
                        m_InitialBatchZeroScanDay = m_CurrentGameDay;
                    }
                    else if (m_CurrentGameDay - m_InitialBatchZeroScanDay >= InitialEmptyScanGraceDays)
                    {
                        m_InitialBatchRecorded = true;
                        Log.Info("Initial scan: no pre-existing plants found after grace window");
                    }
                }
            }

            } // try
            finally
            {
                if (currentIndices.IsCreated) currentIndices.Dispose();
            }
        }

        /// <summary>
        /// Remove plants that are tracked in the registry but no longer present in the live producer
        /// query — i.e. demolished. Drops the registry record, the resolver-gate classification, the
        /// upgrade-delta last-seen baseline, AND the live construction sidecar, so a building later
        /// reusing the freed Index slot inherits none of the demolished plant's state. The resolver
        /// keys its active-construction map by stable Index (no Version recheck), so a lingering
        /// orphan sidecar would otherwise hand its ramp/base to whatever new plant recycles the Index.
        /// A churned-but-live plant keeps its Index and is still in <paramref name="liveIndices"/>,
        /// so it is never pruned.
        /// </summary>
        private void PruneDemolishedPlants(NativeHashSet<int> liveIndices, ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            using var trackedIndices = m_PlantRegistry.GetTrackedIndices(Allocator.Temp);
            for (int i = 0; i < trackedIndices.Length; i++)
            {
                int idx = trackedIndices[i];
                if (liveIndices.Contains(idx))
                    continue;
                Log.Info($"Commissioning: pruned demolished plant idx={idx} (absent from live producer query)");
                m_PlantRegistry.Remove(idx);
                ConstructionClassifiedState.Unmark(StablePlantIdentityRegistry.ClassificationKey(new Entity { Index = idx }));
                m_LastKnownNameplate.Remove(idx);
                if (m_ConstructionByBuilding.TryGetValue(idx, out var sidecar)
                    && SystemAPI.HasComponent<UnderConstruction>(sidecar))
                {
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(sidecar);
                    IncrementEcbCount();
                }
            }
        }

        /// <summary>
        /// Detect a capacity-increasing upgrade on an already-known plant and apply the construction
        /// build window to the ADDED capacity only (the pre-upgrade base keeps producing full MW).
        /// Keyed on <see cref="PowerCapacityIndexState.UpgradeHash"/> (only InstalledUpgrade buffer
        /// membership changes open a window — a pure enable/disable of a built upgrade, or a hydro
        /// reshape, leaves the hash unchanged and re-baselines instantly). delta &gt; 0 ⇒ create a
        /// sidecar for the increment; delta &lt;= 0 ⇒ instant (delete any stale sidecar). A hash
        /// change while a sidecar is already live re-baselines it (already-ramped portion locks in as
        /// the new base, the fresh delta ramps from there). First sight is seeded WITHOUT acting so
        /// the initial classification scan never fires a window.
        /// </summary>
        /// <param name="entity">The live plant building entity (Known classification).</param>
        /// <param name="prefab">The plant's prefab entity (for plant-type → construction-days).</param>
        /// <param name="existingSidecar">The plant's live UnderConstruction sidecar, or Entity.Null.</param>
        private void TryHandleUpgradeDelta(Entity entity, Entity prefab, Entity existingSidecar, ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            // Plant must be indexed (PlantBaseCapacity + PowerCapacityIndexState present). If not,
            // skip — the index system will catch up within ≤2 sweeps and CDS retries next tick.
            if (!m_BaseCapacityLookup.TryGetComponent(entity, out var baseCap)
                || !m_IndexStateLookup.TryGetComponent(entity, out var indexState))
                return;

            int currentNameplate = baseCap.OriginalCapacity;
            int currentHash = indexState.UpgradeHash;

            // Stable building Index key by design — mirrors m_PlantRegistry/m_ConstructionByBuilding.
            // This method runs ONLY for a Known plant (same Index+prefab); a reused slot classifies as
            // New/ReusedSlot and never reaches here, and prune drops demolished Index entries, so a
            // recycled Index can never be mistaken for the old plant.
#pragma warning disable CIVIC097
            // First sight: seed without acting. The very first scan must never fire a window — a
            // pre-existing plant's current nameplate becomes its baseline, not a delta.
            if (!m_LastKnownNameplate.TryGetValue(entity.Index, out var lastSeen))
            {
                m_LastKnownNameplate[entity.Index] = (currentNameplate, currentHash);
                return;
            }

            // No upgrade-buffer change ⇒ nothing to do. A nameplate drift with the SAME hash (hydro
            // reshape, balance-config reload, enable/disable of a built upgrade) re-baselines
            // instantly — it is not "construction", so no window is opened.
            if (currentHash == lastSeen.hash)
            {
                if (currentNameplate != lastSeen.cap)
                    m_LastKnownNameplate[entity.Index] = (currentNameplate, currentHash);
                return;
            }

            int delta = currentNameplate - lastSeen.cap;
            // Record the new baseline in both branches before returning.
            m_LastKnownNameplate[entity.Index] = (currentNameplate, currentHash);
#pragma warning restore CIVIC097

            if (delta <= 0)
            {
                // Upgrade removed / shrink: instant. The resolver serves the lower nameplate on the
                // next sweep. Cancel any stale sidecar (its window is moot — capacity dropped).
                if (existingSidecar != Entity.Null && SystemAPI.HasComponent<UnderConstruction>(existingSidecar))
                {
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(existingSidecar);
                    IncrementEcbCount();
                }
                Log.Info($"Commissioning: upgrade removed on idx={entity.Index} ({lastSeen.cap / 1000}→{currentNameplate / 1000} MW), drops instantly");
                return;
            }

            var plantType = PowerPlantUtils.GetPlantType(m_PrefabSystem, new PrefabRef { m_Prefab = prefab });
            // Unknown ⇒ the prefab lookup failed transiently; retry next tick (baseline already updated,
            // so the delta is captured against the now-current cap — but a real upgrade keeps the same
            // prefab, so the lookup will succeed next tick and re-baseline picks up any further change).
            if (plantType == PowerPlantUtils.PlantType.Unknown)
                return;
            int constructionDays = PowerPlantUtils.GetConstructionDays(plantType);
            string plantTypeName = EnumName<PowerPlantUtils.PlantType>.Get(plantType);

            // A plant type with no build window (constructionDays==0) gets the upgrade instantly —
            // same as a new plant of that type would.
            if (constructionDays <= 0)
            {
                if (existingSidecar != Entity.Null && SystemAPI.HasComponent<UnderConstruction>(existingSidecar))
                {
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(existingSidecar);
                    IncrementEcbCount();
                }
                return;
            }

            if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }

            if (existingSidecar != Entity.Null && SystemAPI.HasComponent<UnderConstruction>(existingSidecar))
            {
                // Stacked upgrade mid-window: re-baseline. Lock the already-ramped portion as the new
                // base, ramp the fresh delta from there, restart the window. One sidecar per plant.
                var sidecar = SystemAPI.GetComponent<UnderConstruction>(existingSidecar);
                float progressNow = 1f;
                if (sidecar.TotalDays > 0)
                {
                    float remainingFraction = (sidecar.CompletionDay - m_CurrentGameDay) / sidecar.TotalDays;
                    progressNow = math.clamp(1f - remainingFraction, 0f, 1f);
                }
                int sidecarDelta = math.max(0, sidecar.OriginalCapacity - sidecar.BaseCapacityKW);
                int servedNow = sidecar.BaseCapacityKW + (int)math.round(sidecarDelta * progressNow);
                sidecar.BaseCapacityKW = servedNow;
                sidecar.OriginalCapacity = currentNameplate;
                sidecar.CompletionDay = m_CurrentGameDay + constructionDays;
                sidecar.TotalDays = constructionDays;
                sidecar.UpgradeHash = currentHash;
                sidecar.PlantType = plantTypeName;
                ecb.SetComponent(existingSidecar, sidecar);
                IncrementEcbCount();
                Log.Info($"Commissioning: upgrade +{delta / 1000} MW commissioning window started on idx={entity.Index} (re-baseline, base={servedNow / 1000} MW)");
                return;
            }

            // No sidecar yet: create one for the delta. Base = pre-upgrade served capacity (NOT ramped),
            // target = current nameplate (base + delta), so the resolver ramps only the increment.
            var constructionEntity = ecb.CreateEntity();
            ecb.AddComponent(constructionEntity, new UnderConstruction
            {
                Building = BuildingRef.FromEntity(entity),
                OriginalCapacity = currentNameplate,
                CompletionDay = m_CurrentGameDay + constructionDays,
                TotalDays = constructionDays,
                PlantType = plantTypeName,
                BaseCapacityKW = lastSeen.cap,
                UpgradeHash = currentHash
            });
            IncrementEcbCount();
            Log.Info($"Commissioning: upgrade +{delta / 1000} MW commissioning window started on idx={entity.Index}");
        }

        private void UpdateConstructionProgress(ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            // Query UnderConstruction mod entities (not vanilla buildings)
            foreach (var (constructionRef, constructionEntity) in
                SystemAPI.Query<RefRO<UnderConstruction>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                var construction = constructionRef.ValueRO;

                // Check if construction is complete
                if (m_CurrentGameDay >= construction.CompletionDay)
                {
                    Log.Info($"Commissioning: {construction.PlantType} online! Capacity at {construction.OriginalCapacity / 1000} MW");

                    if (construction.BaseCapacityKW > 0)
                    {
                        int delta = construction.OriginalCapacity - construction.BaseCapacityKW;
                        Log.Info($"Commissioning: upgrade +{delta / 1000} MW now live");
                    }

                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(constructionEntity);
                    IncrementEcbCount();
                }
            }
        }

        protected override void OnDestroy()
        {
            if (m_ConstructionByBuilding.IsCreated)
                m_ConstructionByBuilding.Dispose();
            m_PlantRegistry.Dispose();

            Log.Info("Destroyed");
            base.OnDestroy();
        }

    }
}
