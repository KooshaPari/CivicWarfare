using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// DraftExemptionSystem - Save/Load serialization (IDefaultSerializable).
    /// Serializes DraftExemptionSingleton state (deferral rate + permanent disability headcount).
    /// The persisted counter is a GLOBAL HOUR (day*24+hour) since the exemption payout pays
    /// hourly slices; codec field names still say "day" because the stored value is
    /// just "highest handled tick" and renaming is a save-format change for no gain.
    /// </summary>
    #pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class DraftExemptionSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

    #pragma warning restore CIVIC223
        [System.NonSerialized] private bool m_HasRestoredDraftExemption;

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            ResetBootDefaultsFields();
            if (m_SingletonQuery.TryGetSingletonEntity<DraftExemptionSingleton>(out var entity))
            {
                EntityManager.SetComponentData(entity, m_LiveDraftExemption);
            }
        }

        private void ResetBootDefaultsFields()
        {
            m_HasRestoredDraftExemption = false;
            m_LiveDraftExemption = DraftExemptionSingleton.Default;
            m_HourDedup.Reset();
            m_IncomeRemainder = 0.0; // drop the fractional carry with the rest of the state
            m_SingletonMissingWarned = false;
            m_LastOfferToastId = -1;
            m_OfferCash = 0;
            m_PendingAcceptToastId = -1;
            m_PrevGameHours = 0.0;
            // Force a re-subscribe pass on the next update; SubscribeToastEvents
            // unsubscribes first, so an already-live subscription cannot double up.
            m_ToastEventsSubscribed = false;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var singleton = ReadSingleton();
                var state = new DraftExemptionPersistState(
                    singleton.RatePercent,
                    singleton.DisabilityHeadcount,
                    m_HourDedup.LastProcessedDay);
                DraftExemptionCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(DraftExemptionSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                DraftExemptionCodec.Read(reader, out var state);
                m_HourDedup = DayChangedDedup.FromSave(state.LastProcessedDay);
                m_LiveDraftExemption = new DraftExemptionSingleton
                {
                    RatePercent = state.RatePercent,
                    DisabilityHeadcount = state.DisabilityHeadcount
                };
                m_HasRestoredDraftExemption = true;

                Log.Info($"Deserialized v{version}: rate={state.RatePercent}%, disability={state.DisabilityHeadcount}");
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

        public void ValidateAfterLoad()
        {
            // ORDER-INVARIANT: sibling Deserialize order does not guarantee that GameTimeSystem
            // has restored its snapshot. Clamp only in PLVS after GameTime activation.
            m_HourDedup = PostLoadDayClamp.ClampDedupToActivatedGameDay(m_HourDedup, Log, nameof(DraftExemptionSystem));
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            DraftExemptionSingleton.EnsureExists(entityManager);
            if (!m_HasRestoredDraftExemption)
                m_LiveDraftExemption = DraftExemptionSingleton.Default;
            if (m_SingletonQuery.TryGetSingletonEntity<DraftExemptionSingleton>(out var entity))
            {
                entityManager.SetComponentData(entity, m_LiveDraftExemption);
            }
            m_HasRestoredDraftExemption = false;
        }
    }
}
