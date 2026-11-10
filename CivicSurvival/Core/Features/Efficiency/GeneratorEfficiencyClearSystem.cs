using Game;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Core.Features.Efficiency
{
    /// <summary>
    /// Clears generator efficiency modifiers at the start of each group update.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(GeneratorEfficiency))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    public partial class GeneratorEfficiencyClearSystem
        : ThrottledSystemBase, IInitializable, ICivicSingletonOwner<GeneratorEfficiency>
    {
        public int InitOrder => CivicSurvival.Core.Interfaces.Core.InitPriority.DEFAULT;

        private static readonly LogContext Log = new("GeneratorEfficiencyClearSystem");

        private EntityQuery m_EfficiencyQuery;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_1_SECOND;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_EfficiencyQuery = GetEntityQuery(
                ComponentType.ReadWrite<GeneratorEfficiency>(),
                ComponentType.ReadWrite<GeneratorEfficiencySource>()
            );
            EnsureSingleton();
        }

        // Inv 2: EnsureExists on an owned, non-serialized singleton belongs in
        // OnStartRunning so it is recreated on every world activation/reload —
        // not only in OnCreate (which does not re-run on a reuse-world load) and
        // OnInitialize (post-load Phase 2, but OnDestroy may have torn the
        // singleton down asymmetrically before then).
        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            EnsureSingleton();
        }

        [CompletesDependency("OnThrottledUpdate fallback path: CalculateEntityCount runs only when GeneratorEfficiency singleton invariant is violated (>1 entity, expected zero in normal operation); diagnostic-only sync amortised over throttle interval")]
        protected override void OnThrottledUpdate()
        {
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + count check + GetSingletonEntity
            // Recreate path is OnStartRunning (every world activation / stop-start)
            // + OnInitialize (post-load Phase 2) — both run EntityManager structural
            // work at a point Axiom 7 permits. The missing-singleton branch here is
            // no longer a permanent no-op and must not do EntityManager structural
            // changes from the update path (CIVIC006/Axiom 7).
            if (!m_EfficiencyQuery.TryGetSingletonEntity<GeneratorEfficiency>(out var entity))
            {
                // Log warning if duplicates exist (shouldn't happen, but defensive)
                int entityCount = m_EfficiencyQuery.CalculateEntityCount();
                if (entityCount > 1)
                {
                    Log.Warn($"Found {entityCount} entities, expected 1");
                }
                return;
            }

            if (!EntityManager.HasBuffer<GeneratorEfficiencySource>(entity)) return;
            var buffer = SystemAPI.GetBuffer<GeneratorEfficiencySource>(entity);
            buffer.Clear();
        }

        public void OnInitialize()
        {
            EnsureSingleton();
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            EnsureSingleton();
        }

        // FIX #120: Destroy singleton entity created in EnsureSingleton
        protected override void OnDestroy()
        {
            if (m_EfficiencyQuery.TryGetSingletonEntity<GeneratorEfficiency>(out var entity))
                EntityManager.DestroyEntity(entity);

            base.OnDestroy();
        }

        private void EnsureSingleton()
        {
            if (!m_EfficiencyQuery.IsEmptyIgnoreFilter)
                return;

            var entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new GeneratorEfficiency { Value = 1f });
            EntityManager.AddBuffer<GeneratorEfficiencySource>(entity);
            EntityManager.SetName(entity, nameof(GeneratorEfficiency));
        }
    }
}
