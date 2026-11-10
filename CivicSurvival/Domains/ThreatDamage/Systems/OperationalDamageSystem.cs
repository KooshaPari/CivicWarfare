using System;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Features.CrossDomain.DamageAccounting;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems;

namespace CivicSurvival.Domains.ThreatDamage.Systems
{
    /// <summary>
    /// Manages operational damage to power plants from missile strikes.
    ///
    /// Power plants don't get destroyed immediately - they lose efficiency.
    /// Each hit removes a slice = max(Repair.HitDamageMW, Repair.HitFleetSharePercent ×
    /// fleet nameplate from IPowerCapacitySnapshotReader) of the plant's CURRENT
    /// nameplate (PlantBaseCapacity incl. installed upgrades), expressed as a damage
    /// fraction clamped to [MinHitLossPercent..MaxHitLossPercent] (PlantHitMath).
    /// Fleet-share scaling keeps one wave meaningful against over-built grids; at
    /// defaults (1200 MW, share 0.1, 0.1..0.5) and a 14815 MW fleet the slice is
    /// 1481 MW: 105 MW wind = 2 hits, 3500 MW gas = 3 hits, 7500 MW nuclear = 5 hits
    /// to reach DestructionThreshold — and a fleet-dominating station (share &gt; 1/3)
    /// still survives a full MaxThreatsPerTarget wave.
    ///
    /// REPAIR: Manual only (via PlantRepairService through PlantRepairRequestProcessor.ScheduleRepair on IPlantRepairScheduler).
    /// Auto-repair has been disabled - players must choose repair type:
    /// - Municipal Contract (24h, City Budget, optional kickback)
    /// - Shadow Ops (2h, Shadow Cash)
    ///
    /// P0-200 FIX: PowerPlantDamage now lives on SEPARATE mod entities (not vanilla buildings).
    /// CalculateDamage creates/updates mod entities via caller's ECB.
    /// PowerCapacityPipeline reads these sidecars and owns OperationalDamageModifier hydration.
    /// </summary>
    [SingletonOwner(typeof(PowerPlantDamage))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 5 exemplar: the paid repair zeroes the
    // persisted PowerPlantDamage sidecar at the PlantRepairService transaction
    // point (IOperationalDamageRepairSink) and ODS reconciles from EquipmentWear /
    // the sidecar on load — the transient RepairCompletedEvent is in-session
    // structural cleanup only, never load-bearing.
    [TransientConsumerReconcile(typeof(RepairCompletedEvent), ReconcileMode.OwnsDurableOutbox, DurableState = typeof(PowerPlantDamage))]
    public partial class OperationalDamageSystem : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation, IOperationalDamageRepairSink
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("OperationalDamageSystem");

        // Throttle: same pattern as BlackoutSystem.
        // S18-01 ACCEPTED: ODS observes hit-sidecar state within this 2-frame cadence,
        // but visible power capacity is reconciled by PowerCapacityPipeline's own
        // throttle/forced-update boundary. Do not document this as a pure 2-frame
        // user-visible window.
        private const int UPDATE_INTERVAL = 2;
        private int m_UpdateCounter = 0;

        // Query for PowerPlantDamage mod entities
        private EntityQuery m_DamageModEntityQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        // PERF: chunk iteration for RebuildDamageMap
        private ComponentTypeHandle<PowerPlantDamage> m_PowerPlantDamageTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;

        // ComponentLookups for atomic TryGetComponent (avoids TOCTOU race)
        private ComponentLookup<ElectricityProducer> m_ElectricityProducerLookup;
        private ComponentLookup<PowerPlantDamage> m_PowerPlantDamageLookup;
        private ComponentLookup<PlantBaseCapacity> m_BaseCapacityLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;

        // BuildingIndex → mod entity map (rebuilt each update cycle)
        [NonEntityIndex] private NativeHashMap<int, Entity> m_DamageByBuilding;
        private int m_DamageMapOrderToken = int.MinValue;

        // M6 FIX: Track in-frame first-hits via ECB (entity doesn't exist yet, ComponentLookup can't find it).
        // Key: BuildingIndex, Value: (ECB entity, accumulated damage). Cleared by caller each frame.
        // CIVIC097: Index-only key is safe — cleared every frame, no structural changes during CalculateDamage.
#pragma warning disable CIVIC097
        [NonEntityIndex] private NativeHashMap<int, (Entity ecbEntity, PowerPlantDamage damage)> m_InFrameEcbHits;
#pragma warning restore CIVIC097

        // H1 FIX: Tracks buildings already queued for destruction side-effects this frame.
        // Unity ECB single-entity AddComponent<Deleted> is idempotent at playback; this
        // guard prevents duplicate counters/events when clamp makes arithmetic guard unreliable.
        [NonEntityIndex] private NativeHashSet<int> m_DeletionQueued;

        // M12/M13 FIX: Tracks damage sidecar entities already queued for Deleted
        // this frame. ComponentLookup<Deleted> cannot see same-frame ECB tags.
        private NativeHashSet<Entity> m_DamageEntityDeletionQueued;

        // H2 FIX: Pending flag — OnActChanged sets true, OnUpdateImpl processes.
        // CIVIC292: SystemAPI.Query() in event handler runs in wrong system context.
        private bool m_PendingActTransitionCleanup;

        // C-5: self-defending consumer — independently drops a stale/unstamped
        // threat generation even though TDS already gates. Resolved in OnCreate (clock is
        // registered in Mod.OnLoad, before any OnCreate), never in OnUpdate.
        // Process-lifetime service handle (re-resolved in ValidateAfterLoad) — not save state.
        [System.NonSerialized] private ThreatGenerationClock m_threatGenerationClock = null!;
        [System.NonSerialized] private bool m_KnownActInitialized;
        [System.NonSerialized] private CivicServiceLookups m_RepairLookups = null!;

        // Fleet nameplate source for the fleet-share hit slice (PlantHitMath.EffectiveHitSliceMW).
        // Process-lifetime service handle, lazy-resolved at the use site (Engineering is
        // AlwaysOpen, registered before any wave can land a hit) — not save state.
        [System.NonSerialized] private IPowerCapacitySnapshotReader? m_PowerCapacitySnapshotReader;

        // PERF: Gate CalculateDamage lookups to once per frame (reset in ClearInFrameHits)
        [System.NonSerialized] private bool m_LookupsUpdatedThisFrame;

        // State
        private double m_GameTime = 0.0;
        [System.NonSerialized] private Act m_KnownAct;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Initialize ComponentLookups for atomic component access
            m_ElectricityProducerLookup = GetComponentLookup<ElectricityProducer>(true);
            m_PowerPlantDamageLookup = GetComponentLookup<PowerPlantDamage>(false);
            m_BaseCapacityLookup = GetComponentLookup<PlantBaseCapacity>(true);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_RepairLookups = new CivicServiceLookups(() =>
            {
                m_PowerPlantDamageLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
            });

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            // Query for PowerPlantDamage mod entities (P0-200: no longer on vanilla buildings)
            m_DamageModEntityQuery = GetEntityQuery(
                ComponentType.ReadOnly<PowerPlantDamage>(),
                ComponentType.Exclude<Deleted>()
            );
            // BuildingIndex → mod entity map
            m_DamageByBuilding = new NativeHashMap<int, Entity>(16, Allocator.Persistent);
            m_InFrameEcbHits = new NativeHashMap<int, (Entity, PowerPlantDamage)>(4, Allocator.Persistent);
            m_DeletionQueued = new NativeHashSet<int>(4, Allocator.Persistent);
            m_DamageEntityDeletionQueued = new NativeHashSet<Entity>(4, Allocator.Persistent);

            m_PowerPlantDamageTypeHandle = GetComponentTypeHandle<PowerPlantDamage>(true);
            m_EntityTypeHandle = GetEntityTypeHandle();

            SubscribeRequired<ActChangedEvent>(OnActChanged);

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IOperationalDamageRepairSink>(this);
            }

