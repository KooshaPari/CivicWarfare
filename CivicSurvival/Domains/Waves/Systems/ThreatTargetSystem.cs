using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Waves.Systems
{
    /// <summary>
    /// Collects threat statistics and targets for simulation.
    /// Writes to ThreatStatsSingleton (Single Source of Truth for threat counts).
    /// Groups threats by target for UI display.
    ///
    /// Radar visualization moved to ThreatRadarSystem (Systems/UI).
    /// </summary>
    [SingletonOwner(typeof(ThreatStatsSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    [ActIndependent]
    public partial class ThreatTargetSystem : CivicSystemBase, IThreatTargetReader, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ThreatTargetSystem");

        private PrefabSystem m_PrefabSystem = null!;

        // Cached queries
        private EntityQuery m_ShahedQuery;
        private EntityQuery m_BallisticQuery;

        // PERF: Type handles for chunk iteration (avoids ToComponentDataArray allocations)
        private ComponentTypeHandle<Shahed> m_ShahedHandle;
        private ComponentTypeHandle<ShahedCombatState> m_CombatStateHandle;
        private ComponentTypeHandle<Ballistic> m_BallisticHandle;
        private ComponentTypeHandle<BallisticInterceptState> m_BallisticInterceptHandle;
        private ComponentTypeHandle<ThreatPosition> m_ThreatPositionHandle;
        private ComponentTypeHandle<ActiveThreat> m_ActiveThreatHandle;
        private ComponentTypeHandle<PendingDestruction> m_PendingDestructionHandle;

        // Cached targets (updated each frame during attack)
        // PERF: Pre-allocated large capacity to avoid resize spikes
        private List<ThreatTargetDto> m_CachedTargets = new(64);
        private readonly VersionedView<ThreatTargetSnapshot> m_TargetsView = new(ThreatTargetSnapshot.Empty);
        private ThreatTargetSnapshot m_LastPublishedTargetSnapshot = ThreatTargetSnapshot.Empty;
        public IVersionedView<ThreatTargetSnapshot> TargetsView => m_TargetsView;

        // PERF: Persistent dictionary (reused via Clear(), no allocation per frame)
        // Key: TargetBuilding Index+Version at spawn time.
        [NonEntityIndex] private Dictionary<long, ThreatTargetDto> m_TargetGroups = new(64);

        // PERF: Pool for ThreatInfoDto lists (avoid allocations per target)
        private List<List<ThreatInfoDto>> m_ThreatListPool = new(32);

        // PERF: Entity name cache (PrefabSystem lookup is expensive)
        [NonEntityIndex] private Dictionary<long, string> m_EntityNameCache = new(64);
        // Building geometry (prefab size + placement yaw) is static — resolved once per
        // (Index,Version) and reused, just like the name cache above. xyz = size, w = rotationY.
        [NonEntityIndex] private Dictionary<long, float4> m_TargetGeometryCache = new(64);

        // Cached stats (written to ThreatStatsSingleton)
        private int m_ActiveShahedCount;
        private int m_ActiveBallisticCount;

        // PERF: Throttle to 10Hz (UI doesn't need 60fps updates)
        private ThrottleHelper m_Throttle;

        // Track active state to zero out singleton on threat end (T1-2 fix)
        private bool m_WasActive;

        // ComponentLookup for PrefabRef (replaces EntityManager data access in GetEntityName)
        private ComponentLookup<PrefabRef> m_PrefabRefLookup;

        // ComponentLookups for target-building geometry (radar 2.5D box):
        // Transform on the target entity (yaw), ObjectGeometryData on its prefab (m_Size).
        private ComponentLookup<Game.Objects.Transform> m_TransformLookup;
        private ComponentLookup<ObjectGeometryData> m_ObjectGeometryLookup;

        // Render-write barrier: same-frame building Transform reads route through the
        // BuildingTransform ticket (zero current producers → Consume is free).
        private IRenderWriteBarrier m_RenderWriteBarrier = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Create queries for threat statistics collection
#pragma warning disable CIVIC340 // Threat stat queries intentionally include disabled PendingDestruction entities as active/live.
            // Exclude<PlayerOutboundThreat>: the defensive threat counters / UI targets count only
            // inbound waves. Outbound player counter-strikes (enabled bit) are tracked separately
            // for the attack-side 2D window, not in the inbound stats singleton. The enableable
            // exclude drops chunks where the bit is enabled, so inbound waves (bit disabled) stay.
            m_ShahedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Shahed>(),
                ComponentType.ReadOnly<ActiveThreat>(),
                ComponentType.Exclude<PendingDestruction>(),
                ComponentType.Exclude<PlayerOutboundThreat>()
            );

            m_BallisticQuery = GetEntityQuery(
                ComponentType.ReadOnly<Ballistic>(),
                ComponentType.ReadOnly<ActiveThreat>(),
                ComponentType.Exclude<PendingDestruction>(),
                ComponentType.Exclude<PlayerOutboundThreat>()
            );
#pragma warning restore CIVIC340

            // PERF: Type handles for chunk iteration
            m_ShahedHandle = GetComponentTypeHandle<Shahed>(true);
            m_CombatStateHandle = GetComponentTypeHandle<ShahedCombatState>(true);
            m_BallisticHandle = GetComponentTypeHandle<Ballistic>(true);
            m_BallisticInterceptHandle = GetComponentTypeHandle<BallisticInterceptState>(true);
            m_ThreatPositionHandle = GetComponentTypeHandle<ThreatPosition>(true);
            m_ActiveThreatHandle = GetComponentTypeHandle<ActiveThreat>(true);
            m_PendingDestructionHandle = GetComponentTypeHandle<PendingDestruction>(true);

            m_PrefabRefLookup = GetComponentLookup<PrefabRef>(true);
            m_TransformLookup = GetComponentLookup<Game.Objects.Transform>(true);
            m_ObjectGeometryLookup = GetComponentLookup<ObjectGeometryData>(true);

            // NOTE: No RequireAnyForUpdate -- system must run one extra cycle after threats end
            // to zero out ThreatStatsSingleton. Otherwise HasActiveThreats stays stale=true.

            // Domain-Driven Initialization (Static Factory)
            ThreatStatsSingleton.EnsureExists(EntityManager);

            // PERF: 10Hz throttle (60fps / 6 = 10 updates/sec)
            // Drone at 50m/s moves only 5m between updates - imperceptible
            const int THROTTLE_FRAME_INTERVAL = 6;
            m_Throttle = new ThrottleHelper(THROTTLE_FRAME_INTERVAL);

            // PERF M2.1: Pre-warm pool with initial capacity to avoid runtime allocations
            for (int i = 0; i < 8; i++)
            {
                m_ThreatListPool.Add(new List<ThreatInfoDto>(8));
            }

            m_TargetsView.Publish(ThreatTargetSnapshot.Empty);

#pragma warning disable CIVIC098 // ServiceRegistry.Instance is initialized in Mod.OnLoad before any system OnCreate runs.
            ServiceRegistry.Instance.Register<IThreatTargetReader>(this);
#pragma warning restore CIVIC098

            Log.Info(" Created (throttled 10Hz)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2: OnCreate doesn't re-run on new-game.
            ThreatStatsSingleton.EnsureExists(EntityManager);
            m_RenderWriteBarrier ??= ServiceRegistry.Instance.Require<IRenderWriteBarrier>();
        }

        public void ValidateAfterLoad()
        {
            ThreatStatsSingleton.EnsureExists(EntityManager);

            bool hasThreats = !m_ShahedQuery.IsEmpty || !m_BallisticQuery.IsEmpty;
            if (hasThreats)
            {
                CollectTargets();
                m_WasActive = true;
                return;
            }

            m_ActiveShahedCount = 0;
            m_ActiveBallisticCount = 0;
            UpdateThreatStatsSingleton();
            PublishEmptyTargets();
            m_WasActive = false;
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IThreatTargetReader>(this);
            }
            base.OnDestroy();
            m_CachedTargets.Clear();
            m_TargetGroups.Clear();
            m_ThreatListPool.Clear();
            m_EntityNameCache.Clear();
            m_TargetGeometryCache.Clear();
            Log.Info(" Destroyed");
        }

        protected override void OnUpdateImpl()
        {
            bool hasThreats = !m_ShahedQuery.IsEmpty || !m_BallisticQuery.IsEmpty;

            // PERF: Skip entirely when no threats AND singleton already zeroed
            if (!hasThreats && !m_WasActive)
                return;

            // L13 FIX: Zero out immediately when threats gone (don't throttle the cleanup).
            // Prevents 100ms stale TotalActiveCount after last threat dies.
            if (!hasThreats && m_WasActive)
            {
                m_ActiveShahedCount = 0;
                m_ActiveBallisticCount = 0;
                // Return lists to pool before clearing (same pattern as CollectTargets)
                foreach (var kvp in m_TargetGroups)
                    ReturnThreatList(kvp.Value.Threats);
                m_CachedTargets.Clear();
                m_TargetGroups.Clear();
                m_EntityNameCache.Clear();
                m_TargetGeometryCache.Clear();
                UpdateThreatStatsSingleton();
                PublishTargets();
                m_WasActive = false;
                return;
            }

            // PERF: Throttle all work to 10Hz -- WaveExecutor tolerates 100ms staleness
            // for wave phase transitions. Eliminates per-frame TryGetSingletonRW sync point.
            if (!m_Throttle.ShouldUpdate())
            {
                m_WasActive = hasThreats;
                return;
            }

            using (PerformanceProfiler.Measure("ThreatTarget.OnUpdate"))
            {
                // CollectTargets() counts threats via chunk iteration (checks IsIntercepted),
                // groups by target, and writes singleton -- no separate count pass needed.
                CollectTargets();
            }

            m_WasActive = hasThreats;
        }

        /// <summary>
        /// Force this system to fire on the next frame regardless of throttle counter.
        /// Called by WaveExecutor on phase transitions when immediate stats are needed.
        /// </summary>
        public void ForceNextUpdate() => m_Throttle.ForceNextFire();

        private List<ThreatInfoDto> RentThreatList()
        {
            if (m_ThreatListPool.Count > 0)
            {
                var list = m_ThreatListPool[m_ThreatListPool.Count - 1];
                m_ThreatListPool.RemoveAt(m_ThreatListPool.Count - 1);
                list.Clear();
                return list;
            }
#pragma warning disable CIVIC050 // pool miss fallback, infrequent
            return new List<ThreatInfoDto>(8);
#pragma warning restore CIVIC050
        }

        private void ReturnThreatList(List<ThreatInfoDto> list)
        {
            list.Clear();
            m_ThreatListPool.Add(list);
        }

        private void CollectTargets()
        {
            // PERF: Return lists to pool before clearing
            foreach (var kvp in m_TargetGroups)
            {
                ReturnThreatList(kvp.Value.Threats);
            }

            m_CachedTargets.Clear();
            m_TargetGroups.Clear();

            // Reset stats
            m_ActiveShahedCount = 0;
            m_ActiveBallisticCount = 0;

            // PERF: Update handles once per frame (M11 FIX: moved inside throttled path)
            m_PrefabRefLookup.Update(this);
            m_TransformLookup.Update(this);
            m_ObjectGeometryLookup.Update(this);
            m_ShahedHandle.Update(this);
            m_CombatStateHandle.Update(this);
            m_BallisticHandle.Update(this);
            m_BallisticInterceptHandle.Update(this);
            m_ThreatPositionHandle.Update(this);
            m_ActiveThreatHandle.Update(this);
            m_PendingDestructionHandle.Update(this);

            // Same-frame building Transform reads (radar box yaw) sit behind the
            // BuildingTransform ticket. Zero current producers → Consume completes
            // instantly (no handles to wait on).
            var renderTicket = m_RenderWriteBarrier.Consume(GetType(), RenderWriteComponentMask.BuildingTransform);

            // Collect Shaheds using chunk iteration (avoids ToComponentDataArray allocation)
            if (!m_ShahedQuery.IsEmpty)
            {
                var chunks = m_ShahedQuery.ToArchetypeChunkArray(Allocator.Temp);
                if (Log.IsDebugEnabled)
                    PerformanceProfiler.RecordAllocation("TTS.ShahedChunks", chunks.Length * 16);
                for (int c = 0; c < chunks.Length; c++)
                {
                    var chunk = chunks[c];
                    var shaheds = chunk.GetNativeArray(ref m_ShahedHandle);
                    var combatStates = chunk.GetNativeArray(ref m_CombatStateHandle);
                    var activeMask = chunk.GetEnabledMask(ref m_ActiveThreatHandle);
                    bool hasPendingDestruction = chunk.Has(ref m_PendingDestructionHandle);
                    var pendingDestructionMask = hasPendingDestruction
                        ? chunk.GetEnabledMask(ref m_PendingDestructionHandle)
                        : default;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (!activeMask.GetBit(i)) continue;
                        if (hasPendingDestruction && pendingDestructionMask.GetBit(i)) continue;

                        var shahed = shaheds[i];
                        if (combatStates[i].IsIntercepted) continue;

                        m_ActiveShahedCount++;

                        long targetKey = GetTargetGroupKey(shahed.TargetBuilding.Index, shahed.TargetBuilding.Version);
                        float remaining = math.max(0f, shahed.TotalDistance - shahed.CurrentDistance);
                        int eta = shahed.Speed > 0 ? (int)Math.Floor(remaining / shahed.Speed) : 0;

                        if (!m_TargetGroups.TryGetValue(targetKey, out var info))
                        {
                            ResolveTargetGeometry(renderTicket, shahed.TargetBuilding.Index, shahed.TargetBuilding.Version,
                                out float3 size, out float rotationY);
                            info = new ThreatTargetDto
                            {
                                EntityIndex = shahed.TargetBuilding.Index,
                                EntityVersion = shahed.TargetBuilding.Version,
                                Position = shahed.TargetPosition,
                                Name = GetEntityName(shahed.TargetBuilding.Index, shahed.TargetBuilding.Version),
                                SizeX = size.x,
                                SizeY = size.y,
                                SizeZ = size.z,
                                RotationY = rotationY,
                                Threats = RentThreatList()
                            };
                            m_TargetGroups[targetKey] = info;
                        }

                        info.Threats.Add(new ThreatInfoDto
                        {
                            Type = "shahed",
                            EtaSeconds = eta,
                            DistanceMeters = (int)remaining
                        });

                        m_TargetGroups[targetKey] = info;
                    }
                }
                if (chunks.IsCreated) chunks.Dispose();
            }

            // Collect Ballistics using chunk iteration
            if (!m_BallisticQuery.IsEmpty)
            {
                var chunks = m_BallisticQuery.ToArchetypeChunkArray(Allocator.Temp);
                for (int c = 0; c < chunks.Length; c++)
                {
                    var chunk = chunks[c];
                    var ballistics = chunk.GetNativeArray(ref m_BallisticHandle);
                    var interceptStates = chunk.GetNativeArray(ref m_BallisticInterceptHandle);
                    var positions = chunk.GetNativeArray(ref m_ThreatPositionHandle);
                    var activeMask = chunk.GetEnabledMask(ref m_ActiveThreatHandle);
                    bool hasPendingDestruction = chunk.Has(ref m_PendingDestructionHandle);
                    var pendingDestructionMask = hasPendingDestruction
                        ? chunk.GetEnabledMask(ref m_PendingDestructionHandle)
                        : default;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (!activeMask.GetBit(i)) continue;
                        if (hasPendingDestruction && pendingDestructionMask.GetBit(i)) continue;

                        var ballistic = ballistics[i];
                        if (interceptStates[i].IsIntercepted) continue;

                        m_ActiveBallisticCount++;

                        long targetKey = GetTargetGroupKey(ballistic.TargetBuilding.Index, ballistic.TargetBuilding.Version);
#pragma warning disable CIVIC078 // sqrt needed for ETA (distance/speed) and UI display in meters
                        float remaining = math.distance(positions[i].Position, ballistic.TargetPosition);
#pragma warning restore CIVIC078
                        int eta = ballistic.Speed > 0 ? (int)Math.Floor(remaining / ballistic.Speed) : 0;

                        if (!m_TargetGroups.TryGetValue(targetKey, out var info))
                        {
                            ResolveTargetGeometry(renderTicket, ballistic.TargetBuilding.Index, ballistic.TargetBuilding.Version,
                                out float3 size, out float rotationY);
                            info = new ThreatTargetDto
                            {
                                EntityIndex = ballistic.TargetBuilding.Index,
                                EntityVersion = ballistic.TargetBuilding.Version,
                                Position = ballistic.TargetPosition,
                                Name = GetEntityName(ballistic.TargetBuilding.Index, ballistic.TargetBuilding.Version),
                                SizeX = size.x,
                                SizeY = size.y,
                                SizeZ = size.z,
                                RotationY = rotationY,
                                Threats = RentThreatList()
                            };
                            m_TargetGroups[targetKey] = info;
                        }

                        info.Threats.Add(new ThreatInfoDto
                        {
                            Type = "ballistic",
                            EtaSeconds = eta,
                            DistanceMeters = (int)remaining
                        });

                        m_TargetGroups[targetKey] = info;
                    }
                }
                if (chunks.IsCreated) chunks.Dispose();
            }

            // Convert to list and calculate aggregates
            foreach (var kvp in m_TargetGroups)
            {
                var info = kvp.Value;

                // Sort threats by ETA
                info.Threats.Sort((a, b) => a.EtaSeconds.CompareTo(b.EtaSeconds));

                // Calculate aggregates
                info.ThreatCount = info.Threats.Count;
                info.MinEtaSeconds = info.Threats.Count > 0 ? info.Threats[0].EtaSeconds : 0;

                m_CachedTargets.Add(info);
            }

            // Sort targets by min ETA (most urgent first)
            m_CachedTargets.Sort((a, b) => a.MinEtaSeconds.CompareTo(b.MinEtaSeconds));
            PublishTargets();

            // Clear stale caches if no threats
            if (m_ActiveShahedCount + m_ActiveBallisticCount == 0)
            {
                m_EntityNameCache.Clear();
                m_TargetGeometryCache.Clear();
            }

            // NOTE: UpdateThreatStatsSingleton() already called in UpdateThreatCounts()
            // CollectTargets() provides more accurate counts (checks IsIntercepted),
            // so we update singleton again with precise values
            UpdateThreatStatsSingleton();
        }

        private static long GetTargetGroupKey(int index, int version)
            => ((long)version << 32) ^ (uint)index;

        private void PublishTargets()
        {
            if (m_CachedTargets.Count == 0)
            {
                PublishEmptyTargets();
                return;
            }

            var current = new ThreatTargetSnapshot(m_CachedTargets);
            if (s_TargetSnapshotComparer.Equals(m_LastPublishedTargetSnapshot, current))
                return;

            var targets = new ThreatTargetDto[m_CachedTargets.Count];
            for (int i = 0; i < m_CachedTargets.Count; i++)
            {
                var target = m_CachedTargets[i];
#pragma warning disable CIVIC050 // Defensive snapshot copy: m_CachedTargets mutates next cycle; published snapshot must own its data
                target.Threats = target.Threats == null || target.Threats.Count == 0
                    ? new List<ThreatInfoDto>(0)
                    : new List<ThreatInfoDto>(target.Threats);
#pragma warning restore CIVIC050
                targets[i] = target;
            }

            var snapshot = new ThreatTargetSnapshot(targets);
            m_LastPublishedTargetSnapshot = snapshot;
            m_TargetsView.Publish(snapshot);
        }

        private void PublishEmptyTargets()
        {
            if (s_TargetSnapshotComparer.Equals(m_LastPublishedTargetSnapshot, ThreatTargetSnapshot.Empty))
                return;

            m_LastPublishedTargetSnapshot = ThreatTargetSnapshot.Empty;
            m_TargetsView.Publish(ThreatTargetSnapshot.Empty);
        }

        private static readonly IEqualityComparer<ThreatTargetSnapshot> s_TargetSnapshotComparer =
            new ThreatTargetSnapshotComparer();

        private sealed class ThreatTargetSnapshotComparer : IEqualityComparer<ThreatTargetSnapshot>
        {
            private const int HashSeed = 17;
            private const int HashMultiplier = 31;

            public bool Equals(ThreatTargetSnapshot x, ThreatTargetSnapshot y)
            {
                var left = x.Targets ?? Array.Empty<ThreatTargetDto>();
                var right = y.Targets ?? Array.Empty<ThreatTargetDto>();
                if (left.Count != right.Count)
                    return false;

                for (int i = 0; i < left.Count; i++)
                {
                    if (!Equals(left[i], right[i]))
                        return false;
                }

                return true;
            }

            public int GetHashCode(ThreatTargetSnapshot obj)
            {
                var targets = obj.Targets ?? Array.Empty<ThreatTargetDto>();
                unchecked
                {
                    int hash = HashSeed;
                    for (int i = 0; i < targets.Count; i++)
                    {
                        hash = (hash * HashMultiplier) + targets[i].EntityIndex;
                        hash = (hash * HashMultiplier) + targets[i].EntityVersion;
                        hash = (hash * HashMultiplier) + targets[i].Position.GetHashCode();
                        hash = (hash * HashMultiplier) + targets[i].ThreatCount;
                        hash = (hash * HashMultiplier) + targets[i].MinEtaSeconds;
                        hash = (hash * HashMultiplier) + targets[i].SizeX.GetHashCode();
                        hash = (hash * HashMultiplier) + targets[i].SizeY.GetHashCode();
                        hash = (hash * HashMultiplier) + targets[i].SizeZ.GetHashCode();
                        hash = (hash * HashMultiplier) + targets[i].RotationY.GetHashCode();
                        hash = (hash * HashMultiplier) + StringComparer.Ordinal.GetHashCode(targets[i].Name ?? string.Empty);
                        hash = (hash * HashMultiplier) + GetThreatsHashCode(targets[i].Threats);
                    }

                    return hash;
                }
            }

            private static bool Equals(ThreatTargetDto left, ThreatTargetDto right)
            {
                return left.EntityIndex == right.EntityIndex
                    && left.EntityVersion == right.EntityVersion
                    && left.Position.Equals(right.Position)
                    && left.ThreatCount == right.ThreatCount
                    && left.MinEtaSeconds == right.MinEtaSeconds
                    && left.SizeX.Equals(right.SizeX)
                    && left.SizeY.Equals(right.SizeY)
                    && left.SizeZ.Equals(right.SizeZ)
                    && left.RotationY.Equals(right.RotationY)
                    && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                    && ThreatListsEqual(left.Threats, right.Threats);
            }

            private static bool ThreatListsEqual(IReadOnlyList<ThreatInfoDto>? left, IReadOnlyList<ThreatInfoDto>? right)
            {
                left ??= Array.Empty<ThreatInfoDto>();
                right ??= Array.Empty<ThreatInfoDto>();
                if (left.Count != right.Count)
                    return false;

                for (int i = 0; i < left.Count; i++)
                {
                    if (left[i].EtaSeconds != right[i].EtaSeconds
                        || left[i].DistanceMeters != right[i].DistanceMeters
                        || !string.Equals(left[i].Type, right[i].Type, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static int GetThreatsHashCode(IReadOnlyList<ThreatInfoDto>? threats)
            {
                threats ??= Array.Empty<ThreatInfoDto>();
                unchecked
                {
                    int hash = HashSeed;
                    for (int i = 0; i < threats.Count; i++)
                    {
                        hash = (hash * HashMultiplier) + StringComparer.Ordinal.GetHashCode(threats[i].Type ?? string.Empty);
                        hash = (hash * HashMultiplier) + threats[i].EtaSeconds;
                        hash = (hash * HashMultiplier) + threats[i].DistanceMeters;
                    }

                    return hash;
                }
            }
        }

        /// <summary>
        /// Write stats to ECS singleton for other systems to read.
        /// MED FIX: Use TryGetSingletonRW to avoid TOCTOU race.
        /// </summary>
        private void UpdateThreatStatsSingleton()
        {
            if (!SystemAPI.TryGetSingletonRW<ThreatStatsSingleton>(out var singleton))
                return;

            singleton.ValueRW.ActiveShahedCount = m_ActiveShahedCount;
            singleton.ValueRW.ActiveBallisticCount = m_ActiveBallisticCount;
        }

        /// <summary>
        /// Resolve target-building geometry for the radar 2.5D box: footprint size
        /// (ObjectGeometryData.m_Size on the prefab entity) and yaw around Y
        /// (Transform.m_Rotation on the target entity). Degrades to zero size /
        /// zero rotation when the target has no geometry (ground target with
        /// EntityIndex &lt;= 0, demolished building, prefab missing) so the UI
        /// falls back to the flat marker.
        /// </summary>
        private void ResolveTargetGeometry(RenderWriteTicket renderTicket, int entityIndex, int entityVersion,
            out float3 size, out float rotationY)
        {
            size = float3.zero;
            rotationY = 0f;

            if (entityIndex <= 0)
                return;

            // Building geometry is static (a building never moves or resizes), so resolve it once
            // per (Index,Version) and reuse — the 10Hz rebuild was re-doing these lookups + atan2
            // every cycle for every target building. Rebuild/recycle bumps the entity version → new
            // key → natural re-resolve. Same caching the name lookup below already does.
            long cacheKey = ((long)entityVersion << 32) | (uint)entityIndex;
            if (m_TargetGeometryCache.TryGetValue(cacheKey, out var cachedGeometry))
            {
                size = cachedGeometry.xyz;
                rotationY = cachedGeometry.w;
                return;
            }

            EnsureRenderTicket(renderTicket, RenderWriteComponentMask.BuildingTransform);

            var entity = new Entity { Index = entityIndex, Version = entityVersion };

            if (m_TransformLookup.TryGetComponent(entity, out var transform))
            {
                // Yaw around Y from the building rotation: project the forward
                // vector onto the XZ plane (atan2 of its X/Z components).
                float3 forward = math.mul(transform.m_Rotation, math.forward());
                rotationY = math.atan2(forward.x, forward.z);
            }

            if (m_PrefabRefLookup.TryGetComponent(entity, out var prefabRef)
                && m_ObjectGeometryLookup.TryGetComponent(prefabRef.m_Prefab, out var geometry))
            {
                size = geometry.m_Size;
            }
            else
            {
                // No silent 2D fallback on the UI side: a targeted building with no
                // ObjectGeometryData is an anomaly. Surface it loudly instead of
                // letting the radar quietly degrade the 3D box. Caching the result also
                // collapses this Warn to once per building instead of 10x/sec.
                Log.Warn($"Target building {entityIndex} has no ObjectGeometryData — radar box uses minimum size");
            }

            m_TargetGeometryCache[cacheKey] = new float4(size, rotationY);
        }

        private static void EnsureRenderTicket(RenderWriteTicket renderTicket, RenderWriteComponentMask requiredMask)
        {
            if (!renderTicket.Covers(requiredMask))
                throw new InvalidOperationException($"Render write ticket does not cover {requiredMask}");
        }

        /// <summary>
        /// Get cached building name by entity index.
        /// PERF: PrefabSystem lookup is expensive, cache results.
        /// </summary>
        private string GetEntityName(int entityIndex, int entityVersion)
        {
            if (entityIndex <= 0)
                return "Ground Target";

            // L6 FIX: Key by (Index, Version) to avoid stale names after entity recycling
            long cacheKey = ((long)entityVersion << 32) | (uint)entityIndex;

            // Check cache first
            if (m_EntityNameCache.TryGetValue(cacheKey, out var cached))
                return cached;

            // Try to find entity and get prefab name
            string name;
            var entity = new Entity { Index = entityIndex, Version = entityVersion };

            if (m_PrefabRefLookup.TryGetComponent(entity, out var prefabRef))
            {
                if (m_PrefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) && prefab != null)
                {
                    name = prefab.name;
                }
                else
                {
                    name = $"Building #{entityIndex}";
                }
            }
            else
            {
                name = $"Building #{entityIndex}";
            }

            m_EntityNameCache[cacheKey] = name;
            return name;
        }
    }
}

