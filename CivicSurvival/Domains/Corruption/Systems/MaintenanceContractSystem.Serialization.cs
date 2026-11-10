using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.Corruption;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// MaintenanceContractSystem - Save/Load serialization.
    /// REFACTORED: Only persists timing state. Pending offer state is now stored
    /// as PendingProcurement ECS component on building entities (ISerializable).
    /// </summary>
    #pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class MaintenanceContractSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        public void ResetToBootDefaults(ResetReason reason) => ResetBootDefaultsFields();

    #pragma warning restore CIVIC223

        private void ResetState()
        {
            ResetBootDefaultsFields();
        }

        private void ResetBootDefaultsFields()
        {
            m_LastOfferGameDay = 0f;
            m_PrevGameHours = 0f;
            m_LastProcurementToastId = -1;
            m_ProcurementToastBuildingIndex = -1;
            m_ProcurementToastBuildingVersion = -1;
            m_PendingClearToastId = -1; // FIX W2-M6: Reset deferred toast clear state
            m_PendingClearReason = null;
            m_ToastEventsSubscribed = false; // FIX W6-H1: Force re-subscribe on next resolve
            m_ReconcilePendingProcurementAfterLoad = false;
            m_DebugOfferRequested = false; // Deferred debug request must not survive load

#pragma warning disable CIVIC458 // Maintenance contract UI uses a plain owner-side read model, not VersionedView publication.
            m_UiModel = MaintenanceContractUiSnapshot.Empty;
#pragma warning restore CIVIC458
            if (m_BuildingsWithContracts.IsCreated) m_BuildingsWithContracts.Clear();
            m_DmsCleanupDistricts.Clear();
            m_DmsCleanupBuildingKeys.Clear();
            m_ModernizationService = null!; // Force service-boundary re-resolve on next update (stale on hot-reload)
            m_ReputationService = null!;
        }

        public void SetDefaults(Context context) => ResetState();
        void IResettable.ResetState() => ResetState();

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                // ContractStatsSingleton is fully derived from live ContractData and is
                // recomputed on load by SeedUiModelAfterLoad; persisting the counts here
                // produced a dead field that ValidateAfterLoad immediately overwrote.
                var state = new MaintenanceContractPersistState(
                    m_LastOfferGameDay,
                    m_PrevGameHours);
                MaintenanceContractCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(MaintenanceContractSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                MaintenanceContractCodec.Read(reader, out var state);
                m_LastOfferGameDay = state.LastOfferGameDay;
                m_PrevGameHours = state.PrevGameHours;
                // Timing clamp moved to ValidateAfterLoad: GameTimeSystem is not yet
                // activated during Deserialize (vanilla invokes Deserialize before
                // OnGameLoaded), so reading current game time here would always
                // throw / return 0 and clamp persisted values to bogus baselines.
                m_ReconcilePendingProcurementAfterLoad = true;

                // NOTE: Old pending offer data was serialized here in v1.
                // PendingProcurement is now an IEnableableComponent on building entities
                // with its own ISerializable implementation. Enableable state is
                // reconciled in the simulation phase after load so a visible offer
                // remains actionable without touching the serialization codec.
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

        /// <summary>
        /// IPostLoadValidation entry point. Runs after every system has deserialized
        /// AND GameTimeSystem has activated, so it's safe to read current game time
        /// here. Clamps persisted timing baselines that could otherwise be ahead of
        /// the actual current time (corrupted save, hand-edited file, dev rollback).
        /// </summary>
        public void ValidateAfterLoad()
        {
            // LOAD-INVARIANT: ValidateAfterLoad may run before GameTime activation after
            // lifecycle reordering; never fall back to throwing GameTimeSystem accessors.
            int currentDay = 0;
            if (!GameTimeSystem.TryGetGameHours(out var currentGameHours))
            {
                Log.Warn("GameTimeSystem unavailable during MaintenanceContract ValidateAfterLoad; skipping clamp");
            }
            else
            {
                float currentGameDay = currentGameHours / GameRate.HOURS_PER_DAY;
                currentDay = (int)currentGameDay;
                if (m_LastOfferGameDay > currentGameDay)
                    m_LastOfferGameDay = currentGameDay;

                if (m_PrevGameHours > currentGameHours)
                    m_PrevGameHours = currentGameHours;
            }

            SeedUiModelAfterLoad(currentDay);
        }

        public void OnLoadRestore(Unity.Entities.EntityManager entityManager)
        {
            // ContractStatsSingleton is derived from live ContractData, not persisted.
            // Ensure the singleton entity exists so the post-load recount in
            // ValidateAfterLoad/SeedUiModelAfterLoad has a target to write.
            ContractStatsSingleton.EnsureExists(entityManager);
            m_ReconcilePendingProcurementAfterLoad = true;
        }
    }
}
