using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using CivicSurvival.Core.Features.Wellbeing;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;

using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using Unity.Collections;

namespace CivicSurvival.Domains.Blackout.Systems
{
    public partial class BlackoutSystem : IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        [System.NonSerialized] private DistrictSerializationData m_PendingDistrictLoadData;
        [System.NonSerialized] private bool m_HasPendingDistrictLoadData;

        public void ResetToBootDefaults(ResetReason reason)
        {
            ClearTransientState();
            m_HadAnyState = false;
        }

        /// <summary>
        /// Single source for the transient clears shared by the boot-reset and fresh-game
        /// legs (ResetToBootDefaults / ResetState) so the legs cannot drift — the previous
        /// city's frame snapshot, penalty scratch, version cursors and buffered load data
        /// must never survive into the next session. m_HadAnyState is intentionally NOT
        /// cleared here: the boot leg wants false (nothing to reconcile), the fresh leg
        /// forces true (one reconciliation pass over building enable bits).
        /// </summary>
        private void ClearTransientState()
        {
            m_PenaltyDistrictScratch.Clear();
            if (m_LiveDistrictIndicesCache.IsCreated)
                m_LiveDistrictIndicesCache.Clear();
            m_LastDistrictOrderVersion = 0u;
            m_PenaltyUpdateCounter = 0;
            m_CurrentSnapshotReader = DistrictStateSnapshot.Empty;
            ResetBlackoutVersionCursors();
            m_WriteIndex = 0;
            m_PendingFinalClearPass = false;
            m_PendingDistrictLoadData = DistrictSerializationData.Empty;
            m_HasPendingDistrictLoadData = false;
        }

        internal const int MAX_DISTRICT_INDEX = int.MaxValue / Engine.PowerGrid.UI_DISTRICT_ENCODING_MULTIPLIER;

        #region IDefaultSerializable

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var persistState = EmptyPersistState();
            DistrictSerializationData data = DistrictSerializationData.Empty;
            bool hasLiveState = false;

            try
            {
                // HIGH-PG-02 FIX: Cache reference to avoid TOCTOU race condition
                var serialization = StateSerialization;
                if (serialization == null)
                {
                    Log.Warn("[BlackoutSystem] Serialize: State is null (mod unloading?), writing schema-valid empty block");
                }
                else
                {
                    data = serialization.GetSerializationData(CaptureLiveDistrictRefs());
                    persistState = ToPersistState(data);
                    hasLiveState = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[BlackoutSystem] Serialize: state capture failed, writing schema-valid empty block: {ex}");
            }

            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                BlackoutStateCodec.Write(persistState, MAX_DISTRICT_INDEX, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }

            if (hasLiveState)
                Log.Info($"BlackoutSystem.Serialize: {data.DistrictOverrides.Count} district overrides, {data.Blackouts.Count} blackouts, {data.Vips.Count} VIPs, {data.VipBypass.Count} VIPBypass, {data.Penalties.Count} penalties, {data.PreShedStates.Count} preShedStates, {data.Priorities.Count} priorities");
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(BlackoutSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                ClearDistrictStateAfterFailedLoad(ResetReason.VersionMismatch);
                return;
            }

            // Complete any in-flight BlackoutJob and clear its slot handles before loading new state.
            // On a mid-game load Deserialize runs without ResetState first, so a stale job would
            // otherwise race the freshly loaded buffers (mirrors ResetState; CIVIC348).
            Dependency.Complete();
            if (m_BufferSlotHandles != null)
                for (int i = 0; i < m_BufferSlotHandles.Length; i++)
                {
                    m_BufferSlotHandles[i].Complete();
                    m_BufferSlotHandles[i] = default;
                }

            var citySchedule = SchedulePreset.Manual;
            DistrictSerializationData loadData = DistrictSerializationData.Empty;
            bool readSucceeded = false;

            try
            {
                BlackoutStateCodec.Read(reader, out var persistState);

                // Load into district state
                loadData = ToDistrictSerializationData(persistState);
                citySchedule = loadData.CitySchedule;
                readSucceeded = true;
            }
            catch (System.Exception ex)
            {
                Log.Error($"Deserialize failed: {ex}");
                ResetToBootDefaults(ResetReason.DeserializeFailed);
                ClearDistrictStateAfterFailedLoad(ResetReason.DeserializeFailed);
            }
            finally
            {
                SerializationGuard.EndBlock(reader, block);
            }

            if (!readSucceeded)
                return;

            m_PendingDistrictLoadData = loadData;
            m_HasPendingDistrictLoadData = true;
            Log.Info($"BlackoutSystem.Deserialize: buffered citySchedule={citySchedule}, {loadData.DistrictOverrides.Count} district overrides, {loadData.Blackouts.Count} blackouts, {loadData.Vips.Count} VIPs, {loadData.VipBypass.Count} VIPBypass, {loadData.Penalties.Count} penalties, {loadData.PreShedStates.Count} preShedStates, {loadData.Priorities.Count} priorities");
        }

