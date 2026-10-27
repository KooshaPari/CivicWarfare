using System;
using System.Collections.Generic;
using System.Threading;
using Game;
using Game.Common;
using Game.Objects;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Narrative;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.Spotters.Systems
{
    /// <summary>
    /// Processes AddSpotterRequest entities from Narrative domain.
    ///
    /// Data-Driven Command pattern: Narrative creates request entities,
    /// this system processes them without direct domain coupling.
    ///
    /// IMPORTANT: Creates SEPARATE entities with SpotterData.
    /// NEVER AddComponent to vanilla buildings (causes homeless spike cascade).
    ///
    /// Uses RequireForUpdate - zero cost when no requests pending.
    /// </summary>
    [ActIndependent]
    public partial class SpotterRequestSystem : CivicSystemBase, IPostLoadValidation, IBuildingRefRebindOwner
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }
        public int RebindOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);
        private static void IncrementEcbCount() => Interlocked.Increment(ref s_EcbCommandCount);

        private static readonly LogContext Log = new("SpotterRequestSystem");
        private static readonly Type[] s_ReboundComponentTypes = { typeof(SpotterData) };
        private EntityQuery m_RequestQuery;
        private EntityQuery m_SpotterReconcileQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private EntityStorageInfoLookup m_BuildingStorageLookup;
        public IReadOnlyList<Type> ReboundComponentTypes => s_ReboundComponentTypes;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_RequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<AddSpotterRequest>()
            );

            RequireForUpdate(m_RequestQuery);

            // Post-load reconcile scans spotters from ValidateAfterLoad/RebindBuildingRefsAfterLoad,
            // which run outside this system's OnUpdate — a cached query is context-free, SystemAPI is not.
            m_SpotterReconcileQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<SpotterData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() }
            });

            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_BuildingStorageLookup = GetEntityStorageInfoLookup();

            Log.Info("Created (on-demand, zero cost when idle)");
        }

