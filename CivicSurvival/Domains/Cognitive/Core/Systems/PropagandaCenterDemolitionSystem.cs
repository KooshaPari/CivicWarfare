using Game;
using Game.Common;
using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Pause-safe lifecycle owner for Propaganda Center installations whose placed building is
    /// gone — the single killer of <see cref="PropagandaCenterInstallation"/> entities. Runs in
    /// ModificationEnd, which ticks every frame even while paused, mirroring the pause-safe Center
    /// PLACEMENT commit and the AA demolition precedent (<c>AAPlayerDemolitionSystem</c>), so a
    /// bulldoze or an enemy strike re-locks the counter-propaganda layer the same frame:
    ///   - PLAYER bulldoze (building gone WITHOUT Destroyed) → refund the base placement cost
    ///     (<see cref="PropagandaCenterInstallation.PaidBudget"/>; upgrade spend is not refunded)
    ///     credited SYNCHRONOUSLY via <c>CityBudgetService.AddFunds</c> — a deferred budget
    ///     request would not resolve until GameSimulation ran again, i.e. only after unpause —
    ///     then destroy the installation;
    ///   - ENEMY destruction (building carries Destroyed) → destroy the installation, NO refund.
    ///
    /// The two routes partition by the same orthogonal vanilla signal AA uses (decompile-verified
    /// there): Destroyed is stamped only by the GameSimulation DestroySystem; the bulldozer's tool
    /// pipeline stamps Deleted WITHOUT Destroyed. The Destroyed branch is checked FIRST so a ruin
    /// that later also reaches Deleted can never convert into a player refund.
    ///
    /// <c>PropagandaCenterSystem</c> merely skips dead installs in its scan (its derived state
    /// re-locks at most one throttled tick later — or the same frame via the pause poll); it does
    /// not destroy them.
    /// </summary>
    [ActIndependent]
    public partial class PropagandaCenterDemolitionSystem : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("PropagandaCenterDemolition");

        private EntityQuery m_InstallQuery;
        private ModificationEndBarrier m_Barrier = null!;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private bool m_LoadGate;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_InstallQuery = GetEntityQuery(ComponentType.ReadOnly<PropagandaCenterInstallation>());
            m_Barrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            RequireForUpdate(m_InstallQuery);
            Log.Info("Created");
        }

        /// <summary>Load-window gate — symmetric with AAPlayerDemolitionSystem. While raised the
        /// system must NOT run: freshly deserialized installations have building refs that are not
        /// yet remapped/validated, so a stale-ref check would false-positive into a post-load
        /// phantom refund.</summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LoadGate = true;
        }

        /// <summary>PLVS Phase 2 — the post-load installation set now has valid remapped refs.</summary>
        public void ValidateAfterLoad() => m_LoadGate = false;

        public void ResetState() => m_LoadGate = true;

        protected override void OnUpdateImpl()
        {
            if (m_LoadGate) return;

            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);

            // Per-frame liveness POLL, not a Deleted-event query — same reasoning as
            // AAPlayerDemolitionSystem: a mod does not own vanilla's schedule, so observing the
            // transient Deleted tag races vanilla's cleanup (miss the window → lose the event for
            // good) and other mods. Polling version-safe authoritative state (Exists / Deleted /
            // Destroyed on the index+version building ref) in our own pause-safe phase can neither
            // miss a removal nor false-positive on a recycled index.
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            foreach (var (install, installEntity) in
                SystemAPI.Query<RefRO<PropagandaCenterInstallation>>().WithEntityAccess())
            {
                var building = install.ValueRO.GetBuildingEntity();

                // Enemy destruction FIRST — partition by the orthogonal Destroyed signal (as in
                // AA): the bulldozer cannot produce Destroyed, so this branch is combat-only, and
                // checking it before the gone-check keeps the later Destroyed→Deleted rubble chain
                // from converting into a player refund.
                if (m_DestroyedLookup.HasComponent(building))
                {
                    if (!ecbCreated) { ecb = m_Barrier.CreateCommandBuffer(); ecbCreated = true; }
                    ecb.DestroyEntity(installEntity);
                    Log.Info("Propaganda Center destroyed by enemy — installation cleared, layer re-locked (no refund)");
                    continue;
                }

                bool buildingGone = !SystemAPI.Exists(building)
                    || m_DeletedLookup.HasComponent(building);
                // PERF-LOCK: the per-frame poll stays cheap (0-1 installations) only because the
                // per-install body is liveness-only — the refund + destroy + logging are gated
                // behind this buildingGone branch. Moving any of it above this continue makes the
                // poll pay full cost every frame, even on pause.
                if (!buildingGone)
                    continue;

                if (!ecbCreated) { ecb = m_Barrier.CreateCommandBuffer(); ecbCreated = true; }

                // Player bulldoze → synchronous pause-safe refund of the base placement cost.
                // PaidBudget == 0 (free/legacy placements) → nothing to credit.
                int refund = install.ValueRO.PaidBudget;
                if (refund > 0)
                {
                    var result = CityBudgetService.AddFunds(World, refund, "CenterDemolitionRefund", BudgetIncomeKind.Refund);
                    if (result == BudgetResult.Ok)
                        Log.Info($"Propaganda Center demolished: refund {refund}");
                    else
                        Log.Warn($"Propaganda Center demolished: refund {refund} failed ({result})");
                }

                ecb.DestroyEntity(installEntity);
                Log.Info("Propaganda Center demolished by player — installation cleared, layer re-locked");
            }

            if (ecbCreated)
                m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }
}
