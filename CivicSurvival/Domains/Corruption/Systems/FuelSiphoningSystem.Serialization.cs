using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// FuelSiphoningSystem - Save/Load serialization (IDefaultSerializable).
    /// Serializes FuelSiphoningSingleton state.
    /// The persisted counter is a GLOBAL HOUR (day*24+hour) since the siphon payout pays
    /// hourly slices; codec field names still say "day" because the stored value is
    /// just "highest handled tick" and renaming is a save-format change for no gain.
    /// </summary>
    #pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class FuelSiphoningSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

    #pragma warning restore CIVIC223
        [System.NonSerialized] private bool m_HasRestoredFuelSiphoning;

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            ResetBootDefaultsFields();
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (m_SingletonQuery.TryGetSingletonEntity<FuelSiphoningSingleton>(out var entity))
            {
                EntityManager.SetComponentData(entity, m_LiveFuelSiphoning);
            }
        }

        private void ResetBootDefaultsFields()
        {
            m_HasRestoredFuelSiphoning = false;
            m_LiveFuelSiphoning = FuelSiphoningSingleton.Default;
            m_HourDedup.Reset();
            m_IncomeRemainder = 0.0; // drop the fractional carry with the rest of the state
            m_SingletonMissingWarned = false; // L-107: Allow re-warning after reset
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {

                var singleton = ReadSingleton();
                var state = new FuelSiphoningPersistState(
                    singleton.SiphonPercent,
                    m_HourDedup.LastProcessedDay);
                FuelSiphoningCodec.Write(state, writer);

            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(FuelSiphoningSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                FuelSiphoningCodec.Read(reader, out var state);
                m_HourDedup = DayChangedDedup.FromSave(state.LastProcessedDay);
                m_LiveFuelSiphoning = new FuelSiphoningSingleton { SiphonPercent = state.SiphonPercent };
                m_HasRestoredFuelSiphoning = true;

                Log.Info($"Deserialized v{version}: siphon={state.SiphonPercent}%");
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
            m_HourDedup = PostLoadDayClamp.ClampDedupToActivatedGameDay(m_HourDedup, Log, nameof(FuelSiphoningSystem));
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            FuelSiphoningSingleton.EnsureExists(entityManager);
            if (!m_HasRestoredFuelSiphoning)
                m_LiveFuelSiphoning = FuelSiphoningSingleton.Default;
            if (m_SingletonQuery.TryGetSingletonEntity<FuelSiphoningSingleton>(out var entity))
            {
                entityManager.SetComponentData(entity, m_LiveFuelSiphoning);
            }
            m_HasRestoredFuelSiphoning = false;
        }
    }
}