        public void ResetState()
        {
            // Complete any running BlackoutJob before touching buffers.
            Dependency.Complete();
            if (m_BufferSlotHandles != null)
                for (int i = 0; i < m_BufferSlotHandles.Length; i++)
                {
                    m_BufferSlotHandles[i].Complete();
                    m_BufferSlotHandles[i] = default;
                }

            ClearTransientState();
            m_HadAnyState = true; // H14: force one-final-pass to clear stuck BlackoutState on buildings

            // HIGH-PG-02 FIX: Cache reference to avoid TOCTOU race condition
            var writer = StateWriter;
            if (writer == null)
            {
                Log.Warn("[BlackoutSystem] ResetState: State is null (mod unloading?), cleared local state only");
                return;
            }

            writer.ClearAll();
            Log.Info("BlackoutSystem.ResetState: Cleared all settings");
        }

        public void ValidateAfterLoad()
        {
            if (!m_HasPendingDistrictLoadData)
                return;

            var serialization = StateSerialization;
            if (serialization == null)
            {
                Log.Error("[BlackoutSystem] ValidateAfterLoad: StateSerialization unavailable; dropping buffered district state");
                m_PendingDistrictLoadData = DistrictSerializationData.Empty;
                m_HasPendingDistrictLoadData = false;
                return;
            }

            try
            {
                var liveDistrictRefs = CaptureLiveDistrictRefs();
                var loadData = RebindDistrictSerializationData(m_PendingDistrictLoadData, liveDistrictRefs, out var dropped);
                if (dropped > 0)
                    Log.Warn($"BlackoutSystem.ValidateAfterLoad: dropped {dropped} stale/legacy district state rows during DistrictRef rebind");

                serialization.LoadSerializationData(in loadData);
                m_PendingDistrictLoadData = DistrictSerializationData.Empty;
                m_HasPendingDistrictLoadData = false;
                m_PenaltyDistrictScratch.Clear();
                m_PenaltyUpdateCounter = 0;
                m_HadAnyState = true; // Force one post-load pass to reconcile entity enable bits with loaded state.
                ResetBlackoutVersionCursors();

                Log.Info($"BlackoutSystem.ValidateAfterLoad: citySchedule={loadData.CitySchedule}, {loadData.DistrictOverrides.Count} district overrides, {loadData.Blackouts.Count} blackouts, {loadData.Vips.Count} VIPs, {loadData.VipBypass.Count} VIPBypass, {loadData.Penalties.Count} penalties, {loadData.PreShedStates.Count} preShedStates, {loadData.Priorities.Count} priorities");
            }
            catch (System.Exception ex)
            {
                Log.Error($"BlackoutSystem.ValidateAfterLoad: state load failed, clearing previous district state: {ex}");
                m_PendingDistrictLoadData = DistrictSerializationData.Empty;
                m_HasPendingDistrictLoadData = false;
                ClearDistrictStateAfterFailedLoad(ResetReason.DeserializeFailed);
            }
        }

        public void SetDefaults(Context context) => ResetState();