#pragma warning disable CIVIC284 // DestroyEntity calls intentionally excluded from count — EcbCount tracks created spotter entities only
        protected override void OnUpdateImpl()
        {
#pragma warning restore CIVIC284
            m_BuildingStorageLookup.Update(this);

            bool hasCharacterRequest = false;
            foreach (var request in SystemAPI.Query<RefRO<AddSpotterRequest>>())
            {
                if (request.ValueRO.IsCharacterSpotter)
                {
                    hasCharacterRequest = true;
                    break;
                }
            }

            // Build set of buildings that already have spotters + count for cap check (R9-H5)
            var buildingsWithSpotters = new NativeHashSet<long>(64, Allocator.Temp);
            var characterSpotters = new NativeList<Entity>(Allocator.Temp);
            int spotterCount = 0;
            EntityCommandBuffer ecb = default;
            bool hasCommands = false;
            foreach (var (spotter, spotterEntity) in
                SystemAPI.Query<RefRO<SpotterData>>().WithNone<Deleted>().WithEntityAccess())
            {
                // Skip spotters pending evacuation — don't count toward cap, don't block building slot
                if (spotter.ValueRO.IsEvacuating) continue;

                if (hasCharacterRequest && spotter.ValueRO.IsCharacterSpotter)
                    characterSpotters.Add(spotterEntity);

                spotterCount++;
                buildingsWithSpotters.Add(PackBuildingId(spotter.ValueRO.Building.Index, spotter.ValueRO.Building.Version));
            }
            var spotterCfg = BalanceConfig.Current.Spotter;
            int maxSpotters = spotterCfg.MaxSpotters;

            foreach (var (request, entity) in
                SystemAPI.Query<RefRO<AddSpotterRequest>>()
                .WithEntityAccess())
            {
                // NOTE: Reconstruct Entity from Index+Version (avoids vanilla orphan detection)
                var building = request.ValueRO.GetBuildingEntity();
                int districtIndex = request.ValueRO.DistrictIndex;
                int districtVersion = request.ValueRO.DistrictVersion;

                // Validate building exists and doesn't already have spotter
                if (!m_BuildingStorageLookup.Exists(building))
                {
                    if (Log.IsDebugEnabled) Log.Debug($"AddSpotterRequest: building {building.Index}:{building.Version} no longer exists — dropping request");
                    if (!hasCommands)
                    {
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        hasCommands = true;
                    }
                    ecb.DestroyEntity(entity);
                    continue;
                }

                long buildingKey = PackBuildingId(building.Index, building.Version);
                if (!buildingsWithSpotters.Contains(buildingKey))
                {
                    if (request.ValueRO.IsCharacterSpotter)
                    {
                        for (int i = 0; i < characterSpotters.Length; i++)
                        {
                            if (!hasCommands)
                            {
                                ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                                hasCommands = true;
                            }
                            ecb.DestroyEntity(characterSpotters[i]);
                        }

                        spotterCount -= characterSpotters.Length;
                        characterSpotters.Clear();
                    }

                    // R9-H5: Enforce MaxSpotters cap — drop excess requests explicitly
                    if (spotterCount >= maxSpotters)
                    {
                        Log.Info($"AddSpotterRequest dropped — cap reached ({maxSpotters})");
                        if (!hasCommands)
                        {
                            ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                            hasCommands = true;
                        }
                        ecb.DestroyEntity(entity);
                        continue;
                    }

                    // Create SEPARATE entity with SpotterData (NEVER AddComponent on vanilla!)
                    if (!hasCommands)
                    {
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        hasCommands = true;
                    }
                    var spotterEntity = ecb.CreateEntity();
                    ecb.AddComponent(spotterEntity, new SpotterData
                    {
                        Building = BuildingRef.FromEntity(building),
                        PenaltyContribution = spotterCfg.PenaltyPerSpotter,
                        IsActive = true,
                        ReactivateTime = 0,
                        DistrictIndex = districtIndex,
                        DistrictVersion = districtVersion,
                        IsCharacterSpotter = request.ValueRO.IsCharacterSpotter
                    });
                    IncrementEcbCount();
                    spotterCount++;

                    // Overwrite (not TryAdd) — updates version so recycled-building duplicates are blocked
                    buildingsWithSpotters.Add(buildingKey);

                    Log.Info($"SpotterData created via request (district {districtIndex})");
                }
                else
                {
                    if (Log.IsDebugEnabled) Log.Debug($"AddSpotterRequest: building {building.Index}:{building.Version} already has spotter — duplicate dropped");
                }

                // Destroy the request entity (not counted — EcbCount tracks spotter entities only)
                if (!hasCommands)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    hasCommands = true;
                }
                ecb.DestroyEntity(entity);
            }

            buildingsWithSpotters.Dispose();
            characterSpotters.Dispose();

            if (hasCommands)
            {
                m_GameSimulationEndBarrier.AddJobHandleForProducer(Dependency);
            }
        }

        public void ValidateAfterLoad()
        {
            ReconcileValeraSpotterFromNarrative();
        }

        public void RebindBuildingRefsAfterLoad(EntityManager entityManager)
        {
            ReconcileValeraSpotterFromNarrative();
        }

        private void ReconcileValeraSpotterFromNarrative()
        {
            var bindings = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullNarrativeCharacterBindings.Instance);
            var building = bindings.GetBoundEntity("Valera");
            if (building == Entity.Null)
                return;

            if (!EntityManager.Exists(building) || EntityManager.HasComponent<Deleted>(building))
                return;

            var spotterCfg = BalanceConfig.Current.Spotter;
            long desiredKey = PackBuildingId(building.Index, building.Version);
            Entity keep = Entity.Null;
            int activeCount = 0;
            var characterSpottersToDestroy = new NativeList<Entity>(Allocator.Temp);

            using var reconcileEntities = m_SpotterReconcileQuery.ToEntityArray(Allocator.Temp);
            using var reconcileSpotters = m_SpotterReconcileQuery.ToComponentDataArray<SpotterData>(Allocator.Temp);
            for (int i = 0; i < reconcileEntities.Length; i++)
            {
                var spotter = reconcileSpotters[i];
                var entity = reconcileEntities[i];
                if (!spotter.IsEvacuating)
                    activeCount++;

                if (!spotter.IsCharacterSpotter)
                    continue;

                long key = PackBuildingId(spotter.Building.Index, spotter.Building.Version);
                if (key == desiredKey && keep == Entity.Null)
                {
                    keep = entity;
                    continue;
                }

                characterSpottersToDestroy.Add(entity);
                if (!spotter.IsEvacuating)
                    activeCount--;
            }

            for (int i = 0; i < characterSpottersToDestroy.Length; i++)
                EntityManager.DestroyEntity(characterSpottersToDestroy[i]);
            characterSpottersToDestroy.Dispose();

            if (keep != Entity.Null)
                return;

            if (activeCount >= spotterCfg.MaxSpotters)
            {
                Log.Warn($"Valera spotter reconcile deferred — cap reached ({spotterCfg.MaxSpotters})");
                return;
            }

            var spotterEntity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(spotterEntity, new SpotterData
            {
                Building = BuildingRef.FromEntity(building),
                PenaltyContribution = spotterCfg.PenaltyPerSpotter,
                IsActive = true,
                ReactivateTime = 0,
                DistrictIndex = -1,
                DistrictVersion = 0,
                IsCharacterSpotter = true
            });

            IncrementEcbCount();
            Log.Info($"Valera spotter reconciled from Narrative binding ({building.Index}:{building.Version})");
        }

        private static long PackBuildingId(int index, int version)
        {
            return ((long)index << 32) | (uint)version;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Log.Info("Destroyed");
        }
    }
}