            Log.Info("Created (mod entity pattern; PowerCapacityPipeline hydrates OperationalDamageModifier)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_threatGenerationClock ??= ServiceRegistry.Instance.Require<ThreatGenerationClock>();
        }

        protected override void OnUpdateImpl()
        {
            if (m_DeletionQueued.IsCreated) m_DeletionQueued.Clear();
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Clear();
            m_PowerPlantDamageLookup.Update(this);
            m_DeletedLookup.Update(this);

            // H2 FIX: Process deferred act transition cleanup (CIVIC292-safe — runs in OnUpdate context)
            if (m_PendingActTransitionCleanup)
            {
            if (!m_DamageModEntityQuery.IsEmptyIgnoreFilter)
            {
                EntityCommandBuffer ecb = default;
                bool ecbCreated = false;
                m_PowerPlantDamageTypeHandle.Update(this);
                m_EntityTypeHandle.Update(this);
                var chunks = m_DamageModEntityQuery.ToArchetypeChunkArray(Allocator.Temp);
                try
                {
                    for (int ci = 0; ci < chunks.Length; ci++)
                    {
                        var entities = chunks[ci].GetNativeArray(m_EntityTypeHandle);
                        for (int i = 0; i < chunks[ci].Count; i++)
                        {
                            if (!m_PowerPlantDamageLookup.TryGetComponent(entities[i], out var damage))
                                continue;
                            damage.DamagePercent = 0f;
                            m_PowerPlantDamageLookup[entities[i]] = damage;
                            if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                            QueueDamageEntityDeleted(ecb, entities[i]);
                        }
                    }
                }
                finally
                {
                    if (chunks.IsCreated) chunks.Dispose();
                }
                if (m_DamageByBuilding.IsCreated) m_DamageByBuilding.Clear();
                m_DamageMapOrderToken = int.MinValue;
            }
                if (m_InFrameEcbHits.IsCreated) m_InFrameEcbHits.Clear();
                if (m_DeletionQueued.IsCreated) m_DeletionQueued.Clear();
                m_LookupsUpdatedThisFrame = false;
                // C-5: clear the flag AFTER the wipe so this block's own ordering is
                // correct. NOTE: this bool only sequences the LOCAL wipe of
                // already-existing damage entities/maps for one frame — it is NOT the
                // cross-system / post-load guard. That guard is the threat-generation
                // stamp the impact carries, compared in CalculateDamage (and
                // in TDS.ProcessAllImpacts). A single-frame bool cannot gate a
                // same-frame, deferred-ECB, cross-system recreate; the generation can.
                m_PendingActTransitionCleanup = false;
            }

            // Throttle: update every UPDATE_INTERVAL frames (same pattern as BlackoutSystem)
            m_UpdateCounter++;
            if (m_UpdateCounter < UPDATE_INTERVAL)
                return;
            m_UpdateCounter = 0;

            // Update ComponentLookups for current frame
            using (PerformanceProfiler.MeasureDebug("SP:ODS.LookupSync"))
            {
                m_ElectricityProducerLookup.Update(this);
                m_PowerPlantDamageLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
            }

            UpdateGameTime();

            // Rebuild BuildingIndex → mod entity map
            using (PerformanceProfiler.MeasureDebug("ODS.RebuildMap"))
            {
                RebuildDamageMap();
            }

            // FIX S5-01: Process repair completions — delete PowerPlantDamage mod entities
            // before the capacity pipeline reads sidecar state.
            using (PerformanceProfiler.MeasureDebug("ODS.RepairEvents"))
            {
                ProcessRepairCompletedEvents();
            }

        }

