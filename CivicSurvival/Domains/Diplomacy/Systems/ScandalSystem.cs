using System;
using Colossal.Serialization.Entities;
using Unity.Entities;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Diplomacy;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using Game.Simulation;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Types;

namespace CivicSurvival.Domains.Diplomacy.Systems
{
    /// <summary>
    /// International scandal system - monitors Heat and triggers reputation penalties.
    /// High Heat (from corruption) attracts international attention → scandals → trust penalty.
    ///
    /// ECS-pure: reads Heat from CountermeasuresState singleton.
    /// Publishes state via ScandalStateSingleton for consumers (DonorConferenceSystem, UI).
    /// </summary>
    [ActIndependent]
    public partial class ScandalSystem : CivicSystemBase, IDefaultSerializable, IResettable, IBootDefaultsReset, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ScandalSystem");
        private static float ScandalPenaltyCap => BalanceConfig.Current.Countermeasures.ScandalPenaltyCap;

        // State
        private float m_ScandalPenalty;
        private int m_LastScandalDay = -1;
        private bool m_ScandalBaselineSeeded;
        private DayChangedDedup m_DayDedup = default;
        private SerializableRandom m_Random;
        [NonSerialized] private bool m_SingletonWritePending;

        // ECS singleton — liveness-validated handle (Inv 2; CIVIC427)
        [NonSerialized] private CivicSingletonHandle<ScandalStateSingleton> m_Singleton;

        // FIX H58: EntityQuery for CountermeasuresCoreFsm — avoids SystemAPI in event handler (hidden sync point)
        private EntityQuery m_CountermeasuresQuery;
        private EntityQuery m_CurrentActQuery;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Session-unique seed (GameTimeSystem.Instance is null in OnCreate — use TickCount)
            int seed = Environment.TickCount + 0x5C4D;  // SC = Scandal
            m_Random = new SerializableRandom(seed);

            // Create ECS singleton for direct ECS access
            m_Singleton = CreateSingletonHandle<ScandalStateSingleton>();
            EnsureSingleton(ref m_Singleton, ScandalStateSingleton.Default);