        private void ClearDistrictStateAfterFailedLoad(ResetReason reason)
        {
            var writer = StateWriter;
            if (writer == null)
            {
                Log.Warn($"BlackoutSystem.Deserialize: district-state writer unavailable during {reason}; cleared local state only");
                return;
            }

            writer.ClearAll();
            m_HadAnyState = true; // force building state reconciliation after a failed/absent block load
            m_CurrentSnapshotReader = DistrictStateSnapshot.Empty;
            Log.Info($"BlackoutSystem.Deserialize: cleared district state after {reason}");
        }

        private void ResetBlackoutVersionCursors()
        {
            m_DistrictStateObserverCursor = int.MinValue;
            m_ObservedDistrictStateVersion.Publish(0);
            m_BlackoutStateView.Publish(0);
        }

        #endregion

        #region Codec Mapping Helpers

        private static BlackoutPersistState ToPersistState(in DistrictSerializationData data)
            => new(
                (int)data.CitySchedule,
                ToDistrictScheduleEntries(data.DistrictOverrides),
                ToDistrictCategoryEntries(data.Blackouts),
                ToDistrictRefEntries(data.Vips),
                ToDistrictRefEntries(data.VipBypass),
                ToPenaltyEntries(data.Penalties),
                ToPreShedEntries(data.PreShedStates),
                ToPriorityEntries(data.Priorities));

        private static BlackoutPersistState EmptyPersistState()
            => new(
                0,
                Array.Empty<DistrictScheduleEntry>(),
                Array.Empty<DistrictCategoriesEntry>(),
                Array.Empty<DistrictRefEntry>(),
                Array.Empty<DistrictRefEntry>(),
                Array.Empty<DistrictPenaltyPersistEntry>(),
                Array.Empty<PreShedPersistEntry>(),
                Array.Empty<DistrictPriorityEntry>());

        private static DistrictSerializationData ToDistrictSerializationData(in BlackoutPersistState state)
        {
            var blackouts = new Dictionary<DistrictRef, HashSet<BuildingCategory>>();
            foreach (var entry in state.Blackouts)
                blackouts[entry.District] = ToCategorySet(entry.Categories);

            var districtOverrides = new Dictionary<DistrictRef, DistrictOverride>();
            foreach (var entry in state.DistrictOverrides)
            {
                var schedule = ToSchedulePreset(entry.Schedule);
                districtOverrides[entry.District] = schedule == SchedulePreset.Manual
                    ? DistrictOverride.AlwaysOn
                    : DistrictOverride.Scheduled(schedule);
            }

            var vips = ToDistrictRefSet(state.Vips);
            var vipBypass = ToDistrictRefSet(state.VipBypass);

            var penalties = new Dictionary<DistrictRef, DistrictPenalties>();
            foreach (var entry in state.Penalties)
            {
                penalties[entry.District] = new DistrictPenalties
                {
                    ActiveSources = PenaltySources.Sanitize(entry.ActiveSources),
                    TotalHappinessPenalty = entry.HappinessPenalty,
                    TotalCommercePenalty = entry.CommercePenalty
                };
            }

            var preShedStates = new Dictionary<DistrictRef, PreShedState>();
            foreach (var entry in state.PreShedStates)
            {
                preShedStates[entry.District] = new PreShedState(
                    ToSchedulePreset(entry.Schedule),
                    ToCategorySet(entry.CategoriesOff),
                    entry.WasVip,
                    entry.HadExplicitSchedule,
                    entry.WasVipBypass);
            }

            var priorities = new Dictionary<DistrictRef, int>();
            foreach (var entry in state.Priorities)
                priorities[entry.District] = entry.Priority;

            return new DistrictSerializationData(
                blackouts,
                districtOverrides,
                vips,
                vipBypass,
                penalties,
                preShedStates,
                ToSchedulePreset(state.CitySchedule),
                priorities);
        }

        private static DistrictScheduleEntry[] ToDistrictScheduleEntries(IReadOnlyDictionary<DistrictRef, DistrictOverride> source)
        {
            var entries = new DistrictScheduleEntry[source.Count];
            int index = 0;
            foreach (var kvp in source)
            {
                entries[index] = new DistrictScheduleEntry(kvp.Key, (int)kvp.Value.Schedule);
                index++;
            }
            return entries;
        }