        private void UpdateGameTime()
        {
            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider == null) { Log.Error("[OperationalDamageSystem] TimeProvider unavailable"); return; }
            m_GameTime = timeProvider.Current.TotalGameHours;
        }

        /// <summary>
        /// Rebuild BuildingIndex → mod entity map from current PowerPlantDamage entities.
        /// </summary>
        private void RebuildDamageMap()
        {
            m_DamageByBuilding.Clear();
            m_DamageMapOrderToken = m_DamageModEntityQuery.GetCombinedComponentOrderVersion(true);
            if (m_DamageModEntityQuery.IsEmpty)
                return;

            // PERF: chunk iteration — avoids SystemAPI.Query dependency tracker leak
            m_PowerPlantDamageTypeHandle.Update(this);
            m_EntityTypeHandle.Update(this);
            var chunks = m_DamageModEntityQuery.ToArchetypeChunkArray(Allocator.Temp);
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            for (int ci = 0; ci < chunks.Length; ci++)
            {
                var chunk = chunks[ci];
                var damages = chunk.GetNativeArray(ref m_PowerPlantDamageTypeHandle);
                var entities = chunk.GetNativeArray(m_EntityTypeHandle);

                for (int i = 0; i < chunk.Count; i++)
                {
                    int bIdx = damages[i].Building.Index;
                    if (!m_DamageByBuilding.TryAdd(bIdx, entities[i]))
                    {
                        // Duplicate BuildingIndex (stale + new after building rebuild).
                        // Keep the entity with higher BuildingVersion — always the newer building.
                        if (m_DamageByBuilding.TryGetValue(bIdx, out var existing)
                            && m_PowerPlantDamageLookup.TryGetComponent(existing, out var existingDmg)
                            && damages[i].Building.Version > existingDmg.Building.Version)
                        {
                            if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                            QueueDamageEntityDeleted(ecb, existing);
                            m_DamageByBuilding[bIdx] = entities[i];
                        }
                        else
                        {
                            if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                            QueueDamageEntityDeleted(ecb, entities[i]);
                        }
                    }
                }
            }
            if (chunks.IsCreated) chunks.Dispose();
        }

        /// <summary>
        /// FIX S5-01: Process RepairCompletedEvent entities.
        /// Sets DamagePercent=0 on PowerPlantDamage mod entity (prevents pipeline re-application),
        /// then marks mod entity as Deleted via ECB.
        /// </summary>
        private void ProcessRepairCompletedEvents()
        {
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            foreach (var (repairRef, _) in
                SystemAPI.Query<RefRO<RepairCompletedEvent>>()
                .WithEntityAccess())
            {
                var repair = repairRef.ValueRO;

                // C-3: only clear operational (missile) damage if the paid repair
                // actually addressed it. A disaster/wear-only repair must not
                // silently wipe this building's unrelated operational damage.
                if ((repair.CauseMask & RepairCauseMask.Operational) == 0)
                    continue;

                if (!m_DamageByBuilding.TryGetValue(repair.Building.Index, out Entity modEntity))
                    continue;

                if (!m_PowerPlantDamageLookup.TryGetComponent(modEntity, out var damage))
                    continue;

                if (damage.Building.Version != repair.Building.Version)
                    continue;

                // M05 fix: skip if missile damage arrived after repair completed
                if (damage.LastDamageGameHour >= repair.RepairCompletedGameHour)
                {
                    Log.Info($"Repair event skipped: same-frame/post-repair damage detected on building {repair.Building.Index} (damage@{damage.LastDamageGameHour:F2}h >= repair@{repair.RepairCompletedGameHour:F2}h)");
                    continue;
                }

                // Zero out damage — PowerCapacityPipeline will write 0 to OperationalDamageModifier.
                damage.DamagePercent = 0f;
                m_PowerPlantDamageLookup[modEntity] = damage;

                // Schedule mod entity deletion
                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                QueueDamageEntityDeleted(ecb, modEntity);
#pragma warning disable CIVIC097 // PowerPlantDamage map is keyed by persisted BuildingRef.Index+Version checks above.
                m_DamageByBuilding.Remove(repair.Building.Index);
#pragma warning restore CIVIC097

                Log.Info($"Repair completed: cleared PowerPlantDamage for building {repair.Building.Index}");
            }

            if (ecbCreated)
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
        }

        /// <summary>
        /// W2 row 3 root fix: durable transaction-time repair. Zeroes the
        /// PERSISTED PowerPlantDamage sidecar the instant payment completes so a
        /// save taken this frame survives load already-repaired. ODS runs before
        /// PlantWearSimulation in PowerCapacityWriterGroup, so m_DamageByBuilding
        /// is already rebuilt for this frame when CompleteRepair calls this.
        /// The transient RepairCompletedEvent is now only in-session structural
        /// cleanup — no longer load-bearing.
        /// </summary>
        void IOperationalDamageRepairSink.ClearRepairedOperationalDamage(BuildingRef building, double repairGameHour)
        {
            m_RepairLookups.RefreshIfStale();
            if (!m_DamageByBuilding.TryGetValue(building.Index, out var modEntity))
                return;
            if (!m_PowerPlantDamageLookup.TryGetComponent(modEntity, out var damage))
                return;
            if (damage.Building.Version != building.Version)
                return;
            // M05: missile damage that landed at/after the billed repair must survive.
            if (damage.LastDamageGameHour >= repairGameHour)
                return;
            if (damage.DamagePercent <= 0f)
                return;
            damage.DamagePercent = 0f;
            m_PowerPlantDamageLookup[modEntity] = damage;
            Log.Info($"Durable repair: cleared persisted PowerPlantDamage for building {building.Index}");
        }

        // ============================================================================
        // PUBLIC API
        // ============================================================================

        /// <summary>
        /// Calculate and apply operational damage for power plant.
        /// Creates/updates PowerPlantDamage mod entity via caller's ECB.
        /// Each hit = -35% capacity. 3 hits = plant at ~5% efficiency + fire.
        /// </summary>
        /// <summary>
        /// Clear in-frame ECB hit tracking. Call at start of each damage processing frame.
        /// Does NOT attempt to migrate deferred handles (they're invalid after ECB playback).
        /// Instead, CalculateDamage uses lazy RebuildDamageMap to close the throttle gap.
        /// </summary>
        public void ClearInFrameHits()
        {
            if (m_InFrameEcbHits.IsCreated) m_InFrameEcbHits.Clear();
            if (m_DeletionQueued.IsCreated) m_DeletionQueued.Clear();
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Clear();
            m_LookupsUpdatedThisFrame = false;
        }

        /// <param name="powerPlant">Vanilla power plant building entity</param>
        /// <param name="ecb">Caller's EntityCommandBuffer for mod entity creation</param>
        /// <returns>Damage result</returns>
