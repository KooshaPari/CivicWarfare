using System;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Base;
using System.Collections.Generic;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Systems;
using LogContext = CivicSurvival.Core.Utils.LogContext;
using System.Threading;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Random Disasters - power plants can fail unexpectedly.
    /// Creates tension and need for reserve capacity.
    ///
    /// Failure types:
    /// - Minor: -50% capacity for 12 hours
    /// - Major: -100% capacity for 1-3 days
    ///
    /// Owns DisabledByDisaster sidecar lifecycle. PowerCapacityPipeline owns
    /// DisasterDamageModifier hydration and final capacity.
    /// </summary>
    // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5 exemplar: the paid disaster repair
    // stamps DisabledByDisaster.RepairedThroughHour at the PlantRepairService
    // transaction point (IDisasterRepairSink) and PPDS reconciles/sweeps from that
    // persisted record on load — the transient RepairCompletedEvent is in-session
    // structural cleanup only, never load-bearing.
    [TransientConsumerReconcile(typeof(RepairCompletedEvent), ReconcileMode.OwnsDurableOutbox, DurableState = typeof(DisabledByDisaster))]
    public partial class PowerPlantDisasterSystem : ThrottledSystemBase, IPostLoadValidation, IDisasterRepairSink, IActGatedSystem
    {
        public void PurgeAfterLoad() { }

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        // ===== Disaster constants =====
        private const float MAJOR_DISASTER_EXTRA_HOURS = 48f;
        private const float MINOR_DISASTER_DURATION_HOURS = 12f;
        private const ulong RANDOM_SEED = 0x505044535953UL;

        private static readonly LogContext Log = new("PowerPlantDisasterSystem");

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        public Act MinActiveAct => Act.Crisis;
        public ActGateState GateState => m_Gate?.State ?? ActGateState.AwaitingActState;

        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private PrefabSystem m_PrefabSystem = null!;
        private SerializableRandom m_Random;

        // ComponentLookups for sidecar lifecycle.
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;

        private EntityQuery m_CurrentActQuery;
        private EntityQuery m_AllDisabledByDisasterQuery;
#pragma warning disable CIVIC324 // Ephemeral act-gate controller; recreated by OnCreate, reset paths, and Deserialize.
        [System.NonSerialized] private ActGateController m_Gate = null!;
#pragma warning restore CIVIC324

        // Frame-local contract data by building index (ContractData is on separate entities now)
        [NonEntityIndex] private NativeHashMap<long, ContractData> m_ContractsByBuilding;

        // Frame-local map of DisabledByDisaster entities by stable building entity key
        [NonEntityIndex] private NativeHashMap<long, Entity> m_DisabledByBuilding;

        // RW lookup for durable transaction-time repair stamping (IDisasterRepairSink)
        private ComponentLookup<DisabledByDisaster> m_DisabledByDisasterLookup;

        // Reusable candidate buffer for unbiased disaster victim selection (pre-allocated, cleared per check)
        private List<(Entity entity, PrefabRef prefabRef)> m_DisasterCandidates = null!;

        private double m_GameHour = 0.0;
        private float m_LastCheckHour = -1f;

        [System.NonSerialized] private bool m_DisabledCleanupDone;
        [System.NonSerialized] private bool m_SuppressInitialDisabledCleanup;
        [System.NonSerialized] private CivicServiceLookups m_DisasterRepairLookups = null!;
        [System.NonSerialized] private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;

        // Disaster chance per game day (checked every hour)
        private static float DisasterChancePerDay => Core.Config.BalanceConfig.Current.Engineering.DisasterChancePerDay;

        // M2 FIX: Cache ModSettings (avoid lookup in hot path)
        private ModSettings? m_Settings;

        protected override bool ShouldSkipUpdate()
        {
            m_Gate.ReconcileFromSingleton(m_CurrentActQuery);

            if (m_Settings == null || !m_Settings.RandomDisastersEnabled)
                return true;

            return m_Gate.State != ActGateState.Active;
        }

        /// <summary>
        /// Clear stale DisasterDamagePercent when feature disabled.
        /// Prevents plants stuck at reduced capacity after runtime toggle-off.
        /// </summary>
        protected override void OnBecameEnabled()
        {
            m_SuppressInitialDisabledCleanup = false;
            m_DisabledCleanupDone = false;
        }

        protected override void OnBecameDisabled()
        {
            if (m_SuppressInitialDisabledCleanup)
            {
                m_SuppressInitialDisabledCleanup = false;
                return;
            }

            ClearActiveDisastersOnce();
        }

        private void ClearActiveDisastersOnce()
        {
            if (m_DisabledCleanupDone) return;
            m_DisabledCleanupDone = true;
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            foreach (var (_, disasterEntity) in
                SystemAPI.Query<RefRO<DisabledByDisaster>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                ecb.AddComponent<Deleted>(disasterEntity);
                IncrementEcbCount();
            }
            m_DisabledByBuilding.Clear();
            Log.Info("PowerPlantDisaster disabled — cleared all active disasters");
        }

        private void InitializeGate()
        {
            m_Gate = new ActGateController(
                isOpenFor: act => act >= Act.Crisis,
                onTransition: HandleGateTransition);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            Log.Info("Created");

            // Initialize ComponentLookups for atomic component access
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_DisabledByDisasterLookup = GetComponentLookup<DisabledByDisaster>(false);
            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_DisasterRepairLookups = new CivicServiceLookups(() =>
            {
                m_DisabledByDisasterLookup.Update(this);
            });

            // Frame-local map for ContractData (separate entities, query each frame)
            m_ContractsByBuilding = new NativeHashMap<long, ContractData>(16, Allocator.Persistent);

            // Frame-local map for DisabledByDisaster (separate mod entities)
            m_DisabledByBuilding = new NativeHashMap<long, Entity>(16, Allocator.Persistent);

            m_DisasterCandidates = new List<(Entity, PrefabRef)>(16);
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());
            m_AllDisabledByDisasterQuery = GetEntityQuery(
                ComponentType.ReadOnly<DisabledByDisaster>(),
                ComponentType.Exclude<Deleted>());

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Random = new SerializableRandom(RANDOM_SEED);
            InitializeGate();

            // FIX S5-05: Immediate hydration after load — no stale DisasterDamagePercent

            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IDisasterRepairSink>(this);
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
        }

        // FIX S5-05: Immediate DisasterDamagePercent hydration after load
        // Order 20: after OperationalDamage(10), before GridStress(30)
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_MID;
        public void ValidateAfterLoad()
        {
            if (m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var actSingleton)
                && actSingleton.CurrentAct == Act.PreWar
                && GameTimeSystem.Instance?.Current.TotalGameHours > GameRate.HOURS_PER_DAY)
            {
                Log.Warn("PPDS: CurrentActSingleton shows PreWar after load — possible deserialization fallback");
            }

            Log.Info("ValidateAfterLoad: act gate will reconcile on first update; PowerCapacityPipeline reconciles disaster modifiers");
        }

        protected override void OnThrottledUpdate()
        {
            // Update ComponentLookups for current frame
            m_BaseCapacityLookup.Update(this);

            // Build frame-local map of ContractData by building index
            // ContractData is now on SEPARATE entities (not on vanilla buildings)
            m_ContractsByBuilding.Clear();
            foreach (var contract in SystemAPI.Query<RefRO<ContractData>>().WithNone<Deleted>())
            {
                long contractKey = ((long)contract.ValueRO.Building.Index << 32) | (uint)contract.ValueRO.Building.Version;
                m_ContractsByBuilding.TryAdd(contractKey, contract.ValueRO);
            }

            // Build frame-local map of DisabledByDisaster entities by building index.
            // PowerCapacityPipeline.ApplyDisasterModifier hydrates DisasterDamageModifier.
            m_DisabledByBuilding.Clear();
            foreach (var (disaster, disasterEntity) in
                SystemAPI.Query<RefRO<DisabledByDisaster>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                if (disaster.ValueRO.Building.Index <= 0) continue;

                // W2 row 3: a disaster durably repaired at the transaction point
                // (RepairedThroughHour stamped on the persisted record) is cancelled
                // even if the transient RepairCompletedEvent did not survive load.
                // Exclude it from the active map + clear its modifier; the durable
                // sweep in ProcessRepairCompletedEvents deletes the stale entity.
                if (disaster.ValueRO.CreatedHour > 0.0
                    && disaster.ValueRO.RepairedThroughHour >= disaster.ValueRO.CreatedHour)
                {
                    continue;
                }

                long disasterKey = BuildingIdentityKey.Pack(disaster.ValueRO.Building.Index, disaster.ValueRO.Building.Version);
                if (!m_DisabledByBuilding.TryAdd(disasterKey, disasterEntity))
                    Log.Warn($"Duplicate active disaster key {disaster.ValueRO.Building.Index}:{disaster.ValueRO.Building.Version}");

            }

            if (!UpdateGameTime())
                return;

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            ProcessRepairCompletedEvents(ref ecb, ref hasEcb);
            CheckForNewDisasters(ref ecb, ref hasEcb);
            UpdateDisabledPlants(ref ecb, ref hasEcb);

        }

        private bool UpdateGameTime()
        {
            // Use cumulative game hours from TimeSystem
            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider == null)
            {
                Log.Error("[PowerPlantDisasterSystem] GameTimeSystem unavailable — skipping time update");
                return false;
            }
            double newHour = timeProvider.Current.TotalGameHours;
            // Mirror PlantWearSimulation.UpdateGameTime: on the first post-load tick the time
            // snapshot can still read 0/NaN/Inf (the local hour is stale until GameTimeSystem fills
            // the snapshot). Keep the last good hour and skip this frame, otherwise the hourly gate
            // opens on a garbage hour and spawns a disaster with CreatedHour=0 — which then falls
            // out of durable repair (post-load exclude requires CreatedHour > 0, sweep skips <= 0).
            if (double.IsNaN(newHour) || double.IsInfinity(newHour) || newHour <= 0.0)
                return false;
            m_GameHour = newHour;
            return true;
        }

        /// <summary>
        /// Cancel active disasters when a plant is fully repaired.
        /// RepairCompletedEvent is spawned by PlantRepairService.CompleteRepair.
        /// </summary>
        private void ProcessRepairCompletedEvents(ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            foreach (var (repairRef, _) in
                SystemAPI.Query<RefRO<RepairCompletedEvent>>()
                .WithEntityAccess())
            {
                var repair = repairRef.ValueRO;

                // C-3: only cancel a disaster lifecycle if the paid repair actually
                // cleared disaster damage. A wear/missile-only repair must not
                // force-cancel an unrelated active disaster for free.
                if ((repair.CauseMask & RepairCauseMask.Disaster) == 0)
                    continue;

                long repairKey = BuildingIdentityKey.Pack(repair.Building.Index, repair.Building.Version);
                if (!m_DisabledByBuilding.TryGetValue(repairKey, out Entity disasterEntity))
                    continue;

                if (!SystemAPI.HasComponent<DisabledByDisaster>(disasterEntity))
                    continue;

                var disaster = SystemAPI.GetComponent<DisabledByDisaster>(disasterEntity);
                if (disaster.Building.Version != repair.Building.Version)
                    continue;
                if (disaster.CreatedHour >= repair.RepairCompletedGameHour)
                    continue;

                // Cancel disaster sidecar; the pipeline clears DisasterDamageModifier.
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                ecb.AddComponent<Deleted>(disasterEntity);
                IncrementEcbCount();
                m_DisabledByBuilding.Remove(repairKey);

                Log.Info($"Repair cancelled active disaster on building {repair.Building.Index}");
            }

            // W2 row 3 durable sweep: cancel disasters whose persisted
            // RepairedThroughHour reached their CreatedHour. This is driven purely
            // by durable state (stamped at the repair transaction point), so it
            // works on load even though the transient RepairCompletedEvent is gone.
            foreach (var (disRef, disEntity) in
                SystemAPI.Query<RefRO<DisabledByDisaster>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                var d = disRef.ValueRO;
                if (d.CreatedHour <= 0.0 || d.RepairedThroughHour < d.CreatedHour)
                    continue;

                long durableKey = BuildingIdentityKey.Pack(d.Building.Index, d.Building.Version);
                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                ecb.AddComponent<Deleted>(disEntity);
                IncrementEcbCount();
                if (m_DisabledByBuilding.ContainsKey(durableKey))
                    m_DisabledByBuilding.Remove(durableKey);

                Log.Info($"Durable repair sweep: cancelled DisabledByDisaster for building {d.Building.Index}");
            }
        }

        /// <summary>
        /// W2 row 3 root fix: durable transaction-time disaster cancel. Stamps
        /// the PERSISTED DisabledByDisaster record with RepairedThroughHour the
        /// instant payment completes so a save taken this frame survives load
        /// cancelled. This scans the persisted sidecars directly instead of
        /// relying on the frame-local active-disaster map.
        /// </summary>
        [CompletesDependency("PowerPlantDisasterSystem durable repair sink: scans persisted DisabledByDisaster sidecars by cached query so repair stamping does not depend on the frame-local active-disaster map.")]
        void IDisasterRepairSink.ClearRepairedDisaster(BuildingRef building, double repairGameHour)
        {
            m_DisasterRepairLookups.RefreshIfStale();
            long key = BuildingIdentityKey.Pack(building.Index, building.Version);
            using var disasterEntities = m_AllDisabledByDisasterQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < disasterEntities.Length; i++)
            {
                Entity disasterEntity = disasterEntities[i];
                if (!m_DisabledByDisasterLookup.TryGetComponent(disasterEntity, out var disaster))
                    continue;
                if (disaster.Building.Index != building.Index || disaster.Building.Version != building.Version)
                    continue;
                // A disaster created AFTER the billed repair must not be cancelled by it.
                if (disaster.CreatedHour >= repairGameHour)
                    continue;

                disaster.RepairedThroughHour = repairGameHour;
                m_DisabledByDisasterLookup[disasterEntity] = disaster;
                if (m_DisabledByBuilding.ContainsKey(key))
                    m_DisabledByBuilding.Remove(key);

                Log.Info($"Durable repair: marked DisabledByDisaster repaired for building {building.Index}");
                return;
            }
        }

        private void CheckForNewDisasters(ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            // Defense-in-depth: never roll a disaster on a non-positive game hour. UpdateGameTime
            // already skips the frame on garbage time, but this guards any future caller of the
            // spawn path so CreatedHour (= m_GameHour) can never be written as <= 0.
            if (m_GameHour <= 0.0)
                return;

            // Only check once per game hour
            int currentHour = (int)m_GameHour;
            if (currentHour == (int)m_LastCheckHour)
                return;
            m_LastCheckHour = currentHour;

            // Collect all plants that pass the hourly disaster roll, then pick one uniformly.
            // Avoids bias toward low-index entities that "break on first hit" would cause.
            m_DisasterCandidates.Clear();

            foreach (var (baseCap, prefabRef, entity) in
                SystemAPI.Query<RefRO<PlantBaseCapacity>, RefRO<PrefabRef>>()
                .WithAll<Building, ElectricityProducer>()
                .WithNone<Game.Net.OutsideConnection, Deleted>()
                .WithEntityAccess())
            {
                // Skip plants with 0 original capacity
                if (baseCap.ValueRO.OriginalCapacity <= 0)
                    continue;

                // Skip plants that the capacity pipeline currently has offline
                // (construction, repair, collapse, or other zero-capacity state).
                if (IsOfflineInLatestCapacitySnapshot(entity))
                    continue;

                // Skip plants already affected by disaster
                long entityKey = BuildingIdentityKey.Pack(entity);
                if (m_DisabledByBuilding.TryGetValue(entityKey, out var existingDisasterEntity)
                    && SystemAPI.HasComponent<DisabledByDisaster>(existingDisasterEntity)
                    && SystemAPI.GetComponent<DisabledByDisaster>(existingDisasterEntity).Building.Version == entity.Version)
                    continue;

                // Calculate quality-adjusted disaster chance
                // Base chance is hourly (DisasterChancePerDay / 24)
                // Quality multiplier: Quality 0.7 → 1.3x, Quality 0.5 → 1.5x, Quality 1.0 → 1.0x
                float hourlyChance = DisasterChancePerDay / GameRate.HOURS_PER_DAY;
                float qualityMultiplier = GetQualityMultiplier(entity);
                float adjustedChance = math.min(1f, hourlyChance * qualityMultiplier);

                if (m_Random.NextDouble() <= adjustedChance)
                    m_DisasterCandidates.Add((entity, prefabRef.ValueRO));
            }

            Entity victim = Entity.Null;
            PrefabRef victimPrefab = default;

            if (m_DisasterCandidates.Count > 0)
            {
                int idx = m_Random.Next(m_DisasterCandidates.Count);
                (victim, victimPrefab) = m_DisasterCandidates[idx];
            }

            if (victim != Entity.Null)
            {
                var prefabRef = victimPrefab;

                if (!m_BaseCapacityLookup.TryGetComponent(victim, out var victimBase))
                    return;
                // Check for shady contract (ContractData is on separate entity now)
                long victimKey = ((long)victim.Index << 32) | (uint)victim.Version;
                bool shadyContract = m_ContractsByBuilding.TryGetValue(victimKey, out var contract)
                                    && contract.IsShady;

                // Determine failure severity - shady contracts have higher major failure chance
                const float NORMAL_MAJOR_CHANCE = 0.3f;
                const float SHADY_MAJOR_CHANCE = 0.5f;
                float majorChance = shadyContract ? SHADY_MAJOR_CHANCE : NORMAL_MAJOR_CHANCE;
                bool isMajor = m_Random.NextDouble() < majorChance;

                // Duration: Minor = 12h, Major = 24-72h
                float durationHours = isMajor
                    ? GameRate.HOURS_PER_DAY + (float)(m_Random.NextDouble() * MAJOR_DISASTER_EXTRA_HOURS)
                    : MINOR_DISASTER_DURATION_HOURS;

                // Disaster damage: Minor = 50%, Major = 100%
                float disasterDamage = isMajor ? 1f : 0.5f;

                string plantName = GetPlantName(prefabRef);
                int originalCapacity = victimBase.OriginalCapacity;

                if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                var disasterEntity = ecb.CreateEntity();
                ecb.AddComponent(disasterEntity, new DisabledByDisaster
                {
                    Building = BuildingRef.FromEntity(victim),
                    OriginalCapacity = originalCapacity,
                    RestoreHour = m_GameHour + durationHours,
                    CreatedHour = m_GameHour,
                    IsMajor = isMajor,
                    PlantName = plantName
                });
                IncrementEcbCount();

                // Spawn DamageAppliedEvent for tracking (EstimatedRepairCost=0: disasters auto-repair for free)
                var damageEventEntity = ecb.CreateEntity();
                ecb.AddComponent(damageEventEntity, new DamageAppliedEvent
                {
                    Type = DamageType.Disaster,
                    Building = BuildingRef.FromEntity(victim),
                    DamagePercent = disasterDamage,
                    EstimatedRepairCost = 0,
                    IsWaveDamage = false
                });
                IncrementEcbCount();

                int originalMW = originalCapacity / 1000;
                int lostMW = (int)Math.Round(originalMW * disasterDamage);

                if (shadyContract)
                {
                    Log.Info($"DISASTER: {(isMajor ? "Major" : "Minor")} failure at {plantName}! -{lostMW} MW for {durationHours:F0}h (SHADY CONTRACT!)");

                    EventBus?.SafePublish(new CorruptionGainEvent(
                        BalanceConfig.Current.Countermeasures.ShadyDisasterSuspicion,
                        "PowerPlantShadyDisaster"), "PowerPlantDisasterSystem");
                    EventBus?.SafePublish(new NarrativeTriggerEvent(
                        NarrativeTrigger.SatireShadyDisaster.ToKey(),
#pragma warning disable CIVIC050 // returned/stored in event, not per-frame
                        new Dictionary<string, string> { { "plant", plantName } }), "PowerPlantDisasterSystem");
#pragma warning restore CIVIC050
                }
                else
                {
                    Log.Info($"DISASTER: {(isMajor ? "Major" : "Minor")} failure at {plantName}! -{lostMW} MW for {durationHours:F0}h");
                }

                EventBus?.SafePublish(new InfraEvent(InfraEventType.PowerPlantDisaster, IsShady: shadyContract), "PowerPlantDisasterSystem");
            }
        }

        /// <summary>
        /// Get quality-based disaster multiplier for a power plant.
        /// Buildings with shady ContractData have higher disaster chance.
        /// Quality 0.7 → 1.3x, Quality 0.5 → 1.5x, Quality 1.0 → 1.0x
        /// POT-E05 FIX: Clamps quality to [0,1] to prevent unbounded multiplier.
        /// </summary>
        private float GetQualityMultiplier(Entity entity)
        {
            // ContractData is on separate entity now - look up by building index + version
            long qualityKey = ((long)entity.Index << 32) | (uint)entity.Version;
            if (!m_ContractsByBuilding.TryGetValue(qualityKey, out var contract))
                return 1f; // No contract = normal chance

            // Only Maintenance contracts affect disaster chance
            // Supply contracts affect efficiency (handled elsewhere)
            if (contract.Type != ContractType.Maintenance)
                return 1f;

            // Clamp quality to valid range [0, 1] to prevent unbounded multiplier
            float clampedQuality = math.clamp(contract.Quality, 0f, 1f);

            // Quality 1.0 → multiplier 1.0 (no increase)
            // Quality 0.7 → multiplier 1.3 (30% more disasters)
            // Quality 0.5 → multiplier 1.5 (50% more disasters)
            // Quality 0.0 → multiplier 2.0 (max - 100% more disasters)
            float multiplier = 1f + (1f - clampedQuality);

            if (contract.IsShady)
            {
                if (Log.IsDebugEnabled) Log.Debug($"[Disaster] Entity {entity.Index} has shady MAINTENANCE contract (Q={clampedQuality:F2}), multiplier={multiplier:F2}");
            }

            return multiplier;
        }

        private void UpdateDisabledPlants(ref EntityCommandBuffer ecb, ref bool hasEcb)
        {
            // Query DisabledByDisaster mod entities (not vanilla buildings)
            foreach (var (disasterRef, disasterEntity) in
                SystemAPI.Query<RefRO<DisabledByDisaster>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                var data = disasterRef.ValueRO;
                if (data.Building.Index <= 0) continue;
                // Skip if already handled by ProcessRepairCompletedEvents this frame
                long disasterKey = BuildingIdentityKey.Pack(data.Building.Index, data.Building.Version);
                if (!m_DisabledByBuilding.ContainsKey(disasterKey)) continue;

                // Check if repair time reached
                if (m_GameHour >= data.RestoreHour)
                {
                    Log.Info($"REPAIRED: {data.PlantName} back online! +{data.OriginalCapacity / 1000} MW");

                    // Mark mod entity as Deleted (ModEntityCleanupSystem will destroy)
                    if (!hasEcb) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); hasEcb = true; }
                    ecb.AddComponent<Deleted>(disasterEntity);
                    IncrementEcbCount();
                    m_DisabledByBuilding.Remove(disasterKey);
                }
            }
        }

        private bool IsOfflineInLatestCapacitySnapshot(Entity entity)
        {
            if (m_PowerCapacitySnapshotReader == null
                || !m_PowerCapacitySnapshotReader.TryGetSnapshot(out var snapshot))
            {
                return false;
            }

            for (int i = 0; i < snapshot.Plants.Count; i++)
            {
                if (snapshot.Plants[i].Plant == entity)
                    return snapshot.Plants[i].EffectiveCapacityKW <= 0;
            }

            return false;
        }

        /// <summary>
        /// SMELL-E04 FIX: Use shared utility for plant name detection.
        /// </summary>
        private string GetPlantName(PrefabRef prefabRef)
        {
            var plantType = PowerPlantUtils.GetPlantType(m_PrefabSystem, prefabRef);
            return PowerPlantUtils.GetDisplayName(plantType);
        }

        private void HandleGateTransition(ActGateState _, ActGateState next, bool isInitial)
        {
            if (next == ActGateState.Active)
            {
                m_SuppressInitialDisabledCleanup = false;
                if (!isInitial)
                {
                    m_DisabledCleanupDone = false;
                    ResetThrottleCounter();
                    ForceNextUpdate();
                    Log.Info("[Disaster] Gate opened");
                }
                return;
            }

            if (next == ActGateState.Inactive && isInitial)
            {
                m_SuppressInitialDisabledCleanup = true;
                return;
            }

            if (next == ActGateState.Inactive)
            {
                ClearActiveDisastersOnce();
                m_LastCheckHour = -1f;
                Log.Info("[Disaster] Gate closed");
            }
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IDisasterRepairSink>(this);

            if (m_ContractsByBuilding.IsCreated)
                m_ContractsByBuilding.Dispose();

            if (m_DisabledByBuilding.IsCreated)
                m_DisabledByBuilding.Dispose();

            m_DisasterCandidates?.Clear();
            m_DisasterCandidates = null!;
            m_PowerCapacitySnapshotReader = null;

            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }
}
