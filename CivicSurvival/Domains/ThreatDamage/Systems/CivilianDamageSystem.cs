using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.ThreatDamage;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.ThreatDamage.Systems
{
    /// <summary>
    /// Manages progressive damage and repair for non-power-plant buildings.
    /// Each hit increments HitCount on a CivilianWarDamage mod entity.
    /// Building destroyed when HitCount >= hitsToDestroy (sqrt formula).
    ///
    /// Repair lifecycle:
    /// 1. UI sends CivilianRepairRequest → CivilianRepairDetectorSystem creates CivilianRepairIntent in ModificationEnd
    /// 2. CivilianRepairPaymentSystem resolves payment in ModificationEnd
    /// 3. CivilianRepairCommitSystem calls ApplyRepairStart() here to set RepairEndHour
    /// 4. RebuildDamageMap() checks repair completion → resets HitCount
    ///
    /// Same pattern as OperationalDamageSystem:
    /// - Separate mod entities (not on vanilla buildings)
    /// - M6 same-frame ECB dedup via m_InFrameEcbHits
    /// - H1 double-delete guard via m_DeletionQueued
    /// - Throttled OnUpdate for map rebuild + cleanup
    /// </summary>
    public partial class CivilianDamageSystem : CivicSystemBase, IDefaultSerializable, IResettable, ICivilianDamageReader, IPostLoadValidation, IBuildingRefRebindOwner
    {
        public void PurgeAfterLoad() { }
        public int RebindOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;

        private static readonly LogContext Log = new("CivilianDamageSystem");
        private static readonly Type[] s_ReboundComponentTypes = { typeof(CivilianWarDamage) };

        private const int UPDATE_INTERVAL = 2;
        private int m_UpdateCounter = 0;

        private EntityQuery m_DamageModEntityQuery;
        private EntityQuery m_PendingCivilianRepairIntentQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;

        // PERF: chunk iteration for RebuildDamageMap
        private ComponentTypeHandle<CivilianWarDamage> m_CivilianWarDamageTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;

        private ComponentLookup<CivilianWarDamage> m_CivilianWarDamageLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;
        private ComponentLookup<BuildingData> m_BuildingDataLookup;

        // BuildingIndex → mod entity map (rebuilt each update cycle)
        [NonEntityIndex] private NativeHashMap<int, Entity> m_DamageByBuilding;

        // Archetype order version the map was last built at. Mirror of
        // OperationalDamageSystem.m_DamageMapOrderToken: lets EnsureDamageMapFresh skip the
        // ToArchetypeChunkArray sync point when the CivilianWarDamage set is unchanged since
        // the last rebuild (it cannot change mid-frame — ECB sidecars play at frame end).
        // Reset to int.MinValue wherever m_DamageByBuilding is cleared directly so a stale
        // token can never make a post-clear caller skip a required rebuild.
        private int m_DamageMapOrderToken = int.MinValue;

        // M6 FIX: Track in-frame first-hits via ECB (entity doesn't exist yet).
        // CIVIC097: Index-only key is safe — cleared every frame, no structural changes during RecordHit.
#pragma warning disable CIVIC097
        [NonEntityIndex] private NativeHashMap<int, (Entity ecbEntity, CivilianWarDamage damage)> m_InFrameEcbHits;
#pragma warning restore CIVIC097

        // H1 FIX: Tracks buildings already queued for destruction side-effects this frame.
        // Unity ECB single-entity AddComponent<Deleted> is idempotent; this prevents
        // duplicate counters/events while ComponentLookup cannot see same-frame ECB tags.
        [NonEntityIndex] private NativeHashSet<int> m_DeletionQueued;

        // M12/M13 FIX: Tracks damage sidecar entities already queued for Deleted
        // this frame. ComponentLookup<Deleted> cannot see same-frame ECB tags.
        private NativeHashSet<Entity> m_DamageEntityDeletionQueued;

        // H2 FIX: Pending flag — OnActChanged sets true, OnUpdateImpl processes.
        private bool m_PendingActTransitionCleanup;
        private bool m_PendingActTransitionPublish;
        [System.NonSerialized] private Act m_KnownAct;
        [System.NonSerialized] private bool m_KnownActInitialized;

        // PERF: Gate lookups to once per frame (reset in ClearInFrameHits)
        [System.NonSerialized] private bool m_LookupsUpdatedThisFrame;

        // Game time for repair completion checks
        private double m_GameHour;

        // ICivilianDamageReader: atomic version+value carrier.
        private readonly VersionedView<CivilianDamageSnapshot> m_DamageView = new(CivilianDamageSnapshot.Empty);
        private readonly List<CivilianBuildingDamageDto> m_SnapshotScratch = new(32);
        private CivilianDamageSnapshot m_DamageSnapshotSource = CivilianDamageSnapshot.Empty;
        public IVersionedView<CivilianDamageSnapshot> DamageView => m_DamageView;

        // C-5: self-defending consumer — independently drops a stale/unstamped
        // threat generation even though TDS already gates. Resolved in OnCreate (clock is
        // registered in Mod.OnLoad, before any OnCreate), never in OnUpdate.
        // Process-lifetime service handle (re-resolved in ValidateAfterLoad) — not save state.
        [System.NonSerialized] private ThreatGenerationClock m_threatGenerationClock = null!;
        [System.NonSerialized] private CivicServiceLookups m_RepairLookups = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CivilianWarDamageLookup = GetComponentLookup<CivilianWarDamage>(false);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_BuildingDataLookup = GetComponentLookup<BuildingData>(true);
            // Inline refresh lambda — CIVIC081 requires every lookup updated by the bundle
            // to appear as a literal {field}.Update(...) inside the constructor lambda body
            // so the analyzer can pair RefreshIfStale() callers with the right field set.
            m_RepairLookups = new CivicServiceLookups(() =>
            {
                m_CivilianWarDamageLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                m_PrefabRefLookup.Update(this);
                m_BuildingDataLookup.Update(this);

                var timeProvider = GameTimeSystem.Instance;
                if (timeProvider != null)
                    m_GameHour = timeProvider.Current.TotalGameHours;
            });

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();

            m_DamageModEntityQuery = GetEntityQuery(
                ComponentType.ReadOnly<CivilianWarDamage>(),
                ComponentType.Exclude<Deleted>()
            );

            m_PendingCivilianRepairIntentQuery = GetEntityQuery(
                ComponentType.ReadOnly<CivilianRepairIntent>(),
                ComponentType.Exclude<Deleted>());

            m_DamageByBuilding = new NativeHashMap<int, Entity>(64, Allocator.Persistent);
            m_InFrameEcbHits = new NativeHashMap<int, (Entity, CivilianWarDamage)>(8, Allocator.Persistent);
            m_DeletionQueued = new NativeHashSet<int>(8, Allocator.Persistent);
            m_DamageEntityDeletionQueued = new NativeHashSet<Entity>(8, Allocator.Persistent);

            m_CivilianWarDamageTypeHandle = GetComponentTypeHandle<CivilianWarDamage>(true);
            m_EntityTypeHandle = GetEntityTypeHandle();

            // System runs when ANY of: damaged buildings exist or repair intents are in-flight.
            RequireAnyForUpdate(m_DamageModEntityQuery, m_PendingCivilianRepairIntentQuery);

            SubscribeRequired<ActChangedEvent>(OnActChanged);

            // Register repair reader service for cross-domain access.
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<ICivilianDamageReader>(this);
            }

            Log.Info("Created (with repair support, ICivilianDamageReader registered)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_threatGenerationClock ??= ServiceRegistry.Instance.Require<ThreatGenerationClock>();
        }

        protected override void OnUpdateImpl()
        {
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Clear();
            m_CivilianWarDamageLookup.Update(this);
            m_DeletedLookup.Update(this);

            // H2 FIX: Process deferred act transition cleanup
            if (m_PendingActTransitionPublish && !m_PendingActTransitionCleanup)
            {
                PublishCivilianDamage(CivilianDamageSnapshot.Empty);
                m_PendingActTransitionPublish = false;
            }

            if (m_PendingActTransitionCleanup)
            {
                if (!m_DamageModEntityQuery.IsEmptyIgnoreFilter)
                    RebuildDamageMap(publishSnapshot: false, processRepairCompletion: false);

                EntityCommandBuffer ecb = default;
                bool anyDeleted = false;

                // Delete damage mod entities
                foreach (var (_, entity) in
                    SystemAPI.Query<RefRO<CivilianWarDamage>>()
                    .WithNone<Deleted>()
                    .WithEntityAccess())
                {
                    if (!anyDeleted)
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    QueueDamageEntityDeleted(ecb, entity);
                    anyDeleted = true;
                }
                if (anyDeleted)
                {
                    m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
                    if (m_DamageByBuilding.IsCreated) m_DamageByBuilding.Clear();
                    m_DamageMapOrderToken = int.MinValue;
                }
                PublishCivilianDamage(CivilianDamageSnapshot.Empty);
                m_PendingActTransitionPublish = false;
                // Always clear — one pass is enough. ECB will structurally remove entities next frame.
                // Two-phase wait caused permanent flag stuck: RequireAnyForUpdate blocked the system
                // after all entities gained Deleted, so the "anyDeleted=false" branch never ran.
                m_PendingActTransitionCleanup = false;
                return;
            }

            m_UpdateCounter++;
            if (m_UpdateCounter < UPDATE_INTERVAL)
                return;
            m_UpdateCounter = 0;

            m_CivilianWarDamageLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_BuildingDataLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);

            // Update game time for repair completion checks
            var timeProvider = GameTimeSystem.Instance;
            if (timeProvider != null)
                m_GameHour = timeProvider.Current.TotalGameHours;

            using (PerformanceProfiler.MeasureDebug("CDS.RebuildMap"))
            {
                // Single authoritative rebuild per throttle tick: it starts with a Clear()
                // and rescans from scratch, so a preceding map-only rebuild was pure waste.
                RebuildDamageMap(publishSnapshot: true, processRepairCompletion: true);
            }
        }

        // Rebuild the BuildingIndex→sidecar map only when the CivilianWarDamage archetype
        // changed since the last build. Within a frame no ECB sidecar materializes (they play
        // at GameSimulationEndBarrier), so the order version is frozen and repeated callers
        // (per-hit RecordHit, UI repair readers) reuse the same map instead of forcing a
        // ToArchetypeChunkArray sync point + full scan each call. Same-frame fresh hits are
        // tracked in m_InFrameEcbHits, not the map, so skipping the rebuild loses nothing.
        // Mirror of OperationalDamageSystem.CalculateDamage's order-token gate.
        private void EnsureDamageMapFresh()
        {
            int token = m_DamageModEntityQuery.GetCombinedComponentOrderVersion(true);
            if (token != m_DamageMapOrderToken)
                RebuildDamageMap(publishSnapshot: false, processRepairCompletion: false);
        }

        private void RebuildDamageMap(bool publishSnapshot = true, bool processRepairCompletion = true)
        {
            m_DamageByBuilding.Clear();
            // Stamp the token BEFORE the IsEmpty early-return (mirror ODS:288) so an empty
            // query still refreshes it — otherwise EnsureDamageMapFresh would keep rebuilding.
            m_DamageMapOrderToken = m_DamageModEntityQuery.GetCombinedComponentOrderVersion(true);
            var snapshot = m_SnapshotScratch;
            snapshot.Clear();
            int damagedCount = 0;
            int repairingCount = 0;

            if (m_DamageModEntityQuery.IsEmpty)
            {
                if (!publishSnapshot)
                    return;
                PublishCivilianDamage(CivilianDamageSnapshot.Empty);
                return;
            }

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            m_CivilianWarDamageTypeHandle.Update(this);
            m_EntityTypeHandle.Update(this);
            var chunks = m_DamageModEntityQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                // Phase 1: Populate m_DamageByBuilding map (dedup by BuildingVersion).
                // Snapshot is NOT written here — avoids double-entry when version-replacement wins.
                for (int ci = 0; ci < chunks.Length; ci++)
                {
                    var chunk = chunks[ci];
                    var damages = chunk.GetNativeArray(ref m_CivilianWarDamageTypeHandle);
                    var entities = chunk.GetNativeArray(m_EntityTypeHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var damage = damages[i];
                        int bIdx = damage.Building.Index;

                        // Check repair completion — mark entity for destruction
                        if (processRepairCompletion && damage.IsUnderRepair && m_GameHour >= damage.RepairEndHour)
                        {
                            if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                            QueueDamageEntityDeleted(ecb, entities[i]);
                            Log.Info($"Repair completed: building {bIdx}, entity destroyed");
                            continue;
                        }

                        // Skip undamaged entries (HitCount=0 leftover, pending cleanup)
                        if (damage.HitCount <= 0 && !damage.IsUnderRepair)
                            continue;

                        if (!m_DamageByBuilding.TryAdd(bIdx, entities[i]))
                        {
                            if (!m_DamageByBuilding.TryGetValue(bIdx, out var existing)
                                || !m_CivilianWarDamageLookup.TryGetComponent(existing, out var existingDmg))
                            {
                                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                                QueueDamageEntityDeleted(ecb, entities[i]);
                                continue;
                            }

                            // Duplicate for the same building identity: merge hits/repair state into
                            // the survivor before deleting the loser so damage cannot be lost.
                            if (damage.Building.Version == existingDmg.Building.Version)
                            {
                                var merged = MergeDuplicateDamage(existingDmg, damage);
                                m_CivilianWarDamageLookup[existing] = merged;
                                if (!ecbCreated) { ecb = m_GameSimulationEndBarrier.CreateCommandBuffer(); ecbCreated = true; }
                                QueueDamageEntityDeleted(ecb, entities[i]);
                            }
                            // Duplicate for a recycled building index: keep higher BuildingVersion.
                            else if (damage.Building.Version > existingDmg.Building.Version)
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
            }
            finally
            {
                if (chunks.IsCreated) chunks.Dispose();
            }

            if (!publishSnapshot)
                return;

            // Phase 2: Build snapshot from the deduplicated map.
            // Exactly one entry per BuildingIndex regardless of how many duplicate entities existed.
            var mapKeys = m_DamageByBuilding.GetKeyArray(Allocator.Temp);
            for (int k = 0; k < mapKeys.Length; k++)
            {
                int bIdx = mapKeys[k];
                if (!m_DamageByBuilding.TryGetValue(bIdx, out var winnerEntity)) continue;
                if (!m_CivilianWarDamageLookup.TryGetComponent(winnerEntity, out var damage)) continue;

                int maxHits = GetHitsToDestroy(damage.GetBuildingEntity());
                float repairHoursLeft = 0f;
                if (damage.IsUnderRepair)
                {
                    repairHoursLeft = (float)math.max(0.0, damage.RepairEndHour - m_GameHour);
                    repairingCount++;
                }
                else
                {
                    damagedCount++;
                }

                snapshot.Add(new CivilianBuildingDamageDto
                {
                    Building = new EntityRef(damage.Building.Index, damage.Building.Version),
                    // (Building DTO field is EntityRef-shaped; carrying typed BuildingRef would require DTO surface change.)
                    HitCount = damage.HitCount,
                    MaxHits = maxHits,
                    DamagePercent = maxHits > 0 ? (float)damage.HitCount / maxHits : 1f,
                    IsRepairing = damage.IsUnderRepair,
                    RepairHoursLeft = repairHoursLeft,
                    RepairTypeByte = damage.RepairTypeByte
                });
            }
            if (mapKeys.IsCreated) mapKeys.Dispose();

            PublishCivilianDamage(new CivilianDamageSnapshot(
                snapshot.Count == 0 ? Array.Empty<CivilianBuildingDamageDto>() : snapshot.ToArray(),
                damagedCount,
                repairingCount));
        }

        private static CivilianWarDamage MergeDuplicateDamage(CivilianWarDamage survivor, CivilianWarDamage duplicate)
        {
            survivor.HitCount = math.max(survivor.HitCount, duplicate.HitCount);
            if (!survivor.IsUnderRepair && duplicate.IsUnderRepair)
            {
                survivor.RepairEndHour = duplicate.RepairEndHour;
                survivor.RepairTypeByte = duplicate.RepairTypeByte;
            }
            else if (survivor.IsUnderRepair && duplicate.IsUnderRepair)
            {
                survivor.RepairEndHour = math.max(survivor.RepairEndHour, duplicate.RepairEndHour);
                if (survivor.RepairTypeByte == 0)
                    survivor.RepairTypeByte = duplicate.RepairTypeByte;
            }

            return survivor;
        }

        private void PublishCivilianDamage(CivilianDamageSnapshot snapshot)
        {
            m_DamageSnapshotSource = snapshot;
            m_DamageView.Publish(snapshot);
        }

        // ============================================================================
        // REPAIR PROCESSING
        // ============================================================================

        private bool TryResolveCivilianRepairTarget(
            in CivilianRepairIntent intent,
            bool allowMapRebuild,
            out Entity modEntity,
            out CivilianWarDamage damage)
        {
            if (TryResolveCivilianRepairTargetFromMap(intent, out modEntity, out damage))
                return true;

            if (!allowMapRebuild || m_DamageModEntityQuery.IsEmptyIgnoreFilter)
                return false;

            EnsureDamageMapFresh();
            return TryResolveCivilianRepairTargetFromMap(intent, out modEntity, out damage);
        }

        private bool TryResolveCivilianRepairTargetFromMap(
            in CivilianRepairIntent intent,
            out Entity modEntity,
            out CivilianWarDamage damage)
        {
            modEntity = Entity.Null;
            damage = default;
            return m_DamageByBuilding.TryGetValue(intent.Building.Index, out modEntity)
                && m_CivilianWarDamageLookup.TryGetComponent(modEntity, out damage)
                && damage.Building.Version == intent.Building.Version
                && !m_DeletedLookup.HasComponent(modEntity);
        }

        /// <summary>
        /// True iff the building referenced by the intent currently has a repair
        /// in progress (RepairEndHour > current hour). Lets the commit system
        /// distinguish "target missing" from "target busy" so it can emit a
        /// distinct CivilianRepairAlreadyActive ReasonId instead of a misleading
        /// CivilianRepairNotFound toast.
        /// </summary>
        public bool IsRepairTargetUnderRepair(in CivilianRepairIntent intent)
        {
            m_RepairLookups.RefreshIfStale();
            if (!TryResolveCivilianRepairTarget(intent, allowMapRebuild: true, out _, out var damage))
                return false;
            return damage.IsUnderRepair;
        }

        public bool ApplyRepairStart(ref CivilianRepairIntent intent, EntityCommandBuffer ecb)
        {
            m_RepairLookups.RefreshIfStale();

            if (intent.DurationHours <= 0f
                || !TryResolveCivilianRepairTarget(intent, allowMapRebuild: true, out Entity modEntity, out var damage))
            {
                return false;
            }

            if (damage.IsUnderRepair)
                return false;

            damage.RepairEndHour = (float)m_GameHour + intent.DurationHours;
            damage.RepairTypeByte = intent.RepairTypeByte;
#pragma warning disable CIVIC035 // TryResolveCivilianRepairTarget already proved modEntity has CivilianWarDamage.
            m_CivilianWarDamageLookup[modEntity] = damage;
#pragma warning restore CIVIC035

            if (intent.KickbackAmount > 0)
            {
                bool kickbackQueued = ShadowEconomyEmitter.TryQueueIncome(
                    World,
                    ecb,
                    intent.KickbackAmount,
                    $"CivRepairKB:{intent.Building.Index}",
                    $"CivRepairKB:{intent.RequestId}:{intent.Building.Index}");
                if (!kickbackQueued)
                    Log.Warn($"Civilian repair kickback failed: building {intent.Building.Index}, amount {intent.KickbackAmount}");
            }

#pragma warning disable CIVIC459 // PublishCivilianDamage below bumps the VersionedView; snapshot array has no separate version field.
            PublishRepairStartSnapshot(damage);
#pragma warning restore CIVIC459
            Log.Info($"Civilian repair started: building {intent.Building.Index}, " +
                     $"{intent.RepairType} {intent.DurationHours}h, ends at {damage.RepairEndHour:F1}h");
            return true;
        }

        public bool RefundRepair(in CivilianRepairIntent intent, EntityCommandBuffer ecb)
        {
            if (!intent.BudgetSucceeded || intent.Cost <= 0)
                return true;

            bool refunded;
            if (intent.RepairType == RepairType.ShadowOps)
            {
                refunded = ShadowEconomyEmitter.TryApplyRefund(
                    World,
                    intent.Cost,
                    $"CivRepairRefund:{intent.Building.Index}",
                    $"CivRepairRefund:{intent.RequestId}:{intent.Building.Index}");
            }
            else
            {
#pragma warning disable CIVIC182 // Phase-neutral budget refund helper lives with City budget service implementation.
                refunded = CivicSurvival.Services.City.BudgetTransactionResolver.QueueRefund(
                    ecb,
                    intent.Cost,
                    BudgetSource.CivRepairRefund,
                    BudgetIncomeKind.Refund);
#pragma warning restore CIVIC182
            }

            if (refunded)
                Log.Warn($"Civilian repair refund: building {intent.Building.Index}, amount {intent.Cost}");
            else
                Log.Error($"Civilian repair refund FAILED: building {intent.Building.Index}, amount {intent.Cost}");

            return refunded;
        }

        private void PublishRepairStartSnapshot(CivilianWarDamage damage)
        {
            var snapshot = m_SnapshotScratch;
            snapshot.Clear();

            var sourceBuildings = m_DamageSnapshotSource.Buildings;
            int damagedCount = 0;
            int repairingCount = 0;
            bool updated = false;

            int maxHits = GetHitsToDestroy(damage.GetBuildingEntity());
            float repairHoursLeft = (float)math.max(0.0, damage.RepairEndHour - m_GameHour);
            var updatedDto = new CivilianBuildingDamageDto
            {
                Building = new EntityRef(damage.Building.Index, damage.Building.Version),
                HitCount = damage.HitCount,
                MaxHits = maxHits,
                DamagePercent = maxHits > 0 ? (float)damage.HitCount / maxHits : 1f,
                IsRepairing = true,
                RepairHoursLeft = repairHoursLeft,
                RepairTypeByte = damage.RepairTypeByte
            };

            for (int i = 0; i < sourceBuildings.Count; i++)
            {
                var dto = sourceBuildings[i];
                if (dto.Building.Index == damage.Building.Index)
                {
                    dto = updatedDto;
                    updated = true;
                }

                snapshot.Add(dto);
                if (dto.IsRepairing) repairingCount++;
                else damagedCount++;
            }

            if (!updated)
            {
                snapshot.Add(updatedDto);
                repairingCount++;
            }

            PublishCivilianDamage(new CivilianDamageSnapshot(
                snapshot.Count == 0 ? Array.Empty<CivilianBuildingDamageDto>() : snapshot.ToArray(),
                damagedCount,
                repairingCount));
        }

        // ============================================================================
        // ICivilianDamageReader
        // ============================================================================

        public int DamagedCount => m_DamageSnapshotSource.DamagedCount;
        public int RepairingCount => m_DamageSnapshotSource.RepairingCount;

        (bool found, CivilianRepairView view) ICivilianDamageReader.GetRepairState(int buildingIndex, int buildingVersion)
        {
            m_RepairLookups.RefreshIfStale();
            bool found = TryGetRepairStateCore(buildingIndex, buildingVersion, out var view, out _);
            return (found, view);
        }

        bool ICivilianDamageReader.HasPendingRepairIntent(int buildingIndex, int buildingVersion) =>
            HasPendingCivilianRepairIntent(buildingIndex, buildingVersion);

        private bool TryGetRepairStateCore(
            int buildingIndex,
            int buildingVersion,
            out CivilianRepairView view,
            out CivilianWarDamage damage)
        {
            view = default;
            damage = default;

            if (!m_DamageModEntityQuery.IsEmptyIgnoreFilter)
                EnsureDamageMapFresh();

            if (!m_DamageByBuilding.TryGetValue(buildingIndex, out var modEntity)
                || !m_CivilianWarDamageLookup.TryGetComponent(modEntity, out damage)
                || damage.Building.Version != buildingVersion)
            {
                return false;
            }

            view = new CivilianRepairView
            {
                Building = damage.Building,
                HitCount = damage.HitCount,
                IsUnderRepair = damage.IsUnderRepair,
                RepairEndHour = damage.RepairEndHour
            };
            return true;
        }

        private bool HasPendingCivilianRepairIntent(int buildingIndex, int buildingVersion)
        {
            if (m_PendingCivilianRepairIntentQuery.IsEmptyIgnoreFilter)
                return false;

            var intents = m_PendingCivilianRepairIntentQuery.ToComponentDataArray<CivilianRepairIntent>(Allocator.Temp);
            try
            {
                for (int i = 0; i < intents.Length; i++)
                {
                    if (intents[i].Building.Index == buildingIndex
                        && intents[i].Building.Version == buildingVersion
                        && !intents[i].Applied)
                        return true;
                }
            }
            finally
            {
                if (intents.IsCreated)
                    intents.Dispose();
            }

            return false;
        }


        // ============================================================================
        // IPostLoadValidation
        // ============================================================================

        public int HydrationOrder => HydrationPriority.DEFAULT;
        public IReadOnlyList<Type> ReboundComponentTypes => s_ReboundComponentTypes;

        public void RebindBuildingRefsAfterLoad(EntityManager entityManager)
        {
            m_RepairLookups.Refresh();
            if (!m_DamageModEntityQuery.IsEmptyIgnoreFilter)
                RebuildDamageMap(publishSnapshot: false, processRepairCompletion: false);
        }

        public void ValidateAfterLoad()
        {
            // C-5: re-resolve on a fresh-world load (process-lifetime ref otherwise survives).
            if (m_threatGenerationClock == null && ServiceRegistry.IsInitialized)
                m_threatGenerationClock = ServiceRegistry.Instance.Require<ThreatGenerationClock>();

            m_UpdateCounter = UPDATE_INTERVAL; // Force immediate update on next frame
#pragma warning disable CIVIC005 // Intentional: 0.0 fallback — corrected by first OnUpdateImpl time refresh
            m_GameHour = GameTimeSystem.Instance?.Current.TotalGameHours ?? 0.0;
#pragma warning restore CIVIC005

            RebindBuildingRefsAfterLoad(EntityManager);

            if (SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
            {
                m_KnownAct = actSingleton.CurrentAct;
                m_KnownActInitialized = true;
            }
        }

        // ============================================================================
        // PUBLIC API
        // ============================================================================

        /// <summary>
        /// Clear in-frame ECB hit tracking. Call at start of each damage processing frame.
        /// </summary>
        public void ClearInFrameHits()
        {
            if (m_InFrameEcbHits.IsCreated) m_InFrameEcbHits.Clear();
            if (m_DeletionQueued.IsCreated) m_DeletionQueued.Clear();
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Clear();
            m_LookupsUpdatedThisFrame = false;
        }

        /// <summary>
        /// Record a hit on a non-PP building. Creates/updates CivilianWarDamage mod entity.
        /// </summary>
#pragma warning disable CIVIC231 // Called by ThreatDamageSystem — caller validates threat context
#pragma warning disable CIVIC097 // Index-only key safe: m_InFrameEcbHits cleared every frame
        public CivilianDamageResult RecordHit(Entity building, EntityCommandBuffer ecb, int impactThreatGeneration)
        {
#pragma warning restore CIVIC231
            if (!Enabled) return new CivilianDamageResult { IsValid = false };
            // Gate during act transition cleanup — new entities would be immediately swept
            // by the cleanup block in OnUpdateImpl:160-201, wasting ECB commands.
            // M-55 deadlock concern is resolved: cleanup always clears the flag (line 201)
            // and RequireAnyForUpdate passes if existing entities haven't been cleaned yet.
            if (m_PendingActTransitionCleanup) return new CivilianDamageResult { IsValid = false };

            // Lazy resolve at the cross-system entry point: RequireAnyForUpdate keeps this
            // system from starting (no OnStartRunning) until the first CivilianWarDamage
            // entity exists, and on a late playset activation the post-load
            // ValidateAfterLoad pass has already run — both assignment paths miss while
            // ThreatDamageSystem can already call in with the first hit of the session.
            // OnCreate resolution is barred by the registry-order analyzer.
            m_threatGenerationClock ??= ServiceRegistry.Instance.Require<ThreatGenerationClock>();

            // C-5 root: self-defend by threat generation. Unstamped (0) or stale
            // post-load zombie records no civilian damage.
            if (impactThreatGeneration == ThreatGenerationClock.Unstamped
                || impactThreatGeneration != m_threatGenerationClock.Current)
                return new CivilianDamageResult { IsValid = false };

            // PERF: Update lookups once per frame (must be before lazy rebuild)
            if (!m_LookupsUpdatedThisFrame)
            {
                m_CivilianWarDamageLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_DestroyedLookup.Update(this);
                m_PrefabRefLookup.Update(this);
                m_BuildingDataLookup.Update(this);
                m_LookupsUpdatedThisFrame = true;
            }

            if (building == Entity.Null || m_DeletedLookup.HasComponent(building) || m_DestroyedLookup.HasComponent(building))
                return new CivilianDamageResult { IsValid = false };

            // Lazy rebuild: close throttle gap after ECB playback. Gated on the archetype
            // order version — under mass damage on a large city this is called many times per
            // frame, and an unconditional rebuild forced one sync point + full scan per hit.
            EnsureDamageMapFresh();

            int hitsToDestroy = GetHitsToDestroy(building);
            CivilianWarDamage damage;
            int hitCount;

            if (m_DamageByBuilding.TryGetValue(building.Index, out Entity modEntity)
                && m_CivilianWarDamageLookup.TryGetComponent(modEntity, out var existingDamage)
                && existingDamage.Building.Version == building.Version
                && !m_DeletedLookup.HasComponent(modEntity))
            {
                // Branch A: existing mod entity — increment
                damage = existingDamage;
                // Cancel active repair on new hit — bombing interrupts repair
                if (damage.IsUnderRepair)
                {
                    damage.RepairEndHour = 0;
                    damage.RepairTypeByte = 0;
                    Log.Info($"Repair cancelled by new hit: building {building.Index}");
                }
                damage.HitCount++;
                hitCount = damage.HitCount;
                m_CivilianWarDamageLookup[modEntity] = damage;
            }
            else if (m_InFrameEcbHits.TryGetValue(building.Index, out var ecbHit)
                     && ecbHit.damage.Building.Version == building.Version)
            {
                // Branch B: M6 same-frame dedup — ECB entity exists but not materialized
                damage = ecbHit.damage;
                damage.HitCount++;
                hitCount = damage.HitCount;
                ecb.SetComponent(ecbHit.ecbEntity, damage);
                m_InFrameEcbHits[building.Index] = (ecbHit.ecbEntity, damage);
            }
            else
            {
                // Branch C: first hit — create mod entity
                damage = new CivilianWarDamage
                {
                    Building = BuildingRef.FromEntity(building),
                    HitCount = 1
                };
                hitCount = 1;

                var newEntity = ecb.CreateEntity();
                ecb.AddComponent(newEntity, damage);
                m_InFrameEcbHits[building.Index] = (newEntity, damage);
            }

            bool shouldDestroy = hitCount >= hitsToDestroy;
            if (shouldDestroy)
            {
                // H1 FIX: prevent duplicate destruction side-effects this frame.
                if (!m_DeletionQueued.Contains(building.Index))
                {
                    // Delete the mod entity (not the building — TDS handles building destruction).
                    // Civilian damage records are NOT kept on destruction: civilian buildings are
                    // numerous, and a kept sidecar per ruin would make RebuildDamageMap (an O(all
                    // sidecars) scan run per hit) grow with cumulative destruction. The destroyed
                    // count is surfaced by the attention-driven DESTROYED tile, not a per-building list.
                    Entity entityToDelete = Entity.Null;
                    if (m_DamageByBuilding.TryGetValue(building.Index, out var existing2))
                        entityToDelete = existing2;
                    else if (m_InFrameEcbHits.TryGetValue(building.Index, out var ecbHit2))
                        entityToDelete = ecbHit2.ecbEntity;
                    if (entityToDelete != Entity.Null)
                        QueueDamageEntityDeleted(ecb, entityToDelete);
                    m_DeletionQueued.Add(building.Index);
                }
            }

            if (Log.IsDebugEnabled) Log.Debug($"[CDS] building={building.Index} hits={hitCount}/{hitsToDestroy} destroy={shouldDestroy}");

            return new CivilianDamageResult
            {
                IsValid = true,
                ShouldDestroy = shouldDestroy,
                HitCount = hitCount,
                HitsToDestroy = hitsToDestroy
            };
#pragma warning restore CIVIC097
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

        private int GetHitsToDestroy(Entity building)
        {
            if (!m_PrefabRefLookup.TryGetComponent(building, out var prefabRef)) return 1;
            if (!m_BuildingDataLookup.TryGetComponent(prefabRef.m_Prefab, out var buildingData)) return 1;
            float area = buildingData.m_LotSize.x * buildingData.m_LotSize.y;
            return (int)math.max(1f, math.round(math.sqrt(area) * BalanceConfig.Current.Threats.CivilianDurabilityMultiplier));
        }

        private void OnActChanged(ActChangedEvent evt)
        {
            if (!m_KnownActInitialized)
            {
                m_KnownAct = evt.NewAct;
                m_KnownActInitialized = true;
                Log.Info($"[CivilianDamage] Act initialized from event ({evt.NewAct}), state preserved");
                return;
            }

            if (evt.NewAct == m_KnownAct)
            {
                Log.Info($"[CivilianDamage] Act re-published ({evt.NewAct}), state preserved");
                return;
            }
            m_KnownAct = evt.NewAct;

            m_UpdateCounter = UPDATE_INTERVAL;
            m_GameHour = GameTimeSystem.Instance?.Current.TotalGameHours ?? m_GameHour;
            m_PendingActTransitionCleanup =
                !m_DamageModEntityQuery.IsEmptyIgnoreFilter
                || !m_PendingCivilianRepairIntentQuery.IsEmptyIgnoreFilter;
            if (m_DamageByBuilding.IsCreated) m_DamageByBuilding.Clear();
            m_DamageMapOrderToken = int.MinValue;
            if (m_InFrameEcbHits.IsCreated) m_InFrameEcbHits.Clear();
            if (m_DeletionQueued.IsCreated) m_DeletionQueued.Clear();
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Clear();
            m_DamageSnapshotSource = CivilianDamageSnapshot.Empty;
            m_PendingActTransitionPublish = true;
            Log.Info($"Reset state on act transition → {evt.NewAct}");
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<ICivilianDamageReader>(this);
            }
            UnsubscribeSafe<ActChangedEvent>(OnActChanged);
            if (m_DamageByBuilding.IsCreated) m_DamageByBuilding.Dispose();
            if (m_InFrameEcbHits.IsCreated) m_InFrameEcbHits.Dispose();
            if (m_DeletionQueued.IsCreated) m_DeletionQueued.Dispose();
            if (m_DamageEntityDeletionQueued.IsCreated) m_DamageEntityDeletionQueued.Dispose();
            base.OnDestroy();
        }
    }
}