#pragma warning disable CIVIC231 // Called by ThreatArrivalSystem — caller validates threat context
        public OperationalDamageResult CalculateDamage(Entity powerPlant, EntityCommandBuffer ecb, int impactThreatGeneration)
        {
#pragma warning restore CIVIC231
            // W7-C5 FIX: ShouldDestroy=false — caller must not destroy building just because ODS is disabled
            if (!Enabled) return new OperationalDamageResult { IsValid = false, ShouldDestroy = false };

            // W6-M2: Act transition pending — entities about to be deleted, damage is meaningless
            // W7-C5 FIX: ShouldDestroy=false — missiles during act transition must not destroy buildings
            if (m_PendingActTransitionCleanup) return new OperationalDamageResult { IsValid = false, ShouldDestroy = false };

            // C-5 root: self-defend by threat generation. An unstamped (0) or stale
            // post-load zombie must never damage/destroy — the clock-stamped
            // generation IS the cross-system guard (not the bool below).
            if (impactThreatGeneration == ThreatGenerationClock.Unstamped
                || impactThreatGeneration != m_threatGenerationClock.Current)
                return new OperationalDamageResult { IsValid = false, ShouldDestroy = false };

            // PERF: Update lookups once per frame (no structural changes between calls).
            // Must run BEFORE lazy rebuild — RebuildDamageMap uses m_PowerPlantDamageLookup for dedup.
            if (!m_LookupsUpdatedThisFrame)
            {
                m_ElectricityProducerLookup.Update(this);
                m_PowerPlantDamageLookup.Update(this);
                m_BaseCapacityLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                // H2 FIX: Refresh m_GameTime — RequireForUpdate blocks OnUpdateImpl until
                // first PowerPlantDamage entity exists, so UpdateGameTime() never ran yet.
                var timeProvider = GameTimeSystem.Instance;
                if (timeProvider != null) m_GameTime = timeProvider.Current.TotalGameHours;

                m_LookupsUpdatedThisFrame = true;
            }

            if (powerPlant == Entity.Null || m_DeletedLookup.HasComponent(powerPlant) || m_DestroyedLookup.HasComponent(powerPlant))
                return new OperationalDamageResult { IsValid = false, ShouldDestroy = false };

            // Lazy rebuild: close throttle gap after ECB playback by observing
            // structural order, not "empty map" as a stale sentinel.
            int damageOrderToken = m_DamageModEntityQuery.GetCombinedComponentOrderVersion(true);
            if (damageOrderToken != m_DamageMapOrderToken)
                RebuildDamageMap();

            // Not a power plant - signal destruction
            if (!m_ElectricityProducerLookup.HasComponent(powerPlant))
                return new OperationalDamageResult { IsValid = false, ShouldDestroy = true };

            var repairCfg = BalanceConfig.Current.Repair;
            // Fleet-share hit slice: one slice per damage event, same formula as
            // EquipmentUISystem's hit counter (PlantHitMath — "can never drift").
            // No snapshot yet (first frames of a fresh world) → fleetNameplateKW = 0
            // → EffectiveHitSliceMW falls back to the absolute HitDamageMW.
            m_PowerCapacitySnapshotReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            int fleetNameplateKW = m_PowerCapacitySnapshotReader.TryGetSnapshot(out var fleetSnapshot)
                ? fleetSnapshot.NameplateKW
                : 0;
            float sliceMW = PlantHitMath.EffectiveHitSliceMW(repairCfg.HitDamageMW, repairCfg.HitFleetSharePercent, fleetNameplateKW);
            // Nameplate-scaled hit model: one hit = a sliceMW slice of the plant's
            // CURRENT nameplate, clamped (see PlantHitMath). The nameplate is read LIVE
            // from PlantBaseCapacity on EVERY hit — the index system re-publishes it on
            // upgrade, so installed upgrades immediately raise survivability instead of
            // the plant being judged by its pre-upgrade size.
            bool hasLiveNameplate = m_BaseCapacityLookup.TryGetComponent(powerPlant, out var liveBaseCap)
                && liveBaseCap.OriginalCapacity > 0;
            float lossPerHit;
            PowerPlantDamage damage;
            bool isFirstHit;
            Entity resolvedEntity; // Tracks the correct mod entity across all branches (real or ECB-deferred)
            float previousDamagePercent;

            // Check if mod entity exists for this building
            // R11-S05-F1: Also check Deleted — when ODS is disabled via RequireForUpdate,
            // m_DamageByBuilding is stale and may point to a Deleted mod entity
#pragma warning disable CIVIC097 // Index-only damage maps are frame-local or version-guarded at the access site
            if (m_DamageByBuilding.TryGetValue(powerPlant.Index, out Entity modEntity)
                && m_PowerPlantDamageLookup.TryGetComponent(modEntity, out var existingDamage)
                && existingDamage.Building.Version == powerPlant.Version
                && !m_DeletedLookup.HasComponent(modEntity))
#pragma warning restore CIVIC097
            {
                // Already damaged - increase damage. Nameplate falls back to the sidecar's
                // last-known snapshot only when PlantBaseCapacity is momentarily absent.
                damage = existingDamage;
                int nameplateKW = hasLiveNameplate ? liveBaseCap.OriginalCapacity : damage.OriginalCapacity;
                lossPerHit = PlantHitMath.LossPerHit(nameplateKW, sliceMW, repairCfg.MinHitLossPercent, repairCfg.MaxHitLossPercent);
                previousDamagePercent = damage.DamagePercent;
                damage.DamagePercent = math.clamp(damage.DamagePercent + lossPerHit, 0f, 1f);
                damage.LastDamageGameHour = m_GameTime;
                damage.OriginalCapacity = nameplateKW;   // follow upgrades (PlantBaseCapacity is mod-owned, no vanilla race)
                isFirstHit = false;
                resolvedEntity = modEntity;

                // Write back to mod entity (immediate via ComponentLookup)
                m_PowerPlantDamageLookup[modEntity] = damage;
            }
#pragma warning disable CIVIC097 // Frame-local ECB hit map is cleared by ClearInFrameHits
            else if (m_InFrameEcbHits.TryGetValue(powerPlant.Index, out var ecbHit))
#pragma warning restore CIVIC097
            {
                // M6 FIX: Second+ hit in same frame — ECB entity exists but not materialized yet.
                // Update the deferred entity's damage via ECB SetComponent (overwrites previous AddComponent).
                damage = ecbHit.damage;
                int nameplateKW = hasLiveNameplate ? liveBaseCap.OriginalCapacity : damage.OriginalCapacity;
                lossPerHit = PlantHitMath.LossPerHit(nameplateKW, sliceMW, repairCfg.MinHitLossPercent, repairCfg.MaxHitLossPercent);
                previousDamagePercent = damage.DamagePercent;
                damage.DamagePercent = math.clamp(damage.DamagePercent + lossPerHit, 0f, 1f);
                damage.LastDamageGameHour = m_GameTime;
                isFirstHit = false;
                resolvedEntity = ecbHit.ecbEntity;

                ecb.SetComponent(ecbHit.ecbEntity, damage);
#pragma warning disable CIVIC097 // Same frame update to the already-resolved ECB placeholder
                m_InFrameEcbHits[powerPlant.Index] = (ecbHit.ecbEntity, damage);
#pragma warning restore CIVIC097
            }
            else
            {
                // First hit - create mod entity via caller's ECB.
                // PowerPlantDamage.OriginalCapacity is sourced ONLY from PlantBaseCapacity
                // (mod-owned, refreshed on every hit to follow upgrades). It must never
                // fall back to producer.m_Capacity: that read raced vanilla
                // PowerPlantTickJob on frame %128 == 0 boundaries (≤0.78% of damage events,
                // for freshly-placed plants only) and a single bad read corrupted the
                // captured nameplate.
                //
                // Require PlantBaseCapacity to be present. If it's missing, the plant
                // is in the very-first-frame post-construction window (PowerCapacityIndex
                // ECB hasn't played back yet). Defer: ShouldDestroy=false marks "skip this
                // damage event, threat passes harmlessly, retry on the next hit" — same
                // pattern as the disabled / act-transition early-returns above. Returning
                // ShouldDestroy=true here would let a single first-frame threat hit
                // permanently destroy a freshly-placed plant before PlantBaseCapacity has
                // been hydrated, which would mis-attribute the cause to the threat.
                if (!hasLiveNameplate)
                    return new OperationalDamageResult { IsValid = false, ShouldDestroy = false };

                int currentCapacity = liveBaseCap.OriginalCapacity;
                lossPerHit = PlantHitMath.LossPerHit(currentCapacity, sliceMW, repairCfg.MinHitLossPercent, repairCfg.MaxHitLossPercent);

                damage = new PowerPlantDamage
                {
                    Building = BuildingRef.FromEntity(powerPlant),
                    OriginalCapacity = currentCapacity,
                    DamagePercent = lossPerHit,
                    LastDamageGameHour = m_GameTime
                };
                previousDamagePercent = 0f;
                isFirstHit = true;

                resolvedEntity = ecb.CreateEntity();
                ecb.AddComponent(resolvedEntity, damage);

                // M6 FIX: Track ECB entity for same-frame second hit
#pragma warning disable CIVIC097 // Same-frame dedup key; cleared before each damage processing frame
                m_InFrameEcbHits[powerPlant.Index] = (resolvedEntity, damage);
#pragma warning restore CIVIC097
            }

            // Calculate new capacity (for logging/events - actual enforcement in OnUpdate)
            int newCapacity = (int)Math.Round(damage.OriginalCapacity * (1f - damage.DamagePercent));
            int previousCapacity = (int)Math.Round(damage.OriginalCapacity * (1f - previousDamagePercent));
            int lostMW = Math.Max(0, (previousCapacity - newCapacity) / 1000);
            int remainingMW = newCapacity / 1000;

            // Spawn DamageAppliedEvent for financial tracking (DamageAccountingSystem)
            long estimatedRepairCost = (long)Math.Round(lossPerHit * 100) * repairCfg.RepairCostPerPercent;
            var damageEvent = ecb.CreateEntity();
            ecb.AddComponent(damageEvent, new DamageAppliedEvent
            {
                Type = DamageType.Operational,
                Building = BuildingRef.FromEntity(powerPlant),
                DamagePercent = lossPerHit,
                EstimatedRepairCost = estimatedRepairCost,
                IsWaveDamage = true
            });

            // Log damage details
            Log.Info($" Plant {powerPlant.Index}: damage +{lossPerHit:P0} → total {damage.DamagePercent:P0}, capacity {damage.OriginalCapacity / 1000}MW → {newCapacity / 1000}MW");

            // Notify about power loss
            // FIX N4-03: Pass IsFirstHit so ThreatNarrativeResolver can differentiate first strike
            EventBus?.SafePublish(new ThreatNarrativeEvent(ThreatNarrativeEventType.PowerPlantDamaged, LostMW: lostMW, RemainingMW: remainingMW, IsFirstHit: isFirstHit, AffectedPlantCount: 1), "OperationalDamageSystem");

            // Check if plant should be destroyed
            bool shouldDestroy = damage.DamagePercent >= repairCfg.DestructionThreshold;
            if (shouldDestroy)
            {
                // H1 FIX: Explicit fact-based guard — immune to clamp/rounding artifacts.
                // Old arithmetic guard (prevDamage = damage - loss) failed when clamp absorbed increment.
                // ECB AddComponent<Deleted> is idempotent; this prevents duplicate side-effects.
#pragma warning disable CIVIC097 // Deletion guard is frame-local and cleared with in-frame hit tracking
                if (!m_DeletionQueued.Contains(powerPlant.Index))
                {
                    if (Log.IsDebugEnabled) Log.Debug($" Power plant {powerPlant.Index} critically damaged");
                    QueueDamageEntityDeleted(ecb, resolvedEntity);
                    m_DeletionQueued.Add(powerPlant.Index);
                }
#pragma warning restore CIVIC097
            }

            return new OperationalDamageResult
            {
                IsValid = true,
                ShouldDestroy = shouldDestroy,
                Damage = damage,
                ResolvedModEntity = resolvedEntity
            };
        }

        // ShouldCatchFire removed — callers use CalculateDamage result.Damage.DamagePercent
        // directly (inline check vs FireThreshold). This avoids the stale m_DamageByBuilding
        // problem where first-frame ECB hits were invisible to the old map-based lookup.

        /// <summary>
        /// S18-06: Force immediate pipeline reconciliation after load.
        /// Manual throttle counter is set to UPDATE_INTERVAL so next OnUpdateImpl fires immediately.
        /// Order 10: first capacity sidecar reconciliation (before Disaster=20, Wear=20, GridStress=30).
        /// </summary>
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_FIRST;
        public void ValidateAfterLoad()
        {
            // C-5: re-resolve on a fresh-world load (process-lifetime ref otherwise survives).
            if (m_threatGenerationClock == null && ServiceRegistry.IsInitialized)
                m_threatGenerationClock = ServiceRegistry.Instance.Require<ThreatGenerationClock>();

            m_UpdateCounter = UPDATE_INTERVAL;

            // Pre-populate damage map to avoid lazy-rebuild sync point in CalculateDamage hot path
            if (!m_DamageModEntityQuery.IsEmptyIgnoreFilter)
            {
                m_ElectricityProducerLookup.Update(this);
                m_PowerPlantDamageLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                RebuildDamageMap();
                ReconcileRepairedOperationalDamageAfterLoad();
            }

            if (SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
            {
                m_KnownAct = actSingleton.CurrentAct;
                m_KnownActInitialized = true;
            }
        }

        private void ReconcileRepairedOperationalDamageAfterLoad()
        {
            int reconciled = 0;
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            foreach (var wearRef in SystemAPI.Query<RefRO<EquipmentWear>>())
            {
                var wear = wearRef.ValueRO;
                if (wear.Building.IsNull || wear.LastMaintenanceHour <= 0f)
                    continue;

#pragma warning disable CIVIC097 // m_DamageByBuilding entries are version-checked before mutation.
                if (!m_DamageByBuilding.TryGetValue(wear.Building.Index, out var damageEntity))
                    continue;
#pragma warning restore CIVIC097

                if (!m_PowerPlantDamageLookup.TryGetComponent(damageEntity, out var damage))
                    continue;

                if (damage.Building.Version != wear.Building.Version)
                    continue;

                if (damage.LastDamageGameHour >= wear.LastMaintenanceHour)
                    continue;

                damage.DamagePercent = 0f;
                m_PowerPlantDamageLookup[damageEntity] = damage;

                if (!m_DeletedLookup.HasComponent(damageEntity))
                {
                    if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                    QueueDamageEntityDeleted(ecb, damageEntity);
                }

#pragma warning disable CIVIC097 // m_DamageByBuilding entries are version-checked before removal.
                m_DamageByBuilding.Remove(wear.Building.Index);
#pragma warning restore CIVIC097
                reconciled++;
            }

            if (reconciled > 0)
                Log.Info($"ValidateAfterLoad: reconciled {reconciled} repaired operational damage sidecar(s)");
        }

        private void QueueDamageEntityDeleted(EntityCommandBuffer ecb, Entity entity)
        {
            if (entity == Entity.Null)
                return;

            if (entity.Index >= 0 && m_DeletedLookup.HasComponent(entity))
                return;

            if (m_DamageEntityDeletionQueued.IsCreated && !m_DamageEntityDeletionQueued.Add(entity))
                return;

            ecb.AddComponent<Deleted>(entity);
        }

        private void OnActChanged(ActChangedEvent evt)
        {
            if (!m_KnownActInitialized)
            {
                m_KnownAct = evt.NewAct;
                m_KnownActInitialized = true;
                Log.Info($"[OperationalDamage] Act initialized from event ({evt.NewAct}), state preserved");
                return;
            }

            if (evt.NewAct == m_KnownAct)
            {
                Log.Info($"[OperationalDamage] Act re-published ({evt.NewAct}), state preserved");
                return;
            }
            m_KnownAct = evt.NewAct;

            // Reset: clear accumulated damage tracking on act transition.
            // H12: Keep m_GameTime at current time (NOT 0) — zeroing it causes LastDamageGameHour=0
            // which makes repair cost formula use huge delta on first post-transition hit.
            m_UpdateCounter = UPDATE_INTERVAL; // Force immediate sync on next frame (same as ValidateAfterLoad)
            m_GameTime = GameTimeSystem.Instance?.Current.TotalGameHours ?? m_GameTime;

            // Defer all entity/map cleanup to OnUpdateImpl. CalculateDamage sees the pending flag
            // and drops same-frame post-transition damage instead of creating duplicate ECB entities.
            m_PendingActTransitionCleanup = true;
            Log.Info($"[OperationalDamage] Reset state on act transition → {evt.NewAct}");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IOperationalDamageRepairSink>(this);

            UnsubscribeSafe<ActChangedEvent>(OnActChanged);

            if (m_DamageByBuilding.IsCreated)
                m_DamageByBuilding.Dispose();
            if (m_InFrameEcbHits.IsCreated)
                m_InFrameEcbHits.Dispose();
            if (m_DeletionQueued.IsCreated)
                m_DeletionQueued.Dispose();
            if (m_DamageEntityDeletionQueued.IsCreated)
                m_DamageEntityDeletionQueued.Dispose();

            base.OnDestroy();
            Log.Info(" Destroyed");
        }
    }
}

