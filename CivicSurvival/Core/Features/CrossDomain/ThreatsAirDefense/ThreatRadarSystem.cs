using System;
using System.Collections.Generic;
using Game;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Domain.AirDefense;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Core.Features.CrossDomain.ThreatsAirDefense
{
    /// <summary>
    /// UI system for radar visualization of threats.
    ///
    /// Reads threat data from RadarThreatBuffer (populated by ThreatMovementSystem).
    /// Zero queries needed - data is already prepared by movement system.
    ///
    /// Separated from ThreatTargetSystem to isolate UI concerns from simulation.
    ///
    /// NOTE: Located in Services/UI/ (not Core/) because it imports domain namespaces.
    /// Core should remain domain-agnostic.
    /// </summary>
    [ActIndependent]
    [ServiceOnlySystem("On-demand radar reader; consumers call RadarView/IThreatRadarReader directly, no per-frame work.")]
    public partial class ThreatRadarSystem : CivicSystemBase, IThreatRadarReader, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private const int DEFAULT_MAP_EXTENT = 7168;

        private static readonly LogContext Log = new("ThreatRadarSystem");
        private IThreatTargetReader m_TargetReader = null!;
        private IAirDefenseCoverageReader? m_CoverageReader;
        private IMapBoundsReader? m_MapBounds;
        private EntityQuery m_RadarSingletonQuery;

        // Radar data (real-time positions for UI visualization)
        // PERF: Pre-allocated large capacity to avoid List resize spikes during gameplay
        // 256 threats = heavy attack wave, 64 targets = multiple buildings under attack
        private List<RadarThreatDto> m_CachedRadarThreats = new(256);
        private List<RadarTargetDto> m_CachedRadarTargets = new(64);
        private List<RadarDefenseDto> m_CachedRadarDefenses = new(16);
        private float3 m_MapMin = new(-DEFAULT_MAP_EXTENT, 0, -DEFAULT_MAP_EXTENT);
        private float3 m_MapMax = new(DEFAULT_MAP_EXTENT, 0, DEFAULT_MAP_EXTENT);
        private bool m_MapBoundsCached;
        private bool m_MapBoundsMissingWarned;

        // Frame-scoped cache (invalidates automatically each frame)
        private int m_CacheFrame = -1;

        private static readonly IEqualityComparer<ThreatRadarSnapshot> s_RadarSnapshotComparer =
            new ThreatRadarSnapshotComparer();

        private readonly VersionedView<ThreatRadarSnapshot> m_RadarView = new(ThreatRadarSnapshot.Empty);
        private int m_RadarObserverCursor;
        private int m_TargetObserverCursor;
        private ThreatRadarSnapshot m_CurrentRadarSnapshotReader = ThreatRadarSnapshot.Empty;
        public IVersionedView<ThreatRadarSnapshot> RadarView
        {
            get
            {
                EnsureCacheValid();
                return m_RadarView;
            }
        }

        // Reusable HashSet to avoid allocations
        // PERF: Pre-allocated capacity
        [NonEntityIndex] private HashSet<int> m_SeenTargets = new(32);

        // PERF: Pre-computed type strings (avoids ToString().ToUpperInvariant() allocation each frame)
        private static readonly string[] s_ThreatTypeStrings = { "shahed", "ballistic" };

        protected override void OnCreate()
        {
            base.OnCreate();

            m_RadarSingletonQuery = GetEntityQuery(ComponentType.ReadOnly<RadarDataSingleton>());

            m_CacheFrame = -1;
            m_MapBoundsCached = false;

            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IThreatRadarReader>(this);

            Enabled = false;
            Log.Info(" Created (buffer-based, zero queries)");
        }

        protected override void OnUpdateImpl()
        {
            // Disabled in OnCreate: this system is used as an on-demand service.
        }

        public void ValidateAfterLoad() => ResetTransientRuntimeStateAfterLoad();

        private void ResetTransientRuntimeStateAfterLoad()
        {
            m_TargetReader = null!;
            m_CoverageReader = null;
            m_MapBounds = null;
            m_MapMin = new float3(-DEFAULT_MAP_EXTENT, 0, -DEFAULT_MAP_EXTENT);
            m_MapMax = new float3(DEFAULT_MAP_EXTENT, 0, DEFAULT_MAP_EXTENT);
            m_MapBoundsCached = false;
            m_MapBoundsMissingWarned = false;
            m_CacheFrame = -1;
            m_RadarObserverCursor = 0;
            m_TargetObserverCursor = 0;
            m_CurrentRadarSnapshotReader = ThreatRadarSnapshot.Empty;
            m_CachedRadarThreats.Clear();
            m_CachedRadarTargets.Clear();
            m_CachedRadarDefenses.Clear();
            m_SeenTargets.Clear();
            m_RadarView.Publish(ThreatRadarSnapshot.Empty, s_RadarSnapshotComparer);
        }

        /// <summary>
        /// Ensures cache is valid for current frame.
        /// If called multiple times in same frame, returns cached data.
        /// If called in new frame, reads buffer data.
        /// </summary>
        private void EnsureCacheValid()
        {
            int currentFrame = UnityEngine.Time.frameCount;

            // Cache is valid only for current frame
            if (m_CacheFrame == currentFrame)
                return;

            using (PerformanceProfiler.Measure("ThreatRadar.CollectData"))
            {
                CollectRadarData();
                m_CacheFrame = currentFrame;
                m_CurrentRadarSnapshotReader = m_RadarView.Observe(ref m_RadarObserverCursor).Value;
            }
        }

        /// <summary>
        /// Get radar threat data (real-time positions for UI).
        /// Returns cached data valid for current frame.
        /// Callers must NOT cast to List and modify - use ToList() if modification needed.
        /// </summary>
        public IReadOnlyList<RadarThreatDto> GetRadarThreats()
        {
            EnsureCacheValid();
            return m_CurrentRadarSnapshotReader.Threats;
        }

        /// <summary>
        /// Get radar target data.
        /// Returns cached data valid for current frame.
        /// Callers must NOT cast to List and modify - use ToList() if modification needed.
        /// </summary>
        IReadOnlyList<RadarTargetDto> IThreatRadarReader.GetRadarTargets()
        {
            EnsureCacheValid();
            return m_CurrentRadarSnapshotReader.Targets;
        }

        /// <summary>
        /// Get radar air-defense coverage circles (position + range) for the defended-zone
        /// overlay. Returns cached data valid for current frame.
        /// </summary>
        IReadOnlyList<RadarDefenseDto> IThreatRadarReader.GetRadarDefenses()
        {
            EnsureCacheValid();
            return m_CurrentRadarSnapshotReader.Defenses;
        }

        /// <summary>
        /// Get map bounds for radar coordinate normalization.
        /// </summary>
        public (float3 min, float3 max) GetMapBounds()
        {
            if (!m_MapBoundsCached) CacheMapBounds();
            return (m_MapMin, m_MapMax);
        }

        /// <summary>
        /// Collect threat data from RadarThreatBuffer.
        /// No queries needed - ThreatMovementSystem already populated the buffer.
        /// </summary>
        private void CollectRadarData()
        {
            m_CachedRadarThreats.Clear();
            m_CachedRadarTargets.Clear();
            m_CachedRadarDefenses.Clear();

            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (m_RadarSingletonQuery.TryGetSingletonEntity<RadarDataSingleton>(out var singleton)
                && EntityManager.HasBuffer<RadarThreatBuffer>(singleton))
            {
                var buffer = EntityManager.GetBuffer<RadarThreatBuffer>(singleton, true);

                // CRASH-DEBUG: Log buffer read
                int bufferLen = buffer.Length;

                // PERF: Ensure capacity before loop to avoid resize allocations
                if (m_CachedRadarThreats.Capacity < bufferLen)
                {
                    m_CachedRadarThreats.Capacity = bufferLen + 16;
                }

                int skippedCount = 0;
                foreach (var data in buffer)
                {
                    // FIX: Validate data to prevent crash in UI JSON parser
                    int typeIndex = (int)data.Type;
                    if (typeIndex < 0 || typeIndex >= s_ThreatTypeStrings.Length)
                    {
                        skippedCount++;
                        Log.Warn($" SKIP invalid type: {typeIndex}");
                        continue;
                    }

                    float3 pos = data.Position;
                    if (float.IsNaN(pos.x) || float.IsInfinity(pos.x) ||
                        float.IsNaN(pos.y) || float.IsInfinity(pos.y) ||
                        float.IsNaN(pos.z) || float.IsInfinity(pos.z))
                    {
                        skippedCount++;
                        Log.Warn($" SKIP NaN/Inf pos: entity={data.EntityIndex} pos={pos}");
                        continue;
                    }

                    float3 targetPos = data.TargetPosition;
                    float3 direction = math.normalizesafe(targetPos - pos);
#pragma warning disable CIVIC078 // Needs actual distance for ETA = remaining / speed
                    float remaining = math.distance(pos, targetPos);
#pragma warning restore CIVIC078
                    float eta = data.Speed > 0 ? remaining / data.Speed : 0f;

                    // Sanitize calculated values
                    if (float.IsNaN(eta) || float.IsInfinity(eta)) eta = 0f;

                    m_CachedRadarThreats.Add(new RadarThreatDto
                    {
                        Entity = new EntityRef(data.EntityIndex, data.EntityVersion),
                        X = pos.x,
                        Y = pos.y,
                        Z = pos.z,
                        Vx = float.IsNaN(direction.x) || float.IsInfinity(direction.x) ? 0f : direction.x,
                        Vz = float.IsNaN(direction.z) || float.IsInfinity(direction.z) ? 0f : direction.z,
                        Eta = eta,
                        Type = s_ThreatTypeStrings[typeIndex],
                        EvasionStatus = GetEvasionStatus(data.MissedShotsCount),
                        IsIdentified = data.IsIdentified,
                        IsOutbound = data.IsOutbound
                    });
                }

                // PERF: Only log if there are problems
                if (skippedCount > 0)
                {
                    Log.Warn($" CollectRadarData: skipped {skippedCount} invalid entries");
                }
            }

            // Collect unique targets from IThreatTargetReader (null-object → empty list)
            if (m_TargetReader == null)
                m_TargetReader = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullThreatTargetReader.Instance);

            IReadOnlyList<ThreatTargetDto> targets = Array.Empty<ThreatTargetDto>();
            var targetView = m_TargetReader.TargetsView;
            if (targetView == null)
            {
                m_TargetObserverCursor = 0;
            }
            else
            {
                targets = targetView.Observe(ref m_TargetObserverCursor).Value.Targets
                    ?? Array.Empty<ThreatTargetDto>();
            }
            m_SeenTargets.Clear();

            foreach (var target in targets)
            {
                if (m_SeenTargets.Contains(target.EntityIndex)) continue;
                m_SeenTargets.Add(target.EntityIndex);

                m_CachedRadarTargets.Add(new RadarTargetDto
                {
                    Entity = new EntityRef(target.EntityIndex, target.EntityVersion),
                    X = target.Position.x,
                    Z = target.Position.z,
                    Name = target.Name,
                    SizeX = target.SizeX,
                    SizeY = target.SizeY,
                    SizeZ = target.SizeZ,
                    RotationY = target.RotationY
                });
            }

            // Collect live air-defense coverage circles (null-object → empty list)
            if (m_CoverageReader == null)
                m_CoverageReader = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullAirDefenseCoverageReader.Instance);

            foreach (var coverage in m_CoverageReader.GetCoverage())
            {
                if (float.IsNaN(coverage.X) || float.IsInfinity(coverage.X) ||
                    float.IsNaN(coverage.Z) || float.IsInfinity(coverage.Z) ||
                    float.IsNaN(coverage.Range) || float.IsInfinity(coverage.Range) ||
                    coverage.Range <= 0f)
                {
                    continue;
                }

                m_CachedRadarDefenses.Add(new RadarDefenseDto
                {
                    X = coverage.X,
                    Z = coverage.Z,
                    Range = coverage.Range
                });
            }

            var snapshot = new ThreatRadarSnapshot(
                m_CachedRadarThreats.Count == 0
                    ? Array.Empty<RadarThreatDto>()
                    : m_CachedRadarThreats.ToArray(),
                m_CachedRadarTargets.Count == 0
                    ? Array.Empty<RadarTargetDto>()
                    : m_CachedRadarTargets.ToArray(),
                m_CachedRadarDefenses.Count == 0
                    ? Array.Empty<RadarDefenseDto>()
                    : m_CachedRadarDefenses.ToArray());
            m_RadarView.Publish(snapshot, s_RadarSnapshotComparer);
        }

        private sealed class ThreatRadarSnapshotComparer : IEqualityComparer<ThreatRadarSnapshot>
        {
            private const float Epsilon = 0.0001f;

            public bool Equals(ThreatRadarSnapshot x, ThreatRadarSnapshot y)
            {
                return RadarThreatsEqual(x.Threats, y.Threats)
                    && RadarTargetsEqual(x.Targets, y.Targets)
                    && RadarDefensesEqual(x.Defenses, y.Defenses);
            }

            public int GetHashCode(ThreatRadarSnapshot obj) => 0;

            private static bool RadarThreatsEqual(IReadOnlyList<RadarThreatDto> x, IReadOnlyList<RadarThreatDto> y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null || x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                {
                    var a = x[i];
                    var b = y[i];
                    if (a.Entity.Index != b.Entity.Index
                        || a.Entity.Version != b.Entity.Version
                        || !FloatEquals(a.X, b.X)
                        || !AltitudeEqual(a.Y, b.Y)
                        || !FloatEquals(a.Z, b.Z)
                        || !FloatEquals(a.Vx, b.Vx)
                        || !FloatEquals(a.Vz, b.Vz)
                        || !FloatEquals(a.Eta, b.Eta)
                        || a.Type != b.Type
                        || a.EvasionStatus != b.EvasionStatus
                        || a.IsIdentified != b.IsIdentified
                        || a.IsOutbound != b.IsOutbound)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool RadarTargetsEqual(IReadOnlyList<RadarTargetDto> x, IReadOnlyList<RadarTargetDto> y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null || x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                {
                    var a = x[i];
                    var b = y[i];
                    if (a.Entity.Index != b.Entity.Index
                        || a.Entity.Version != b.Entity.Version
                        || !FloatEquals(a.X, b.X)
                        || !FloatEquals(a.Z, b.Z)
                        || !FloatEquals(a.SizeX, b.SizeX)
                        || !FloatEquals(a.SizeY, b.SizeY)
                        || !FloatEquals(a.SizeZ, b.SizeZ)
                        || !FloatEquals(a.RotationY, b.RotationY)
                        || a.Name != b.Name)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool RadarDefensesEqual(IReadOnlyList<RadarDefenseDto> x, IReadOnlyList<RadarDefenseDto> y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null || x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                {
                    var a = x[i];
                    var b = y[i];
                    if (!FloatEquals(a.X, b.X)
                        || !FloatEquals(a.Z, b.Z)
                        || !FloatEquals(a.Range, b.Range))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool FloatEquals(float left, float right)
                => Math.Abs(left - right) <= Epsilon;

            // World-Y reaches the UI only as the normalized track brightness
            // (Altitude = clamp(Y / AltitudeCeiling, 0, 1) in the generated FromRuntime). Comparing
            // raw metres re-published a snapshot for sub-pixel Y jitter a hovering ballistic shows at
            // apogee — invisible on screen. Compare the same normalized altitude the UI actually sees.
            private static bool AltitudeEqual(float leftY, float rightY)
            {
                const float ceiling = CivicSurvival.Core.UI.DomainState.RadarThreatDto.AltitudeCeiling;
                float left = math.saturate(leftY / ceiling);
                float right = math.saturate(rightY / ceiling);
                return Math.Abs(left - right) <= Epsilon;
            }
        }

        private void CacheMapBounds()
        {
            if (m_MapBounds == null)
            {
                m_MapBounds = ServiceRegistry.TryGet<IMapBoundsReader>();
                if (m_MapBounds == null)
                {
                    if (!m_MapBoundsMissingWarned)
                    {
                        Log.Warn("IMapBoundsReader unavailable - using default extent fallback");
                        m_MapBoundsMissingWarned = true;
                    }
                    return;
                }
            }

            if (!m_MapBounds.TryGetBounds(out var snapshot, out _))
            {
                if (!m_MapBoundsMissingWarned)
                {
                    Log.Warn("Map bounds unavailable — will retry on next radar bounds request");
                    m_MapBoundsMissingWarned = true;
                }
                return;
            }

            m_MapMin = snapshot.PlayableOffset;
            m_MapMax = new float3(
                snapshot.PlayableOffset.x + snapshot.PlayableArea.x,
                0f,
                snapshot.PlayableOffset.z + snapshot.PlayableArea.y);
            m_MapBoundsCached = true;
            m_MapBoundsMissingWarned = false;
            if (Log.IsDebugEnabled) Log.Debug($" Map bounds: {m_MapMin} to {m_MapMax}");
        }

        /// <summary>
        /// Convert MissedShotsCount to evasion status for UI display.
        /// 0 misses = "targeted" (white) - normal tracking
        /// 1-2 misses = "evasive" (yellow blinking) - drone is maneuvering
        /// 3+ misses = "hardlock" (orange fast blink) - critical, hard to hit
        /// </summary>
        private static string GetEvasionStatus(int missedShots) => missedShots switch
        {
            0 => "targeted",
            1 or 2 => "evasive",
            _ => "hardlock"
        };

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IThreatRadarReader>(this);
            }

            base.OnDestroy();

            m_CachedRadarThreats.Clear();
            m_CachedRadarTargets.Clear();
            m_CachedRadarDefenses.Clear();
            m_SeenTargets.Clear();

            Log.Info(" Destroyed");
        }
    }
}
