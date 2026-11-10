using Colossal.Serialization.Entities;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;

namespace CivicSurvival.Domains.Engineering.Systems
{
    public partial class WinterMultiplierSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        [System.NonSerialized] private bool m_PendingBootDefaultWinterRestore;

        public void ResetToBootDefaults(ResetReason reason)
        {
            // 1.0f = "no winter" baseline (same as OnBecameDisabled).
            // MaxValue would fail previousMultiplier < crisisThreshold → WinterCrisis skipped.
            m_LastMultiplier = 1.0f;
            m_WasWinterActive = false;
            m_TemperatureWarningLogged = false;
            m_PendingBootDefaultWinterRestore = true;
            Log.Info($"[{nameof(WinterMultiplierSystem)}] Boot reset: reason={reason}");
        }

        /// <summary>
        /// Reset serializable state to defaults.
        /// Called on new game and when save version is incompatible.
        /// </summary>
        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        private void ResetState()
        {
            // 1.0f = "no winter" baseline (same as OnBecameDisabled).
            // MaxValue would fail previousMultiplier < crisisThreshold → WinterCrisis skipped.
            // If first real multiplier == 1.0, epsilon skips update — correct (warm start, no events).
            m_LastMultiplier = 1.0f;
            m_WasWinterActive = false;
            m_TemperatureWarningLogged = false;
            WriteWinterSingleton(false);
            Log.Info($"[{nameof(WinterMultiplierSystem)}] SetDefaults: Reset to fresh state");
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var state = new WinterMultiplierPersistState(
                    m_LastMultiplier,
                    m_WasWinterActive);
                WinterMultiplierCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(WinterMultiplierSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(WinterMultiplierSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                WinterMultiplierCodec.Read(reader, out var state);
                bool winterEnabled = IsWinterFeatureEnabledForLoad();
                m_LastMultiplier = winterEnabled ? state.LastMultiplier : 1.0f;
                m_WasWinterActive = winterEnabled && state.WasWinterActive;
                // LOAD-INVARIANT: Deserialize buffers only scalar state; PLVS publishes the singleton.
                m_PendingBootDefaultWinterRestore = true;

                Log.Info($"[{nameof(WinterMultiplierSystem)}] Deserialized v{version}: LastMultiplier={m_LastMultiplier:F2}, WasWinterActive={m_WasWinterActive}");
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

        private void WriteWinterSingleton(bool isWinterActive)
        {
            Core.Components.Domain.Engineering.WinterStateSingleton.EnsureExists(EntityManager);
            using var winterQuery = EntityManager.CreateEntityQuery(typeof(Core.Components.Domain.Engineering.WinterStateSingleton));
            if (!winterQuery.TryGetSingletonEntity<Core.Components.Domain.Engineering.WinterStateSingleton>(out var winterEntity))
            {
                Log.Error("WinterStateSingleton missing after EnsureExists; winter flag write skipped");
                return;
            }
            EntityManager.SetComponentData(winterEntity, new Core.Components.Domain.Engineering.WinterStateSingleton { IsWinterActive = isWinterActive });
        }

        public void ValidateAfterLoad()
        {
            if (!IsWinterFeatureEnabledForLoad())
            {
                m_LastMultiplier = 1.0f;
                m_WasWinterActive = false;
                WriteWinterSingleton(false);
                m_PendingBootDefaultWinterRestore = false;
                return;
            }

            if (!m_PendingBootDefaultWinterRestore)
                return;

            WriteWinterSingleton(m_WasWinterActive);
            m_PendingBootDefaultWinterRestore = false;
        }

        private bool IsWinterFeatureEnabledForLoad()
        {
            m_Settings ??= ServiceRegistry.TryGet<ModSettings>();
            return m_Settings?.WinterMultiplierEnabled != false;
        }
    }
}
