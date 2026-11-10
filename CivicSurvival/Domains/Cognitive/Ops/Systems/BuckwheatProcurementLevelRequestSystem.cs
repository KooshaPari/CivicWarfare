using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Requests;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Ops.Systems
{
    /// <summary>
    /// Applies player-selected Buckwheat procurement levels under Cognitive ownership.
    /// </summary>
    [SingletonOwner(typeof(BuckwheatConfig))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    [ActIndependent]
    [HandlesRequestKind(RequestKind.ProcurementLevel)]
    [TransientConsumerReconcile(typeof(BuckwheatProcurementLevelRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Transient UI command: before this consumer applies it no durable procurement-level state has changed, so a load-window loss is intentionally reissuable.")]
    public partial class BuckwheatProcurementLevelRequestSystem : EmittingRequestProcessor<BuckwheatProcurementLevelRequest>, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("BuckwheatProcurementLevelRequestSystem");
        private static readonly int[] ProcurementPresets = { 0, 25, 50, 75, 100 };

        private EntityQuery m_ConfigQuery;
        private ComponentLookup<BuckwheatConfig> m_ConfigLookup;

        protected override ActionKey ActionKey => ActionKey.BuckwheatProcurement;
        public override RequestKind RequestKind => RequestKind.ProcurementLevel;

        // Registered in ModificationEnd (CognitiveDomain) — pause-safe per Axiom 14.
        protected override bool RunsInModificationEnd => true;

        protected override void OnCreateProcessor()
        {
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadWrite<BuckwheatConfig>());
            m_ConfigLookup = GetComponentLookup<BuckwheatConfig>(false);
            EnsureBuckwheatConfigExists();

            Log.Info("Created");
        }

        public void ValidateAfterLoad() => EnsureBuckwheatConfigExists();

        private void EnsureBuckwheatConfigExists()
        {
            BuckwheatSingleton.EnsureExists(EntityManager);
        }

        protected override RequestStatus Apply(
            in BuckwheatProcurementLevelRequest request,
            in RequestMeta meta,
            ref EntityCommandBuffer ecb,
            ref bool ecbCreated,
            out string reasonId)
        {
            _ = ecb;
            _ = ecbCreated;
            m_ConfigLookup.Update(this);

            reasonId = "";
            if (!m_ConfigQuery.TryGetSingletonEntity<BuckwheatConfig>(out var entity))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return RequestStatus.Failed;
            }

            var config = m_ConfigLookup[entity];
            int snapped = Engine.Util.SnapToPreset(request.Percent, ProcurementPresets);
            if (!BuckwheatEligibility.CanSelectProcurementLevel(snapped, config.ProcurementLevel, out reasonId))
                return RequestStatus.Failed;

            config.ProcurementLevel = snapped;
            m_ConfigLookup[entity] = config;

            Log.Info($"[Buckwheat] Procurement level set to {snapped}% (${BuckwheatSingleton.DailyCost(snapped):N0}/day)");
            return RequestStatus.Success;
        }
    }
}
