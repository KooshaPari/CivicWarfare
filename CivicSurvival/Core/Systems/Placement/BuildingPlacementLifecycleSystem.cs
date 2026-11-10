using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Placement;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Core.Systems.Placement
{
    /// <summary>
    /// Owns building-placement lifecycle edges that are not Created -> Intent. Detector wins
    /// first: matching Created entities suppress cancellation even if the tool has already
    /// returned to Default in the same frame. The cancel result is emitted against the pending
    /// kind's handler.
    /// </summary>
    [ActIndependent]
    public partial class BuildingPlacementLifecycleSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private const int CancelGraceFrames = 2;
        private static readonly LogContext Log = new("BuildingPlacementLifecycle");

        private EntityQuery m_PendingQuery;
        private ComponentLookup<BuildingPlacementPending> m_PendingLookup;
        private ModificationEndBarrier m_Barrier = null!;
        private ToolSystem m_ToolSystem = null!;
        private DefaultToolSystem m_DefaultToolSystem = null!;

#pragma warning disable CIVIC241, CIVIC312 // Ephemeral once-guard for deferred ECB playback
        [System.NonSerialized] private int m_LastCancelledPendingIndex;
        [EntityOnceGuard("Paired with m_LastCancelledPendingIndex; together form the (Index, Version) identity of the last cancelled pending placement so the cancel is not re-processed on subsequent ticks.")]
        [System.NonSerialized] private int m_LastCancelledPendingVersion;
#pragma warning restore CIVIC241, CIVIC312

        [System.NonSerialized] private string m_LastNonDefaultToolName = string.Empty;
        [System.NonSerialized] private int m_LastNonDefaultToolFrame;
        [System.NonSerialized] private Game.Tools.ToolBaseSystem? m_LastNonDefaultToolRef;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Barrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultToolSystem = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_PendingLookup = GetComponentLookup<BuildingPlacementPending>(false);
            m_PendingQuery = GetEntityQuery(ComponentType.ReadWrite<BuildingPlacementPending>());

            RequireForUpdate(m_PendingQuery);
            Log.Info("Created");
        }

        protected override void OnUpdateImpl()
        {
            if (!m_PendingQuery.TryGetSingletonEntity<BuildingPlacementPending>(out var pendingEntity))
                return;

            if (pendingEntity.Index == m_LastCancelledPendingIndex &&
                pendingEntity.Version == m_LastCancelledPendingVersion)
                return;

            if (!SystemAPI.TryGetSingleton<BuildingPlacementPending>(out var pending))
                return;

            m_PendingLookup.Update(this);

            if (HasMatchingCreated(pending))
            {
                ClearDefaultCandidateIfNeeded(pendingEntity, pending);
                return;
            }

            if (m_ToolSystem.activeTool != m_DefaultToolSystem)
            {
                var activeTool = m_ToolSystem.activeTool;
                if (!ReferenceEquals(activeTool, m_LastNonDefaultToolRef))
                {
                    m_LastNonDefaultToolRef = activeTool;
                    m_LastNonDefaultToolName = activeTool?.GetType().Name ?? "<null>";
                }
                m_LastNonDefaultToolFrame = UnityEngine.Time.frameCount;
                ClearDefaultCandidateIfNeeded(pendingEntity, pending);
                return;
            }

            int frame = UnityEngine.Time.frameCount;
            if (pending.ToolDefaultSinceFrame == 0)
            {
                pending.ToolDefaultSinceFrame = frame;
                SetPendingIfAlive(pendingEntity, pending);
                return;
            }

            if (frame - pending.ToolDefaultSinceFrame < CancelGraceFrames)
                return;

            CancelPendingPlacement(pendingEntity, pending);
        }

        private bool HasMatchingCreated(BuildingPlacementPending pending)
        {
            Entity pendingPrefab = new Entity { Index = pending.PrefabIndex, Version = pending.PrefabVersion };

            foreach (var prefabRef in
                SystemAPI.Query<RefRO<PrefabRef>>()
                    .WithAll<Created>()
                    .WithNone<Temp, Deleted>())
            {
                if (prefabRef.ValueRO.m_Prefab == pendingPrefab)
                    return true;
            }

            return false;
        }

        private void ClearDefaultCandidateIfNeeded(Entity pendingEntity, BuildingPlacementPending pending)
        {
            bool changed = false;
            if (pending.StartedFrame == 0)
            {
                pending.StartedFrame = UnityEngine.Time.frameCount;
                changed = true;
            }

            if (pending.ToolDefaultSinceFrame != 0)
            {
                pending.ToolDefaultSinceFrame = 0;
                changed = true;
            }

            if (changed)
                SetPendingIfAlive(pendingEntity, pending);
        }

        private void SetPendingIfAlive(Entity pendingEntity, BuildingPlacementPending pending)
        {
            if (m_PendingLookup.HasComponent(pendingEntity))
                m_PendingLookup[pendingEntity] = pending;
        }

        private void CancelPendingPlacement(Entity pendingEntity, BuildingPlacementPending pending)
        {
            m_LastCancelledPendingIndex = pendingEntity.Index;
            m_LastCancelledPendingVersion = pendingEntity.Version;

            var ecb = m_Barrier.CreateCommandBuffer();
            if (pending.RequestId != 0 && BuildingPlacementHandlerRegistry.TryGet(pending.Kind, out var handler))
            {
                RequestResultEmitter.Emit(
                    ecb,
                    pending.RequestId,
                    handler.RequestKind,
                    RequestStatus.Failed,
                    handler.CancelReasonId,
                    SystemAPI.Time.ElapsedTime);
            }

            ecb.DestroyEntity(pendingEntity);
            m_Barrier.AddJobHandleForProducer(Dependency);

            int now = UnityEngine.Time.frameCount;
            int sinceStart = pending.StartedFrame == 0 ? -1 : now - pending.StartedFrame;
            int sinceToolDefault = pending.ToolDefaultSinceFrame == 0 ? -1 : now - pending.ToolDefaultSinceFrame;
            int sinceLastNonDefault = m_LastNonDefaultToolFrame == 0 ? -1 : now - m_LastNonDefaultToolFrame;
            string lastTool = m_LastNonDefaultToolName.Length == 0 ? "<never-non-default>" : m_LastNonDefaultToolName;
            Log.Info(
                $"Placement cancelled: kind={pending.Kind}, requestId={pending.RequestId}, " +
                $"framesSinceStart={sinceStart}, framesSinceToolDefault={sinceToolDefault}, " +
                $"lastNonDefaultTool={lastTool}, framesSinceLastNonDefault={sinceLastNonDefault}, " +
                $"prefab=({pending.PrefabIndex}:{pending.PrefabVersion})");
        }

        public void ValidateAfterLoad()
        {
            m_LastCancelledPendingIndex = 0;
            m_LastCancelledPendingVersion = 0;
            m_LastNonDefaultToolName = string.Empty;
            m_LastNonDefaultToolFrame = 0;
            m_LastNonDefaultToolRef = null;

            if (!m_PendingQuery.TryGetSingletonEntity<BuildingPlacementPending>(out var pendingEntity))
                return;

            EntityManager.DestroyEntity(pendingEntity);
            Log.Info("ValidateAfterLoad: cleared transient BuildingPlacementPending singleton");
        }
    }
}
