using Game;
using Game.Areas;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Collections;
using Colossal.Logging;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Core;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// Tracks district entity lifecycle (creation/destruction).
    /// Publishes DistrictLifecycleEvent to notify subscribers about changes.
    ///
    /// WHY THIS EXISTS:
    /// Entity.Index can be reused by Unity after entity destruction.
    /// Systems using int as key (ThreadSafeDistrictState) need notification
    /// when district is destroyed to clean up stale state.
    ///
    /// Without this, a new district could "inherit" blackout schedules
    /// from a previously deleted district that had the same Entity.Index.
    ///
    /// Published by: This system
    /// Consumed by: ThreadSafeDistrictState (via EventBus subscription)
    /// </summary>
    [ActIndependent]
    public partial class DistrictLifecycleSystem : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("DistrictLifecycle");

        // Track known districts: Index → Version
        // When Index exists but Version differs → district was recreated (destroy + create)
        // When Index missing from current → district was destroyed
        // When Index missing from known → district was created
        // CIVIC097 suppressed: Index is intentional key — this system IS the recycling detector
        [NonEntityIndex] private NativeHashMap<int, int> m_KnownDistricts;
        private float m_ThrottleTimer;

        // The detector cache is runtime memory, not save data. After load the engine
        // remaps District entities; rebuilding a silent baseline (flag set from the
        // PLVS post-load hook, never from a Deserialize path) makes it impossible to
        // compare a pre-save Entity.Index space against a post-load one. Ephemeral —
        // reset by ResetState().
        [System.NonSerialized] private bool m_NeedBaseline;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_KnownDistricts = new NativeHashMap<int, int>(32, Allocator.Persistent);

            Log.Info($"{nameof(DistrictLifecycleSystem)} created");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            if (purpose == Purpose.NewGame)
                ResetState();
        }

        public void ResetState()
        {
            // New-game reset: clear lifecycle cache so real district creation is reported.
            if (m_KnownDistricts.IsCreated) m_KnownDistricts.Clear();
            m_ThrottleTimer = 0f;
            m_NeedBaseline = false;
        }

        // IPostLoadValidation: the detector cache is rebuilt from the live
        // (engine-remapped) District set on the first post-load tick. Arming the
        // flag here — never from a Deserialize path — guarantees the rebuild runs
        // after the engine has remapped entities and the District query is valid
        // (CS2 never re-runs OnCreate; reading the query mid-deserialize is invalid).
        public void ValidateAfterLoad() => m_NeedBaseline = true;

        // CIVIC097 suppressed: Index→Version tracking IS the purpose of this system.
        // It detects entity recycling by comparing known Version for a given Index.
#pragma warning disable CIVIC097
        protected override void OnUpdateImpl()
        {
            if (m_NeedBaseline)
            {
                // First tick after load: adopt the engine-remapped District set as
                // the baseline WITHOUT publishing any DistrictLifecycleEvent, so the
                // detector never compares a pre-save index space against a post-load
                // one. Subsequent ticks then report only genuine future deltas.
                m_NeedBaseline = false;
                if (m_KnownDistricts.IsCreated) m_KnownDistricts.Clear();
                foreach (var (_, entity) in
                    SystemAPI.Query<RefRO<District>>()
                    .WithNone<Temp, Deleted>()
                    .WithEntityAccess())
                {
                    m_KnownDistricts[entity.Index] = entity.Version;
                }
                m_ThrottleTimer = 0f;
                return;
            }

            // Throttle: districts don't change often, check every ~500ms
            m_ThrottleTimer += SystemAPI.Time.DeltaTime;
            if (m_ThrottleTimer < 0.5f) return;
            m_ThrottleTimer = 0f;

            // Build set of current district indices for fast lookup
            var currentIndices = new NativeHashSet<int>(64, Allocator.Temp);

            // Check for new and recreated districts (SystemAPI.Query — zero sync point)
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<District>>()
                .WithNone<Temp, Deleted>()
                .WithEntityAccess())
            {
                int index = entity.Index;
                int version = entity.Version;

                currentIndices.Add(index);

                if (m_KnownDistricts.TryGetValue(index, out int knownVersion))
                {
                    if (knownVersion != version)
                    {
                        // Index reused with different version → old destroyed, new created
                        if (Log.IsDebugEnabled) Log.Debug($"[DistrictLifecycle] District {index} recreated (v{knownVersion} → v{version})");

                        // Publish destroy for old, then create for new
                        EventBus?.SafePublish(new DistrictLifecycleEvent(index, DistrictLifecycle.Destroyed), "DistrictLifecycleSystem");
                        EventBus?.SafePublish(new DistrictLifecycleEvent(index, DistrictLifecycle.Created), "DistrictLifecycleSystem");

                        m_KnownDistricts[index] = version;
                    }
                    // Same version → no change
                }
                else
                {
                    // New district
                    if (Log.IsDebugEnabled) Log.Debug($"[DistrictLifecycle] District {index} created (v{version})");
                    EventBus?.SafePublish(new DistrictLifecycleEvent(index, DistrictLifecycle.Created), "DistrictLifecycleSystem");
                    m_KnownDistricts.Add(index, version);
                }
            }

            // Check for destroyed districts (in known but not in current)
            var toRemove = new NativeList<int>(Allocator.Temp);

            foreach (var kvp in m_KnownDistricts)
            {
                if (!currentIndices.Contains(kvp.Key))
                {
                    if (Log.IsDebugEnabled) Log.Debug($"[DistrictLifecycle] District {kvp.Key} destroyed");
                    EventBus?.SafePublish(new DistrictLifecycleEvent(kvp.Key, DistrictLifecycle.Destroyed), "DistrictLifecycleSystem");
                    toRemove.Add(kvp.Key);
                }
            }

            // Remove destroyed districts from tracking
            for (int i = 0; i < toRemove.Length; i++)
            {
                m_KnownDistricts.Remove(toRemove[i]);
            }

            if (toRemove.IsCreated) toRemove.Dispose();
            if (currentIndices.IsCreated) currentIndices.Dispose();
        }

#pragma warning restore CIVIC097

        protected override void OnDestroy()
        {
            if (m_KnownDistricts.IsCreated)
                m_KnownDistricts.Dispose();

            Log.Info($"{nameof(DistrictLifecycleSystem)} destroyed");
            base.OnDestroy();
        }
    }
}