        private static DistrictCategoriesEntry[] ToDistrictCategoryEntries(IReadOnlyDictionary<DistrictRef, HashSet<BuildingCategory>> source)
        {
            var entries = new DistrictCategoriesEntry[source.Count];
            int index = 0;
            foreach (var kvp in source)
            {
                entries[index] = new DistrictCategoriesEntry(kvp.Key, ToCategoryArray(kvp.Value));
                index++;
            }
            return entries;
        }

        private static DistrictPenaltyPersistEntry[] ToPenaltyEntries(IReadOnlyDictionary<DistrictRef, DistrictPenalties> source)
        {
            var entries = new DistrictPenaltyPersistEntry[source.Count];
            int index = 0;
            foreach (var kvp in source)
            {
                entries[index] = new DistrictPenaltyPersistEntry(
                    kvp.Key,
                    (int)kvp.Value.ActiveSources,
                    kvp.Value.TotalHappinessPenalty,
                    kvp.Value.TotalCommercePenalty);
                index++;
            }
            return entries;
        }

        private static PreShedPersistEntry[] ToPreShedEntries(IReadOnlyDictionary<DistrictRef, PreShedState> source)
        {
            var entries = new PreShedPersistEntry[source.Count];
            int index = 0;
            foreach (var kvp in source)
            {
                entries[index] = new PreShedPersistEntry(
                    kvp.Key,
                    (int)kvp.Value.Schedule,
                    kvp.Value.WasVip,
                    kvp.Value.HadExplicitSchedule,
                    ToCategoryArray(kvp.Value.CategoriesOff),
                    kvp.Value.WasVipBypass);
                index++;
            }
            return entries;
        }

        private static DistrictPriorityEntry[] ToPriorityEntries(IReadOnlyDictionary<DistrictRef, int> source)
        {
            var entries = new DistrictPriorityEntry[source.Count];
            int index = 0;
            foreach (var kvp in source)
            {
                entries[index] = new DistrictPriorityEntry(kvp.Key, kvp.Value);
                index++;
            }
            return entries;
        }

        private static DistrictRefEntry[] ToDistrictRefEntries(IReadOnlyCollection<DistrictRef> source)
        {
            var entries = new DistrictRefEntry[source.Count];
            int index = 0;
            foreach (var value in source)
            {
                entries[index] = new DistrictRefEntry(value);
                index++;
            }
            return entries;
        }

        private static HashSet<DistrictRef> ToDistrictRefSet(IReadOnlyCollection<DistrictRefEntry> source)
        {
            var result = new HashSet<DistrictRef>();
            foreach (var entry in source)
                result.Add(entry.District);
            return result;
        }

        private Dictionary<int, DistrictRef> CaptureLiveDistrictRefs()
        {
            var result = new Dictionary<int, DistrictRef>
            {
                [DistrictRef.Null.Index] = DistrictRef.Null
            };

            if (m_LiveDistrictQuery.Equals(default))
                return result;

            using var liveDistricts = m_LiveDistrictQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < liveDistricts.Length; i++)
            {
                var district = DistrictRef.FromEntity(liveDistricts[i]);
                result[district.Index] = district;
            }

