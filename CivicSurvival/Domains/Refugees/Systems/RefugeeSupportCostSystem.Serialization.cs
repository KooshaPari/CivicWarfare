using Colossal.Serialization.Entities;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Types;

namespace CivicSurvival.Domains.Refugees.Systems
{
    /// <summary>
    /// S15-5 FIX: Serialize m_LastDeductionGameHours to prevent double-charge on load.
    /// Without this, every save/load triggers an immediate refugee support deduction
    /// (m_LastDeductionGameHours defaults to 0, so interval check passes instantly).
    /// </summary>
    public partial class RefugeeSupportCostSystem : IDefaultSerializable, IResettable, IPostLoadValidation, IBootDefaultsReset
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetState();

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            m_LastDeductionGameHours = 0.0;
            m_LastRefugeeCount = 0;
            m_LastDeductionAmount = 0L;
            InitializeGate();
            m_RefugeeCountBinding?.Update(0);
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var state = new RefugeeSupportCostPersistState(
                    m_LastDeductionGameHours,
                    m_LastRefugeeCount,
                    m_LastDeductionAmount);
                RefugeeSupportCostCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(RefugeeSupportCostSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(RefugeeSupportCostSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                RefugeeSupportCostCodec.Read(reader, out var state);
                m_LastDeductionGameHours = state.LastDeductionGameHours;
                m_LastRefugeeCount = state.LastRefugeeCount;
                m_LastDeductionAmount = state.LastDeductionAmount;
                Log.Info($"Deserialized: LastDeductionGameHours={m_LastDeductionGameHours:F1} (scenario gate reset)");
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

            InitializeGate();
        }

        public void ValidateAfterLoad()
        {
            InitializeGate();
        }
    }
}
