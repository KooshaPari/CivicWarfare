using System.Threading;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Engineering;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Engineering;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace CivicSurvival.Domains.Engineering.Systems
{
    /// <summary>
    /// Owns plant-repair pending-intent runtime state and publishes the
    /// <see cref="PlantRepairIntentSnapshot"/> view.
    /// </summary>
    [ActIndependent]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.None,
        DisposePhase = SingletonLifecyclePhase.None,
        AllowAsymmetry = true,
        Justification = "RepairProc holds only ephemeral runtime state (PendingRepairPlantIds is rehydrated from live RepairTransactionIntent entities post-load); no persisted singletons.")]
    public partial class PlantRepairRequestProcessor : CivicSystemBase,
        IPostLoadValidation, IPlantRepairIntentReader, IResettable, IBootDefaultsReset
    {
        public void PurgeAfterLoad() { }

        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);

        private static readonly LogContext Log = new("PlantRepairRequestProcessor");

        private EntityQuery m_LiveRepairIntentQuery;

        [NonEntityIndex] private NativeHashSet<int> m_PendingRepairPlantIds;
        private readonly VersionedView<PlantRepairIntentSnapshot> m_RepairIntentView =
            new(PlantRepairIntentSnapshot.Empty);
        private bool m_PendingRepairPlantIdsNeedHydration;

#pragma warning disable CIVIC241 // Revision is ephemeral and reset/load rehydrates it from live intent entities.
        [System.NonSerialized] private int m_RepairIntentRevision;
        [System.NonSerialized] private int m_LastPublishedIntentCount = -1;
        [System.NonSerialized] private int m_LastPublishedRevision = -1;
#pragma warning restore CIVIC241

        IVersionedView<PlantRepairIntentSnapshot>? IPlantRepairIntentReader.RepairIntentView => m_RepairIntentView;

        bool IPlantRepairIntentReader.HasPendingRepairIntent(int stablePlantId)
        {
            if (m_PendingRepairPlantIdsNeedHydration)
                HydratePendingRepairPlantIds();
            return m_PendingRepairPlantIds.IsCreated
                && m_PendingRepairPlantIds.Contains(stablePlantId);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_LiveRepairIntentQuery = GetEntityQuery(
                ComponentType.ReadOnly<RepairTransactionIntent>(),
                ComponentType.Exclude<Deleted>());
            m_PendingRepairPlantIds = new NativeHashSet<int>(16, Allocator.Persistent);


            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Register<IPlantRepairIntentReader>(this);

            Log.Info("Created");
        }

        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_WEAR;

#pragma warning disable CIVIC231 // ValidateAfterLoad runs during load reconciliation regardless of act.
        public void ValidateAfterLoad()
        {
            ResetRepairRuntimeState();
        }
#pragma warning restore CIVIC231

        public void ResetToBootDefaults(ResetReason reason)
        {
            ResetRepairRuntimeState();
            Log.Info($"Boot reset: reason={reason}");
        }

        void IResettable.ResetState() => ResetRepairRuntimeState();

        private void ResetRepairRuntimeState()
        {
            if (m_PendingRepairPlantIds.IsCreated)
                m_PendingRepairPlantIds.Clear();
            m_PendingRepairPlantIdsNeedHydration = true;
            BumpRevision();
            m_LastPublishedIntentCount = -1;
            m_LastPublishedRevision = -1;
        }

#pragma warning disable CIVIC226 // Monotonic version stamp; overflow is harmless because consumers compare equality only.
        private void BumpRevision()
        {
            unchecked { m_RepairIntentRevision++; }
        }
#pragma warning restore CIVIC226

        protected override void OnUpdateImpl()
        {
            if (m_PendingRepairPlantIdsNeedHydration)
                HydratePendingRepairPlantIds();

            PublishRepairIntentSnapshotIfChanged();
        }

        public void MarkPending(int plantId)
        {
            if (m_PendingRepairPlantIdsNeedHydration)
                HydratePendingRepairPlantIds();
            if (m_PendingRepairPlantIds.IsCreated && m_PendingRepairPlantIds.Add(plantId))
            {
                BumpRevision();
                PublishRepairIntentSnapshotIfChanged();
            }
        }

        public void MarkResolved(int plantId)
        {
            if (m_PendingRepairPlantIdsNeedHydration)
                HydratePendingRepairPlantIds();
            if (m_PendingRepairPlantIds.IsCreated && m_PendingRepairPlantIds.Remove(plantId))
            {
                BumpRevision();
                PublishRepairIntentSnapshotIfChanged();
            }
        }

        private void PublishRepairIntentSnapshotIfChanged()
        {
            int pendingCount = m_PendingRepairPlantIds.IsCreated
                ? m_PendingRepairPlantIds.Count
                : 0;
            int revision = m_RepairIntentRevision;
            if (pendingCount == m_LastPublishedIntentCount && revision == m_LastPublishedRevision)
                return;
            if (Log.IsDebugEnabled)
                Log.Debug($"RepairIntent publish: count {m_LastPublishedIntentCount}->{pendingCount} rev {m_LastPublishedRevision}->{revision}");
            m_RepairIntentView.Publish(new PlantRepairIntentSnapshot(pendingCount, revision));
            m_LastPublishedIntentCount = pendingCount;
            m_LastPublishedRevision = revision;
        }

        [CompletesDependency("One-shot demand-driven hydration gated by m_PendingRepairPlantIdsNeedHydration (set after load / on change), not per-frame; the live repair-intent set is materialised via ToComponentDataArray")]
        private void HydratePendingRepairPlantIds()
        {
            m_PendingRepairPlantIdsNeedHydration = false;
            if (!m_PendingRepairPlantIds.IsCreated)
                return;

            m_PendingRepairPlantIds.Clear();
            if (!m_LiveRepairIntentQuery.IsEmptyIgnoreFilter)
            {
                using var intents = m_LiveRepairIntentQuery.ToComponentDataArray<RepairTransactionIntent>(Allocator.Temp);
                for (int i = 0; i < intents.Length; i++)
                {
                    var intent = intents[i];
                    if (!intent.Applied)
                        m_PendingRepairPlantIds.Add(intent.PlantId);
                }
            }
            BumpRevision();
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
                ServiceRegistry.Instance.Unregister<IPlantRepairIntentReader>(this);

            if (m_PendingRepairPlantIds.IsCreated)
                m_PendingRepairPlantIds.Dispose();

            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }
}
