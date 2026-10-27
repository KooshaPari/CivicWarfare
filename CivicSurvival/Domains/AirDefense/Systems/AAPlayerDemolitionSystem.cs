using Game;
using Game.Common;
using Game.Simulation;
using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.AirDefense.Logic;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Pause-safe player-demolition handler for AA installations.
    ///
    /// When the player bulldozes an AA prop with the vanilla bulldozer, the tool applies in a
    /// pause-safe phase (vanilla deletes the prop immediately, even on pause) and the
    /// installation sidecar's Building ref goes stale. This system runs in ModificationEnd —
    /// which ticks every frame even while paused, mirroring both vanilla's own bulldoze-refund
    /// phase and our pause-safe AA PLACEMENT — so it detects the stale ref the same frame and:
    ///   - returns the FULL crew (no casualties — the player chose to remove it);
    ///   - refunds the decaying buyer's-remorse fraction of the paid budget, credited
    ///     SYNCHRONOUSLY via CityBudgetService.AddFunds (the same pause-safe path AA placement
    ///     uses to deduct) — a deferred budget request would not resolve until GameSimulation
    ///     ran again, i.e. only after unpause, which is exactly the bug this replaces;
    ///   - records the UI-stats removal and tags the sidecar Deleted.
    ///
    /// Combat loss is NOT handled here — that stays in AACrewReleaseSystem (GameSimulation),
    /// driven by DestroyedBuildingEvent with a survivor/KIA split and no refund (combat only
    /// happens unpaused).
    ///
    /// COMBAT vs DEMOLITION is classified by an orthogonal vanilla signal, not by timing.
    /// Decompile-verified (CS2): the two removal routes are separated by both component and
    /// phase, so they can never be confused:
    ///   - COMBAT destruction routes through Game.Objects.DestroySystem, which runs in
    ///     GameSimulation (pause-gated) and stamps Destroyed on the building (it lingers as
    ///     rubble for frames before any Deleted). That is AACrewReleaseSystem's territory.
    ///   - PLAYER bulldoze routes through the tool pipeline — BulldozeToolSystem (ToolUpdate)
    ///     emits a Delete definition, OriginalDeletedSystem (PreTool) stamps Deleted on the
    ///     original WITHOUT Destroyed. Those tool phases are pumped by ToolSystem from MainLoop,
    ///     outside the SimulationSystem pause gate, which is exactly why a bulldoze applies on
    ///     pause — and why this system lives in the equally pause-safe ModificationEnd.
    /// So a Destroyed building is, by construction, a combat loss the bulldozer cannot produce.
    /// We skip it below and let the combat path be its single owner. That makes both paths
    /// race-free at the signal level: even in the narrow Destroyed+Deleted window neither path
    /// double-processes the same installation (no full-crew + wrong refund here, no KIA split
    /// there). The Deleted tag this system applies tags the demolished AA before any later pass.
    /// </summary>
    [ActIndependent]
    public partial class AAPlayerDemolitionSystem : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("AAPlayerDemolition");

        private EntityQuery m_AAQuery;
        private ModificationEndBarrier m_Barrier = null!;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private bool m_LoadGate;
#pragma warning disable CIVIC229 // System reference — UI stats cache is owned by AirDefenseStateSystem.
        private AirDefenseStateSystem m_StateSystem = null!;
