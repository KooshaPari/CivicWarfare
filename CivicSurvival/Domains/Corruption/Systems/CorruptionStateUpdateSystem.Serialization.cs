using Unity.Entities;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;

namespace CivicSurvival.Domains.Corruption.Systems
{
    public partial class CorruptionStateUpdateSystem : IDefaultSerializable, IResettable, IPostLoadValidation, IBootDefaultsReset
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

#pragma warning disable CIVIC221 // Deferred: Deserialize → m_RestoredExposure → ValidateAfterLoad writes singleton. Serialize reads singleton directly.
        private float m_RestoredExposure;
#pragma warning restore CIVIC221
        private EntityQuery m_CorruptionSingletonQuery;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                float exposure = 0f;
                if (m_CorruptionSingletonQuery.TryGetSingleton<CorruptionSingleton>(out var singleton))
                    exposure = singleton.AccumulatedExposure;
                CorruptionExposureCodec.Write(new CorruptionExposureState(exposure), writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(CorruptionStateUpdateSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                CorruptionExposureCodec.Read(reader, out var state);
                m_RestoredExposure = state.Exposure;
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
            ResetBootDefaultsFields();
            // Reset singleton entity — prevents stale AccumulatedExposure surviving SetDefaults
            if (m_CorruptionSingletonQuery.TryGetSingletonEntity<CorruptionSingleton>(out var entity))
            {
                EntityManager.SetComponentData(entity, CorruptionSingleton.Default);
            }
        }

        private void ResetBootDefaultsFields()
        {
            m_RestoredExposure = 0f;
            m_InputSnapshotObserverCursor = 0;
            lock (m_Lock) { m_PendingExposure = 0f; }
            m_PrevGameHours = -1.0;
        }

        public void ValidateAfterLoad()
        {
            // After load, old world entities are destroyed — singleton entity gone.
            // OnStartRunning won't re-fire (system was already running), so EnsureExists
            // must be called here to recreate the singleton before writing restored exposure.
            CorruptionSingleton.EnsureExists(EntityManager);

            if (m_RestoredExposure > 0f)
            {
                if (m_CorruptionSingletonQuery.TryGetSingletonEntity<CorruptionSingleton>(out var entity))
                {
                    var state = EntityManager.GetComponentData<CorruptionSingleton>(entity);
                    state.AccumulatedExposure = m_RestoredExposure;
                    EntityManager.SetComponentData(entity, state);
                    Log.Info($"Restored AccumulatedExposure={m_RestoredExposure:F1} from save");
                    m_RestoredExposure = 0f;
                }
            }
        }
    }
}