            return result;
        }

        internal static DistrictSerializationData RebindDistrictSerializationData(
            in DistrictSerializationData source,
            IReadOnlyDictionary<int, DistrictRef> liveDistrictRefs,
            out int dropped)
        {
            dropped = 0;

            var blackouts = new Dictionary<DistrictRef, HashSet<BuildingCategory>>();
            foreach (var kvp in source.Blackouts)
            {
                if (TryRebindDistrictRef(kvp.Key, liveDistrictRefs, out var district))
                    blackouts[district] = new HashSet<BuildingCategory>(kvp.Value);
                else
                    dropped++;
            }

            var districtOverrides = new Dictionary<DistrictRef, DistrictOverride>();
            foreach (var kvp in source.DistrictOverrides)
            {
                if (TryRebindDistrictRef(kvp.Key, liveDistrictRefs, out var district))
                    districtOverrides[district] = kvp.Value;
                else
                    dropped++;
            }

            var vips = new HashSet<DistrictRef>();
            foreach (var districtRef in source.Vips)
            {
                if (TryRebindDistrictRef(districtRef, liveDistrictRefs, out var district))
                    vips.Add(district);
                else
                    dropped++;
            }

            var vipBypass = new HashSet<DistrictRef>();
            foreach (var districtRef in source.VipBypass)
            {
                if (TryRebindDistrictRef(districtRef, liveDistrictRefs, out var district))
                    vipBypass.Add(district);
                else
                    dropped++;
            }

            var penalties = new Dictionary<DistrictRef, DistrictPenalties>();
            foreach (var kvp in source.Penalties)
            {
                if (TryRebindDistrictRef(kvp.Key, liveDistrictRefs, out var district))
                    penalties[district] = kvp.Value;
                else
                    dropped++;
            }

            var preShedStates = new Dictionary<DistrictRef, PreShedState>();
            foreach (var kvp in source.PreShedStates)
            {
                if (TryRebindDistrictRef(kvp.Key, liveDistrictRefs, out var district))
                {
                    preShedStates[district] = new PreShedState(
                        kvp.Value.Schedule,
                        new HashSet<BuildingCategory>(kvp.Value.CategoriesOff),
                        kvp.Value.WasVip,
                        kvp.Value.HadExplicitSchedule,
                        kvp.Value.WasVipBypass);
                }
                else
                {
                    dropped++;
                }
            }

            var priorities = new Dictionary<DistrictRef, int>();
            foreach (var kvp in source.Priorities)
            {
                if (TryRebindDistrictRef(kvp.Key, liveDistrictRefs, out var district))
                    priorities[district] = kvp.Value;
                else
                    dropped++;
            }

            return new DistrictSerializationData(
                blackouts,
                districtOverrides,
                vips,
                vipBypass,
                penalties,
                preShedStates,
                source.CitySchedule,
                priorities);
        }

        private static bool TryRebindDistrictRef(
            DistrictRef saved,
            IReadOnlyDictionary<int, DistrictRef> liveDistrictRefs,
            out DistrictRef rebound)
        {
            // DistrictKeyCodec already produced live refs via the engine entity remap, so this is
            // now only a liveness belt against a district removed between Deserialize and
            // ValidateAfterLoad. Unzoned (DistrictRef.Null) always survives.
            if (saved.IsNull)
            {
                rebound = DistrictRef.Null;
                return true;
            }

            if (liveDistrictRefs != null
                && liveDistrictRefs.TryGetValue(saved.Index, out var live)
                && live == saved)
            {
                rebound = live;
                return true;
            }

            rebound = default;
            return false;
        }

        private static int[] ToCategoryArray(IReadOnlyCollection<BuildingCategory> source)
        {
            var entries = new int[source.Count];
            int index = 0;
            foreach (var category in source)
            {
                entries[index] = (int)category;
                index++;
            }
            return entries;
        }

        private static HashSet<BuildingCategory> ToCategorySet(IReadOnlyList<int> source)
        {
            var result = new HashSet<BuildingCategory>();
            for (int i = 0; i < source.Count; i++)
            {
                var category = ToBuildingCategory(source[i]);
                if (category != BuildingCategory.None)
                    result.Add(category);
            }
            return result;
        }

        private static SchedulePreset ToSchedulePreset(int value)
            => value switch
            {
                1 => SchedulePreset.MildRestriction,
                2 => SchedulePreset.Balanced,
                3 => SchedulePreset.SevereCrisis,
                4 => SchedulePreset.DayShift,
                _ => SchedulePreset.Manual
            };

        private static BuildingCategory ToBuildingCategory(int value)
            => value switch
            {
                1 => BuildingCategory.Residential,
                2 => BuildingCategory.Commercial,
                3 => BuildingCategory.Industrial,
                4 => BuildingCategory.Office,
                5 => BuildingCategory.Services,
                _ => BuildingCategory.None
            };

        #endregion

    }
}
