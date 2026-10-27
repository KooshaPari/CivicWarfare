using System;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using Unity.Jobs;
using Unity.Mathematics;

namespace CivicSurvival.Core.Adapters
{
    /// <summary>
    /// Per-World host that samples vanilla terrain + water once per loaded city,
    /// derives the coastline (land↔water boundary) via marching-squares, simplifies
    /// it, and caches the resulting world-space polyline JSON. UI reads the cached
    /// string through <see cref="MapContourState"/> / IMapContourReader.
    ///
    /// PERF: the whole sample → contour → cache pass runs exactly once (terrain and
    /// water are static after load). After the cache is filled the system disables
    /// its own tick, so there is zero recurring cost.
    /// </summary>
    [ActIndependent]
    public partial class VanillaMapContourAdapter : ThrottledSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("VanillaMapContourAdapter");

        // Empty geometry payload (no coast, no water). UI parses this to "draw nothing
        // extra", keeping the plain cyan grid.
        private const string EMPTY_GEOMETRY = "{\"coast\":[],\"water\":[]}";

        // Coastline contour is static — poll at most once per second until terrain/water
        // are ready, then the system disables itself (single compute per loaded city).
        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        // Sample grid resolution along each axis. The playable area is ~14.3 km, so a
        // 96×96 grid is ~150 m per sample — enough to read the coastline shape while
        // staying cheap (9216 samples, computed once). Tuned alongside SIMPLIFY_TOLERANCE.
        private const int GRID = 96;

        // A point counts as water when its surface depth exceeds this (metres). Filters
        // out puddles / numerical noise so only real bodies of water form the boundary.
        private const float WATER_DEPTH_THRESHOLD = 1.5f;

        // Douglas–Peucker tolerance (world metres). Larger = fewer points. Sized so a
        // typical coastline lands in the low hundreds of points, well under the ceiling.
        private const float SIMPLIFY_TOLERANCE = 60f;

        // Hard ceiling on emitted points across coast + water combined. The geometry is
        // computed ONCE per loaded city and the UI layer is static + React.memo (rendered
        // once, never on threat ticks), so the cost is a one-time SVG render — a few
        // thousand static points is cheap. The ceiling only guards pathological maps from
        // shipping an oversized payload; on overflow UI falls back to the empty cyan grid.
        // Water run-length polygons add to the coastline count, so this sits well above
        // the coast-only budget (a typical coastal map lands ~800–1500).
        private const int MAX_TOTAL_POINTS = 2500;

        private Game.Simulation.TerrainSystem? m_TerrainSystem;
        private Game.Simulation.WaterSystem? m_WaterSystem;

        [NonSerialized] private MapContourState? m_State;
        [NonSerialized] private string m_ContourJson = EMPTY_GEOMETRY;
        [NonSerialized] private bool m_HasContour;
        [NonSerialized] private string? m_UnavailableReason;

        internal bool HasContour => m_HasContour;
        internal string? UnavailableReason => m_UnavailableReason;

        protected override void OnCreate()
        {
            base.OnCreate();

            try
            {
                m_TerrainSystem = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
                m_WaterSystem = World.GetOrCreateSystemManaged<Game.Simulation.WaterSystem>();
            }
            catch (Exception ex)
            {
                m_UnavailableReason = $"Terrain/Water system unavailable: {ex.Message}";
                Log.Warn(m_UnavailableReason);
            }
        }

        protected override void OnDestroy()
        {
            if (m_State != null && ReferenceEquals(m_State.CurrentHost, this))
                m_State.CurrentHost = null;
            m_State = null;

            base.OnDestroy();
        }

        internal void BindFacade(MapContourState facade)
        {
            m_State = facade;
            facade.CurrentHost = this;

            // After hot-reload the host is rebuilt but the city is already loaded; if
            // the contour was already computed in this host instance it stays valid,
            // otherwise the next tick recomputes it.
        }

        internal bool TryGetContourJson(out string contourJson)
        {
            contourJson = m_ContourJson;
            return m_HasContour;
        }

