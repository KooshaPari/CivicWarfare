using Colossal.Serialization.Entities;
using Game.Citizens;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Refugees.Data;

namespace CivicSurvival.Domains.Refugees.Systems
{
    /// <summary>
    /// RefugeeMigrationSystem - Save/Load serialization (IDefaultSerializable).
    /// Persists migration batch index to prevent duplicate chirper messages.
    ///
    /// IPostLoadValidation: reconciles the NeedsRefugeeRelocation marker's PRESENCE against
    /// the durable HomelessHousehold.m_TempHome on every load. The marker is a plain tag
    /// (presence ⟺ needs relocation), and IEmptySerializable round-trips its presence, so a
    /// normal save loads already correct. This pass repairs drift — chiefly a save taken
    /// after a sheltering park was destroyed but before the orphan scan re-armed the marker
    /// (m_TempHome dangling yet marker absent) — by Adding it where a refugee is at the
    /// border/orphaned and Removing it where the refugee sits in a live park.
    /// </summary>
    public partial class RefugeeMigrationSystem : IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // Post-load reconcile reads m_TempHome to recompute the NeedsRefugeeRelocation
        // marker presence. Declared here, co-located with its only user (ValidateAfterLoad);
        // initialized in OnCreate (main partial). .Update(this) is called in
        // ValidateAfterLoad before reading (IPostLoadValidation lookup-staleness contract).
        private ComponentLookup<HomelessHousehold> m_HomelessLookup;

        public void ResetToBootDefaults(ResetReason reason)
        {
            m_LastCheckGameHours = 0.0;
            m_MigrationBatchIndex = 0;
            m_Random = new Unity.Mathematics.Random(1u);
            m_RandomState = unchecked((int)m_Random.state);
        }

        public void SetDefaults(Context context) => ResetState();

        void IResettable.ResetState() => ResetState();

        /// <summary>
        /// Reconcile the NeedsRefugeeRelocation marker's PRESENCE for every refugee against
        /// the durable m_TempHome: present ⟺ m_TempHome is not a live park (border or orphan).
        /// Presence round-trips via IEmptySerializable, so a normal load needs no change; this
        /// repairs a save taken mid-destruction (a sheltering park gone before the orphan scan
        /// ran → marker still absent) and any other drift. Add/Remove are batched so the
        /// structural changes do not invalidate m_HomelessLookup mid-scan.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_HomelessLookup.Update(this);

            // Live park set (durable truth: a refugee in a live park needs no relocation).
            var parks = m_ParkQuery.ToEntityArray(Allocator.Temp);
            var liveParkSet = new NativeHashSet<Entity>(parks.Length == 0 ? 1 : parks.Length, Allocator.Temp);
            for (int i = 0; i < parks.Length; i++)
                liveParkSet.Add(parks[i]);

            var refugees = m_RefugeeAllQuery.ToEntityArray(Allocator.Temp);
            var toAdd = new NativeList<Entity>(Allocator.Temp);
            var toRemove = new NativeList<Entity>(Allocator.Temp);
            for (int i = 0; i < refugees.Length; i++)
            {
                Entity refugee = refugees[i];
                if (!m_HomelessLookup.HasComponent(refugee))
                    continue;

                Entity tempHome = m_HomelessLookup[refugee].m_TempHome;
                bool needs = !liveParkSet.Contains(tempHome); // border or orphan
                bool has = EntityManager.HasComponent<NeedsRefugeeRelocation>(refugee);
                if (needs && !has)
                    toAdd.Add(refugee);
                else if (!needs && has)
                    toRemove.Add(refugee);
            }

            if (toAdd.Length > 0)
                EntityManager.AddComponent<NeedsRefugeeRelocation>(toAdd.AsArray());
            if (toRemove.Length > 0)
                EntityManager.RemoveComponent<NeedsRefugeeRelocation>(toRemove.AsArray());

            Log.Info($"Post-load relocation markers reconciled: +{toAdd.Length} -{toRemove.Length}");

            if (toAdd.IsCreated) toAdd.Dispose();
            if (toRemove.IsCreated) toRemove.Dispose();
            if (refugees.IsCreated) refugees.Dispose();
            if (parks.IsCreated) parks.Dispose();
            if (liveParkSet.IsCreated) liveParkSet.Dispose();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                // Sync random state to persist field before serializing
                m_RandomState = unchecked((int)m_Random.state);
                var state = new RefugeeMigrationPersistState(
                    m_LastCheckGameHours,
                    m_MigrationBatchIndex,
                    m_RandomState);
                RefugeeMigrationCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(RefugeeMigrationSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out var version, out var block, nameof(RefugeeMigrationSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                RefugeeMigrationCodec.Read(reader, out var state);
                m_LastCheckGameHours = state.LastCheckGameHours;
                m_MigrationBatchIndex = state.MigrationBatchIndex;
                m_RandomState = state.RandomState;
                // Restore random from persisted state
                m_Random = default;
                m_Random.state = m_RandomState == 0 ? 1u : (uint)m_RandomState;

                Log.Info($"Deserialized v{version}: BatchIndex={m_MigrationBatchIndex}");
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

        private void ResetState()
        {
            m_LastCheckGameHours = 0.0;
            m_MigrationBatchIndex = 0;
            // Reinitialize random with session-unique seed for fresh game.
            // GameTimeSystem may not be activated yet on SetDefaults — fall back
            // to TickCount-only when unavailable (still session-unique).
            uint seed = GameTimeSystem.TryGetTotalGameSeconds(out var seconds)
                ? (uint)seconds ^ (uint)System.Environment.TickCount
                : (uint)System.Environment.TickCount;
            m_Random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            m_RandomState = unchecked((int)m_Random.state);
            Log.Info("State reset");
        }
    }
}
