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
    /// ConstructionKickbackSystem - Save/Load serialization (IDefaultSerializable).
    /// Serializes ConstructionKickbackSingleton state (percent + accrued pending payout).
    /// The persisted counter is a GLOBAL HOUR (day*24+hour) since the kickback payout gate pays
    /// hourly slices; codec field names still say "day" because the stored value is
    /// just "highest handled tick" and renaming is a save-format change for no gain.
    /// </summary>
    #pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class ConstructionKickbackSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

    #pragma warning restore CIVIC223
        [System.NonSerialized] private bool m_HasRestoredKickback;

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            ResetBootDefaultsFields();
            if (m_SingletonQuery.TryGetSingletonEntity<ConstructionKickbackSingleton>(out var entity))
            {
                EntityManager.SetComponentData(entity, m_LiveKickback);
            }
        }

        private void ResetBootDefaultsFields()
        {
            m_HasRestoredKickback = false;
            m_LiveKickback = ConstructionKickbackSingleton.Default;
            m_HourDedup.Reset();
            m_SingletonMissingWarned = false;
            m_AccruedThisFrame.Clear();
            m_AccruedFrame = 0;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var singleton = ReadSingleton();
                var state = new ConstructionKickbackPersistState(
                    singleton.KickbackPercent,
                    singleton.PendingPayout,
                    m_HourDedup.LastProcessedDay);
                ConstructionKickbackCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(ConstructionKickbackSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                ConstructionKickbackCodec.Read(reader, out var state);
                m_HourDedup = DayChangedDedup.FromSave(state.LastProcessedDay);
                m_LiveKickback = new ConstructionKickbackSingleton
                {
                    KickbackPercent = state.KickbackPercent,
                    PendingPayout = state.PendingPayout
                };
                m_HasRestoredKickback = true;

                Log.Info($"Deserialized v{version}: kickback={state.KickbackPercent}%, pending=${state.PendingPayout:N0}");
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
            m_HourDedup = PostLoadDayClamp.ClampDedupToActivatedGameDay(m_HourDedup, Log, nameof(ConstructionKickbackSystem));
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            ConstructionKickbackSingleton.EnsureExists(entityManager);
            if (!m_HasRestoredKickback)
                m_LiveKickback = ConstructionKickbackSingleton.Default;
            if (m_SingletonQuery.TryGetSingletonEntity<ConstructionKickbackSingleton>(out var entity))
            {
                entityManager.SetComponentData(entity, m_LiveKickback);
            }
            m_HasRestoredKickback = false;
        }
    }
}