        /// <summary>
        /// System instances are reused across in-process load (NonSerialized fields keep
        /// their value), so a stale contour from the previous city would otherwise stick.
        /// Invalidate the cache and re-enable the tick so the newly loaded city's coastline
        /// is recomputed once.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_HasContour = false;
            m_ContourJson = EMPTY_GEOMETRY;
            m_UnavailableReason = null;
            Enabled = true;
        }

        protected override void OnThrottledUpdate()
        {
            if (m_HasContour)
            {
                // PERF-LOCK: contour is static per loaded city — computed once, then this
                // system stops ticking entirely. Do NOT move sampling into a per-frame
                // path; terrain/water never change after load.
                Enabled = false;
                return;
            }

            if (m_TerrainSystem == null || m_WaterSystem == null)
                return;

            ComputeContour();
        }

        private void ComputeContour()
        {
            var heightData = m_TerrainSystem!.GetHeightData(waitForPending: true);
            if (!heightData.isCreated)
                return; // terrain not ready yet — retry next tick

            // One-shot Complete on the water surface readback. This is the single
            // Complete() allowed here: it runs once per loaded city, never per frame.
            // NOTE: vanilla GetSurfacesData overwrites `out deps` twice and returns only
            // the BACKDROP reader's writer handle, while SampleDepth reads the DEPTHS
            // reader's CPU array — whose writer handle is the one lost. Drain both
            // explicitly so the depths array is settled before we sample it.
            var surfaces = m_WaterSystem!.GetSurfacesData(out JobHandle waterDeps);
            m_WaterSystem.GetDepths(out JobHandle depthsDeps);
            JobHandle.CombineDependencies(waterDeps, depthsDeps).Complete();

            // Sample the playable area; positionOffset/playableArea give the in-bounds
            // rectangle (same source MapBoundsState publishes for the radar).
            var offset = m_TerrainSystem.playableOffset;
            var area = m_TerrainSystem.playableArea;
            if (area.x <= 0f || area.y <= 0f)
                return; // bounds not ready yet

            var origin = new float3(offset.x, 0f, offset.y);
            var cellSize = new float2(area.x / (GRID - 1), area.y / (GRID - 1));

            var isWater = new bool[GRID * GRID];
            for (int r = 0; r < GRID; r++)
            {
                for (int c = 0; c < GRID; c++)
                {
                    var worldPos = new float3(
                        origin.x + c * cellSize.x,
                        0f,
                        origin.z + r * cellSize.y);

                    float depth = Game.Simulation.WaterUtils.SampleDepth(ref surfaces, worldPos);
                    isWater[r * GRID + c] = depth > WATER_DEPTH_THRESHOLD;
                }
            }

            string json = MapContourBuilder.BuildMapGeometryJson(
                isWater,
                GRID,
                GRID,
                origin,
                cellSize,
                SIMPLIFY_TOLERANCE,
                MAX_TOTAL_POINTS,
                out int totalPoints);

            // PERF-LOCK: cache the one-shot result and mark ready; subsequent ticks
            // early-out and disable the system. This assignment is the contour's whole
            // recurring cost (none after this point).
            m_ContourJson = json;
            m_HasContour = true;
            m_UnavailableReason = null;

            if (json == EMPTY_GEOMETRY && totalPoints > MAX_TOTAL_POINTS)
                Log.Warn($"Map geometry exceeded {MAX_TOTAL_POINTS}-point ceiling ({totalPoints}) — shipping empty geometry (UI keeps the grid).");
            else
                Log.Info($"Map geometry computed: {totalPoints} points (coast + water), {json.Length} bytes JSON.");

#if DEBUG
            // Dev content pipeline (MirrorCityTargets phase A): every DEBUG load snapshots the freshly
            // computed contour into ModData as a baked-map candidate. One shot per load (this method
            // runs once, then the system disables); the developer picks the dumps worth keeping,
            // simplifies offline, and copies them into Resources/MirrorCityMaps/.
            if (m_State != null)
                DumpBakedMapCandidate(m_State, new float4(origin.x, origin.z, origin.x + area.x, origin.z + area.y));
#endif

            Enabled = false;
        }

#if DEBUG
        /// <summary>
        /// Dump the just-computed contour as a mirror-city baked-map candidate named after the loaded
        /// city (slugged). DEBUG-only dev tooling — failures only log, never affect the contour path.
        /// </summary>
        private void DumpBakedMapCandidate(MapContourState reader, float4 tileBounds)
        {
            string cityName = "map";
            try
            {
                var cfg = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                if (cfg != null && !string.IsNullOrEmpty(cfg.cityName))
                    cityName = cfg.cityName;
            }
            catch (Exception e)
            {
                Log.Warn($"City name unavailable for baked-map dump ({e.GetType().Name}); using fallback id.");
            }

            var slugChars = new char[cityName.Length];
            for (int i = 0; i < cityName.Length; i++)
            {
                char ch = cityName[i];
                slugChars[i] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
            }
            string slug = new string(slugChars);

            if (MirrorCityContourDumper.TryDumpBakedMap(reader, slug, cityName, tileBounds, out string path))
                Log.Info($"Baked-map candidate dumped: {path}");
        }
#endif
    }
}