#pragma warning restore CIVIC229

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AAQuery = GetEntityQuery(
                ComponentType.ReadOnly<AirDefenseInstallation>(),
                ComponentType.Exclude<Deleted>()
            );
            m_Barrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            RequireForUpdate(m_AAQuery);
            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_StateSystem ??= FeatureRegistry.Instance.Require<AirDefenseStateSystem>();
        }

        /// <summary>Load-window gate — symmetric with AACrewReleaseSystem. While raised the
        /// system must NOT run: freshly deserialized installations have building refs that are
        /// not yet remapped/validated, so a stale-ref check would false-positive into a
        /// post-load phantom refund + crew release.</summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LoadGate = true;
        }

        /// <summary>PLVS Phase 2 — authoritative post-load AA set now has valid remapped refs.</summary>
        public void ValidateAfterLoad() => m_LoadGate = false;

        public void ResetState() => m_LoadGate = true;

        protected override void OnUpdateImpl()
        {
            if (m_LoadGate) return;

            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);

            // WHY A POLL AND NOT A Deleted-event query — DO NOT "optimize" this into one.
            // Vanilla reacts to deletions with RequireForUpdate(All<Deleted>) because vanilla OWNS
            // the schedule: it guarantees its reaction systems run before the cleanup pass recycles
            // the Deleted entity, in a known phase, with the one-frame transient tag still alive. A
            // mod does NOT own that order — our position relative to vanilla's Deleted-cleanup, and
            // relative to OTHER mods touching the same entities, is not guaranteed. Observing the
            // transient Deleted tag would race the cleanup (miss the window → lose the event for
            // good, the entity is already gone) or collide with another mod — the cross-mod crash
            // this project already hit when reacting to entity deletion.
            //
            // So we POLL version-safe authoritative state instead: each frame we read the building
            // ref's Exists/Deleted/Destroyed via ComponentLookup in our own (pause-safe ModEnd)
            // phase. Building is index+version, so a recycled prop fails Exists by version — we
            // cannot miss the removal or false-positive on a reused index, no matter which job or
            // frame the bulldoze landed in, and we never delete or race vanilla / other mods (we
            // only tag our own sidecar). The small per-frame scan is the deliberate price of that
            // timing- and cross-mod-independence, not an un-optimized leftover. (The matching
            // perf invariant — heavy work stays gated so the idle scan stays cheap — is marked at
            // the buildingGone branch below.)
            bool haveTime = GameTimeSystem.TryGetGameHours(out float currentGameHours);
            var aaUnits = BalanceConfig.Current.AAUnits;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            foreach (var (aa, aaEntity) in
                SystemAPI.Query<RefRO<AirDefenseInstallation>>()
                .WithNone<Deleted>()
                .WithEntityAccess())
            {
                var buildingEntity = aa.ValueRO.GetBuildingEntity();

                // Combat-loss exclusion (see class summary): Destroyed is stamped only by the
                // GameSimulation DestroySystem, never by the bulldozer. A Destroyed building is a
                // combat loss owned by AACrewReleaseSystem (survivor/KIA split, no refund), so we
                // skip it here even if it has also reached Deleted — the combat path is the single
                // owner. This is a partition by an orthogonal signal, not a timing guard.
                if (m_DestroyedLookup.HasComponent(buildingEntity))
                    continue;

                bool buildingGone = !SystemAPI.Exists(buildingEntity)
                    || m_DeletedLookup.HasComponent(buildingEntity);
                // PERF-LOCK: the per-frame poll stays O(AA)-cheap only because the per-AA body is
                // liveness-only — all heavy work (crew release, refund, UI stats, Deleted tag) is
                // gated behind this buildingGone branch. Moving any of it above this continue makes
                // the poll pay full cost for every AA every frame, even on pause.
                // Do NOT throttle the scan to "save" cost either: throttling is version-safe (Exists
                // by index+version still catches the removal at any cadence) but it (a) delays the
                // crew + cash refund by up to N frames, breaking parity with vanilla's same-frame
                // bulldoze refund, and (b) drifts the refund amount — AARefund.Compute decays the
                // buyer's-remorse fraction by game time, so a late tick refunds less, making the
                // payout depend on where the demolish landed in the throttle cycle (materially only
                // at high game-speed; at 1x the frame-scale delay is negligible against the
                // day-scale tiers, and on pause game time is frozen so there is no drift at all).
                if (!buildingGone)
                    continue;

                if (!ecbCreated) { ecb = m_Barrier.CreateCommandBuffer(); ecbCreated = true; }

                var removedAA = aa.ValueRO;

                // Player demolition returns the FULL crew — no casualties (deliberate removal).
                if (removedAA.CrewAssigned > 0)
                {
                    AACrewRequests.CreateReleaseRequest(ecb, removedAA.CrewAssigned, removedAA.Type, aaEntity);
                    Log.Info($"{removedAA.Type} demolished: {removedAA.CrewAssigned} crew released");
                }

                // Vanilla buyer's-remorse refund — synchronous, pause-safe credit. PaidBudget == 0
                // (credit/Heritage placements, legacy saves) → Compute returns 0 → no refund.
                if (haveTime)
                {
                    int refund = AARefund.Compute(
                        removedAA.PaidBudget, removedAA.PlacedGameHours, currentGameHours, aaUnits);
                    if (refund > 0)
                    {
                        var result = CityBudgetService.AddFunds(World, refund, "AARefund", BudgetIncomeKind.Refund);
                        if (result == BudgetResult.Ok)
                            Log.Info($"{removedAA.Type} demolished: refund {refund} (paid {removedAA.PaidBudget})");
                        else
                            Log.Warn($"{removedAA.Type} demolished: refund {refund} failed ({result})");
                    }
                }

                m_StateSystem.RecordUiStatsInstallationRemoved(in removedAA);
                ecb.AddComponent<Deleted>(aaEntity);
            }

            if (ecbCreated)
                m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }
}
