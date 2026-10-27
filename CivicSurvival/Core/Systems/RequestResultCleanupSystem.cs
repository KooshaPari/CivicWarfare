using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// Cleans terminal request result events after the UI collector has reported them.
    /// </summary>
    // Reads RequestResultEvent — must run after the post-load purge so it never
    // TTL-touches a stale pre-purge terminal result (CIVIC415 / W2 row 171).
#pragma warning disable CIVIC442 // CommandRequestCleanupSystem only reads RequestResultEvent request ids; this system owns RequestResultEvent destruction.
    [ActIndependent]
    public partial class RequestResultCleanupSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;

        private static readonly LogContext Log = new("RequestResultCleanup");

        private const double ReportedTtlSeconds = 10d;

        private ModCleanupBarrier m_ModCleanupBarrier = null!;
        private EntityQuery m_AllResultsQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ModCleanupBarrier = World.GetOrCreateSystemManaged<ModCleanupBarrier>();
            m_AllResultsQuery = GetEntityQuery(ComponentType.ReadOnly<RequestResultEvent>());
        }

        /// <summary>
        /// RECONCILE pass — nothing to reconcile. A RequestResultEvent is a
        /// one-shot UI notification; the durable consequence of the request
        /// lives in domain state, not in this event.
        /// </summary>
        public void ValidateAfterLoad() { }

        /// <summary>
        /// PURGE pass (W2 row 171). Destroy EVERY RequestResultEvent on load,
        /// Reported or not. Its TTL is ElapsedTime-based (resets on load → a
        /// survivor never expires) and RequestResultCollectorSystem would
        /// re-collect a not-yet-Reported survivor and republish a stale reject
        /// toast against a UI request that no longer exists. Session-scoped, not
        /// load-bearing — see RequestResultEvent (IEmptySerializable).
        /// </summary>
        [CompletesDependency("PurgeAfterLoad: one-shot post-load purge of stale RequestResultEvent entities (session-scoped); CalculateEntityCount is diagnostic-only, sync amortised against the DestroyEntity that follows")]
        public void PurgeAfterLoad()
        {
            if (m_AllResultsQuery.IsEmptyIgnoreFilter)
                return;

            int destroyed = m_AllResultsQuery.CalculateEntityCount();
            EntityManager.DestroyEntity(m_AllResultsQuery);

            if (destroyed > 0)
                Log.Info($"PurgeAfterLoad: destroyed {destroyed} stale RequestResultEvent(s) (W2 row 171 — no stale toast republish)");
        }

        protected override void OnUpdateImpl()
        {
            double now = SystemAPI.Time.ElapsedTime;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            foreach (var (result, entity) in
                SystemAPI.Query<RefRO<RequestResultEvent>>()
                    .WithAll<Reported>()
                    .WithEntityAccess())
            {
                if (now - result.ValueRO.CreatedTime <= ReportedTtlSeconds)
                    continue;

                if (!ecbCreated)
                {
                    ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }
                ecb.DestroyEntity(entity);
            }

            if (ecbCreated)
                m_ModCleanupBarrier.AddJobHandleForProducer(Dependency);
        }
    }
#pragma warning restore CIVIC442
}
