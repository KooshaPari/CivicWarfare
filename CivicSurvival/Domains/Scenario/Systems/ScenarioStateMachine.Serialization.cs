using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Interfaces.Services;

namespace CivicSurvival.Domains.Scenario.Systems
{
    public partial class ScenarioStateMachine : IDefaultSerializable, IResettable, IPostLoadValidation, IBootDefaultsReset
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason)
        {
            m_State = ScenarioState.CreateDefault();
            m_Initialized = false;
            m_HasSaveData = false;
            m_MilestoneWeekShown = false;
            m_MilestoneMonthShown = false;
            m_MilestoneQuarterShown = false;
            m_PostCrisisActStartDay = 0;
            m_RoutineTransitionDeferred = false;
            ClearPendingRuntimeTransitions();
            m_Singleton.Invalidate();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var snapshot = new ScenarioStateMachinePersistState(
                    Mod.VERSION,
                    m_State,
                    m_PostCrisisActStartDay,
                    m_MilestoneWeekShown,
                    m_MilestoneMonthShown,
                    m_MilestoneQuarterShown);
                ScenarioStateMachineCodec.Write(snapshot, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // C-5: load IS an epoch boundary. Advance so every preserved pre-load
            // transient (TMS arrivals/snapshots, TAS pending, debris buffers) is stale
            // by construction regardless of its stamped value.
            EnsureEpochClock();
            m_actEpochClock?.AdvanceForLoad();
            EnsureThreatGenerationClock();
            m_threatGenerationClock?.AdvanceForLoadBoundary();
            ClearPendingRuntimeTransitions();

            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, "ScenarioStateMachine"))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                ScenarioStateMachineCodec.Read(reader, out var snapshot);
                m_State = snapshot.State;
                m_PostCrisisActStartDay = snapshot.PostCrisisActStartDay;
                m_MilestoneWeekShown = snapshot.MilestoneWeekShown;
                m_MilestoneMonthShown = snapshot.MilestoneMonthShown;
                m_MilestoneQuarterShown = snapshot.MilestoneQuarterShown;

                m_HasSaveData = true;

                // Write directly to singleton (entity created in OnCreate)
                WriteSingletonFromState();

                Log.Info($"Deserialized v{version} (mod {snapshot.ModVersion}): Act={m_State.CurrentAct}, Day={m_State.WarDay}, Waves={m_State.WavesDefended}, Defeated={m_State.IsDefeated}");
            }
            catch (System.Exception ex)
            {
                Log.Error($"Deserialize failed: {ex}");
                ResetToBootDefaults(ResetReason.DeserializeFailed);
            }
            finally
            {
                SerializationGuard.EndBlock(reader, block);
            }
        }

        public void SetDefaults(Context context) => ResetState();

        public void ResetState()
        {
            m_State = ScenarioState.CreateDefault();
            m_Initialized = false;
            m_HasSaveData = false;
            m_MilestoneWeekShown = false;
            m_MilestoneMonthShown = false;
            m_MilestoneQuarterShown = false;
            m_PostCrisisActStartDay = 0;
            m_RoutineTransitionDeferred = false;
            ClearPendingRuntimeTransitions();
            m_Singleton.Invalidate();
            // FIX S7-04: Ensure singleton reflects defaults immediately.
            WriteSingletonFromState();
            // S26-#12 FIX: Reset modal coordinator to prevent stale lock after load
            CivicSurvival.Core.Services.ModalCoordinator.Instance?.Reset();
            // C-5: new-game / failed-deserialize is also an epoch boundary.
            EnsureEpochClock();
            m_actEpochClock?.AdvanceForLoad();
            EnsureThreatGenerationClock();
            m_threatGenerationClock?.AdvanceForLoadBoundary();
        }

        private void ClearPendingRuntimeTransitions()
        {
            m_PendingStartWar = false;
            m_HasPendingActTransition = false;
            m_PendingActTransition = default;
        }

        /// <summary>
        /// FIX G-S5-02: Explicit post-load validation.
        /// Ensures singleton matches m_State and re-publishes ScenarioTypeDetectedEvent
        /// so dependent systems (OminousSigns, CrisisActCoordinator) have fresh state.
        /// </summary>
        public void ValidateAfterLoad()
        {
            if (m_HasSaveData
                && (m_State.CurrentAct == Act.Adaptation || m_State.CurrentAct == Act.Exodus)
                && m_PostCrisisActStartDay == 0)
            {
                m_PostCrisisActStartDay = m_State.WarDay;
                Log.Warn($"PostCrisisActStartDay healed after load: act={m_State.CurrentAct}, day={m_State.WarDay}");
            }

            WriteSingletonFromState();

            if (m_HasSaveData && m_State.Type != ScenarioType.None)
            {
                // Loaded save with a detected type — re-announce, skip DetectScenarioType.
                AnnounceScenarioType(m_State.Type, m_State.OriginalPopulation);
                m_Initialized = true;
            }
            else
            {
                // New game OR a save with no detected type. Population is already
                // deserialized at this post-load point (decompile-verified: GameManager
                // awaits LoadSimulationData before WorldReady, and this validator runs
                // before CivicGameLifecycle.MarkGameplayReady), so detect deterministically
                // HERE instead of lazily on the first tick. This runs before the player can
                // save, so Type=None can never be persisted into a save.
                DetectScenarioType();
                m_Initialized = true;
            }

            Log.Info($"ValidateAfterLoad: Act={m_State.CurrentAct}, Day={m_State.WarDay}, Type={m_State.Type}, hasSave={m_HasSaveData}");
        }

        private void WriteSingletonFromState()
        {
            var singletonEntity = EnsureSingletonEntity(EntityManager);
            m_SingletonLookup.Update(this);
            m_CurrentActLookup.Update(this);

            var current = EntityManager.GetComponentData<ScenarioSingleton>(singletonEntity);
            current.ScenarioType = m_State.Type;
            if (TryResolveCurrentGameDay(out var gameDay))
                current.GameDay = gameDay.Value;
            current.WarDay = ResolveCurrentWarDay();
            current.PopulationPeak = m_State.PeakPopulation;
            current.IsWarStarted = m_State.CurrentAct != Act.PreWar;
            current.IsDefeated = m_State.IsDefeated;
            current.ExodusRateOverrideFraction = m_State.ExodusRateOverrideFraction;
            current.ShownModals = m_State.ShownModals;
            current.DonorAidReceived = m_State.DonorAidReceived;
            EntityManager.SetComponentData(singletonEntity, current);

            EntityManager.SetComponentData(singletonEntity, new CurrentActSingleton { CurrentAct = m_State.CurrentAct });
        }
    }
}
