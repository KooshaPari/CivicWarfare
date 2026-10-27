using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// EmergencyFundSystem - Save/Load serialization (IDefaultSerializable).
    /// Serializes EmergencyFundSingleton state.
    /// The persisted counter is a GLOBAL HOUR (day*24+hour) since the raid payout pays
    /// hourly slices; codec field names still say "day" because the stored value is
    /// just "highest handled tick" and renaming is a save-format change for no gain.
    /// </summary>
    #pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class EmergencyFundSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

    #pragma warning restore CIVIC223
        [System.NonSerialized] private bool m_HasRestoredEmergencyFund;

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            ResetBootDefaultsFields();
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (m_SingletonQuery.TryGetSingletonEntity<EmergencyFundSingleton>(out var entity))
            {
                EntityManager.SetComponentData(entity, m_LiveEmergencyFund);
            }
            if (m_ConfigQuery.TryGetSingletonEntity<EmergencyFundSettings>(out var configEntity))
            {
                EntityManager.SetComponentData(configEntity, m_LiveEmergencyFundSettings);
            }
        }

        private void ResetBootDefaultsFields()
        {
            m_HasRestoredEmergencyFund = false;
            m_LiveEmergencyFund = EmergencyFundSingleton.Default;
            m_LiveEmergencyFundSettings = EmergencyFundSettings.Default;
            m_HourDedup.Reset();
            m_SingletonMissingWarned = false; // L-107: Allow re-warning after reset
            m_ConfigMissingWarned = false;    // L-107: Allow re-warning after reset
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {

                var singleton = ReadSingleton();
                var config = ReadConfig();
                var state = new EmergencyFundPersistState(
                    singleton.InitialBalance,
                    config.WithdrawPercent,
                    singleton.WithdrawnAmount,
                    m_HourDedup.LastProcessedDay);
                EmergencyFundCodec.Write(state, writer);

            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(EmergencyFundSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {

                EmergencyFundCodec.Read(reader, BalanceConfig.Current.EmergencyFund.InitialBalance, out var state);
                m_HourDedup = DayChangedDedup.FromSave(state.LastProcessedDay);
                m_LiveEmergencyFund = new EmergencyFundSingleton
                {
                    InitialBalance = state.InitialBalance,
                    WithdrawnAmount = state.WithdrawnAmount
                };
                m_LiveEmergencyFundSettings = new EmergencyFundSettings
                {
                    WithdrawPercent = state.WithdrawPercent
                };
                m_HasRestoredEmergencyFund = true;

                Log.Info($"Deserialized v{version}: {state.WithdrawPercent}% withdraw, ${state.WithdrawnAmount:N0} withdrawn");
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
            m_HourDedup = PostLoadDayClamp.ClampDedupToActivatedGameDay(m_HourDedup, Log, nameof(EmergencyFundSystem));
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            EmergencyFundSingleton.EnsureExists(entityManager);
            if (!m_HasRestoredEmergencyFund)
            {
                m_LiveEmergencyFund = EmergencyFundSingleton.Default;
                m_LiveEmergencyFundSettings = EmergencyFundSettings.Default;
            }
            if (m_SingletonQuery.TryGetSingletonEntity<EmergencyFundSingleton>(out var entity))
            {
                entityManager.SetComponentData(entity, m_LiveEmergencyFund);
            }
            if (m_ConfigQuery.TryGetSingletonEntity<EmergencyFundSettings>(out var configEntity))
            {
                entityManager.SetComponentData(configEntity, m_LiveEmergencyFundSettings);
            }
            m_HasRestoredEmergencyFund = false;
        }
    }
}