            // FIX H58: Query for event handler (no CompleteDependencyBeforeRO)
            m_CountermeasuresQuery = GetEntityQuery(ComponentType.ReadOnly<CountermeasuresCoreFsm>());
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());

            SubscribeRequired<DayChangedEvent>(OnDayChanged, DayEventPriority.StateChange);

            Log.Info($"{nameof(ScandalSystem)} created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // CIVIC414: lifecycle hook so the singleton is restored after load
            // before any OnUpdateImpl runs (the OnCreate ensure does not survive
            // a same-session load barrier on its own).
            EnsureSingleton(ref m_Singleton, ScandalStateSingleton.Default);
            m_SingletonWritePending = true;
        }

        protected override void OnUpdateImpl()
        {
            if (m_SingletonWritePending)
                UpdateSingleton();
        }

        public void ValidateAfterLoad()
        {
            EnsureSingleton(ref m_Singleton, ScandalStateSingleton.Default);
            m_SingletonWritePending = true;
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<DayChangedEvent>(OnDayChanged);

            // Clean up ECS singleton
            if (m_Singleton.Entity != Entity.Null && EntityManager.Exists(m_Singleton.Entity))
            {
                EntityManager.DestroyEntity(m_Singleton.Entity);
            }

            Log.Info($"{nameof(ScandalSystem)} destroyed");
            base.OnDestroy();
        }

        private void OnDayChanged(DayChangedEvent evt)
        {
            if (!IsCrisisOrLater()) return;
            if (m_DayDedup.AlreadyProcessed(evt.DayNumber)) return;

            using var _ = PerformanceProfiler.Measure("Scandal.OnDayChanged");
            // Decay scandal penalty over time
            if (m_ScandalPenalty > 0)
            {
                float decay = BalanceConfig.Current.Countermeasures.ScandalPenaltyDecayPerDay;
                m_ScandalPenalty = Math.Max(0, m_ScandalPenalty - decay);
            }

            // Check for new scandal
            CheckForScandal(evt.DayNumber);

            // Update ECS singleton
            UpdateSingleton();
        }

        private bool IsCrisisOrLater()
        {
            return m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var act)
                && act.CurrentAct >= Act.Crisis;
        }

        private void UpdateSingleton()
        {
            // CIVIC051: EntityManager is required here — called from OnDayChanged event handler where SystemAPI is unavailable.
            // Manually managed singleton entity, no parallel jobs on ScandalStateSingleton archetype.
#pragma warning disable CIVIC051
            if (m_Singleton.Entity == Entity.Null
                || !EntityManager.HasComponent<ScandalStateSingleton>(m_Singleton.Entity))
            {
                m_SingletonWritePending = true;
                return;
            }

            EntityManager.SetComponentData(m_Singleton.Entity, new ScandalStateSingleton
            {
                ScandalPenalty = m_ScandalPenalty,
                LastScandalDay = Math.Max(0, m_LastScandalDay)
            });
            m_SingletonWritePending = false;
#pragma warning restore CIVIC051
        }

        /// <summary>
        /// Check if international scandal triggers based on Heat level.
        /// Same Heat value used for internal investigation AND international scrutiny.
        /// ECS-pure: reads from CountermeasuresCoreFsm singleton.
        /// </summary>
        private void CheckForScandal(int currentDay)
        {
            // FIX H58: Use EntityQuery instead of SystemAPI — called from OnDayChanged event handler,
            // where SystemAPI.TryGetSingleton triggers CompleteDependencyBeforeRO (hidden sync point).
            if (!m_CountermeasuresQuery.TryGetSingleton<CountermeasuresCoreFsm>(out var core))
                return;

            float heat = core.Heat;
            var cm = BalanceConfig.Current.Countermeasures;

            // No scandal below threshold
            if (heat < cm.ScandalHeatThreshold)
                return;

            // First high-Heat day only seeds the initial cooldown once per game.
            // Persisting this separately prevents save/load from re-seeding it.
            if (!m_ScandalBaselineSeeded)
            {
                m_ScandalBaselineSeeded = true;
                m_LastScandalDay = currentDay;
                return;
            }
            int daysSinceLastScandal = currentDay - m_LastScandalDay;
            if (daysSinceLastScandal < cm.ScandalCooldownDays)
                return;

            // Scandal chance = normalized (Heat - threshold) / (100 - threshold), clamped to [0,1]
            float scandalRange = Math.Max(1f, 100f - cm.ScandalHeatThreshold);
            float scandalChance = Math.Min(1f, (heat - cm.ScandalHeatThreshold) / scandalRange);
            float roll = m_Random.NextFloat();

            if (roll < scandalChance)
            {
                TriggerScandal(currentDay, heat);
            }
        }

        /// <summary>
        /// Trigger international scandal - drops Trust.
        /// </summary>
        private void TriggerScandal(int currentDay, float heat)
        {
            m_ScandalBaselineSeeded = true;
            m_LastScandalDay = currentDay;
            m_ScandalPenalty += BalanceConfig.Current.Countermeasures.ScandalTrustPenalty;

            // Cap penalty at maximum
            m_ScandalPenalty = Math.Min(m_ScandalPenalty, ScandalPenaltyCap);

            Log.Warn($"[ScandalSystem] INTERNATIONAL SCANDAL! Heat={heat:F1}%, Trust penalty now {m_ScandalPenalty:F1}");

            // Sync ECS singleton before publishing event so handlers see fresh penalty
            UpdateSingleton();

            // Publish event for UI notification and DonorConferenceSystem
            EventBus?.SafePublish(new DonorEvent(DonorEventType.Scandal, Penalty: m_ScandalPenalty), nameof(ScandalSystem));
        }

        // ============================================================================
        // IResettable
        // ============================================================================

        public void ResetState()
        {
            m_ScandalPenalty = 0f;
            m_LastScandalDay = -1;
            m_ScandalBaselineSeeded = false;
            m_DayDedup.Reset();
            m_SingletonWritePending = true;
            m_Random = new SerializableRandom(World.SequenceNumber.GetHashCode() ^ 0x5C4D);
            if (m_Singleton.Entity != Entity.Null && EntityManager.Exists(m_Singleton.Entity))
                UpdateSingleton();
        }

        public void ResetToBootDefaults(ResetReason reason)
        {
            m_ScandalPenalty = 0f;
            m_LastScandalDay = -1;
            m_ScandalBaselineSeeded = false;
            m_DayDedup.Reset();
            m_SingletonWritePending = true;
            m_Random = new SerializableRandom(Environment.TickCount ^ 0x5C4D);
            m_Singleton.Invalidate();
        }

        // ============================================================================
        // Serialization
        // ============================================================================

        public void SetDefaults(Context context)
        {
            ResetState();
            // UpdateSingleton() already called by ResetState() when entity exists
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var state = new ScandalPersistState(
                    m_ScandalPenalty,
                    m_LastScandalDay,
                    m_DayDedup.LastProcessedDay,
                    m_Random.State,
                    m_ScandalBaselineSeeded);
                ScandalCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(ScandalSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(ScandalSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
#pragma warning disable CIVIC144 // Scandal codec returns a bounded scalar snapshot, not a collection size
                ScandalCodec.Read(reader, out var snapshot);
#pragma warning restore CIVIC144
                m_ScandalPenalty = snapshot.ScandalPenalty;
                m_LastScandalDay = snapshot.LastScandalDay;
                m_ScandalBaselineSeeded = snapshot.BaselineSeeded;
                m_DayDedup = Core.Utils.DayChangedDedup.FromSave(snapshot.LastProcessedDay);
                if (snapshot.RandomState != 0) m_Random = new Core.Utils.SerializableRandom(snapshot.RandomState);

                if (m_ScandalPenalty > ScandalPenaltyCap)
                {
                    Log.Warn($"Deserialize clamped scandal penalty to current cap ({m_ScandalPenalty:F1} -> {ScandalPenaltyCap:F1})");
                    m_ScandalPenalty = ScandalPenaltyCap;
                }

                // Ensure singleton entity exists (may have been destroyed by world cleanup)
                // Deserialize runs outside a safe SystemAPI context; don't
                // EnsureSingleton here. Invalidate so the OnUpdateImpl lazy
                // resolve re-binds query-first next frame; UpdateSingleton below
                // defers the write via m_SingletonWritePending if not yet bound.
                m_Singleton.Invalidate();
                UpdateSingleton();

                Log.Info($"[{nameof(ScandalSystem)}] Deserialized: ScandalPenalty={m_ScandalPenalty:F1}, LastScandalDay={m_LastScandalDay}");
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
    }
}
