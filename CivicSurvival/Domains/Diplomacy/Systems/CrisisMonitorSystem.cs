using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Diplomacy;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Population;
using CivicSurvival.Core.Features.Population;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using Game.Simulation;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Types;

namespace CivicSurvival.Domains.Diplomacy.Systems
{
    /// <summary>
    /// Monitors city crisis level (% population in blackout).
    /// Updates once per day for performance.
    ///
    /// Publishes state via CrisisStateSingleton for consumers (DonorConferenceSystem, UI).
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(CrisisStateSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
#pragma warning disable CIVIC223 // PostLoadValidationSystem invokes ICivicSingletonOwner.OnLoadRestore; Deserialize only buffers payload.
    public partial class CrisisMonitorSystem : CivicSystemBase, IDefaultSerializable, IResettable,
        IPostLoadValidation, ICivicSingletonOwner<CrisisStateSingleton>, IBootDefaultsReset
#pragma warning restore CIVIC223
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CrisisMonitorSystem");
        // Cached metrics (updated daily)
        private float m_CrisisLevel;
        private int m_TotalPopulation;
        private int m_AffectedPopulation;
        private int m_LastUpdateDay;
        [NonSerialized] private IResidentHouseholdView m_ResidentHouseholdView = null!;
        [NonSerialized] private IResidentPopulationReader m_ResidentPopulationReader = null!;
        [NonSerialized] private int m_ResidentHouseholdObserverVersion;
        [NonSerialized] private int m_ResidentPopulationObserverVersion;

        // ECS queries
        private EntityQuery m_BlackoutResidentialQuery;
        private EntityQuery m_CurrentActQuery;

        // Cached lookups (Axiom 8: cache in OnCreate, Update before use in OnDayChanged handler)
#pragma warning disable CIVIC185 // Event-driven: Updated in CountResidentialPopulation() called from OnDayChanged, not OnUpdateImpl
        private BufferLookup<Renter> m_RenterLookup;
#pragma warning restore CIVIC185

        // ECS singleton — liveness-validated handle (Inv 2; CIVIC427)
        [NonSerialized] private CivicSingletonHandle<CrisisStateSingleton> m_Singleton;
        [NonSerialized] private bool m_RecalculateAfterLoad;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Cache queries for population counting
            // BUG-DOM-036 FIX: Exclude destroyed buildings from population count
            m_BlackoutResidentialQuery = GetEntityQuery(
                ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<BlackoutState>(),
                ComponentType.Exclude<Destroyed>(),
                ComponentType.Exclude<Deleted>()
            );
            m_CurrentActQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentActSingleton>());

            // Cache lookups (Axiom 8)
            m_RenterLookup = GetBufferLookup<Renter>(true);

            m_Singleton = CreateSingletonHandle<CrisisStateSingleton>();
            EnsureSingletonEntity(EntityManager);
            UpdateSingleton();

            SubscribeRequired<DayChangedEvent>(OnDayChanged, DayEventPriority.StateChange);

            Log.Info($"{nameof(CrisisMonitorSystem)} created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_ResidentHouseholdView ??= ServiceRegistry.Instance.Require<IResidentHouseholdView>();
            m_ResidentPopulationReader ??= ServiceRegistry.Instance.Require<IResidentPopulationReader>();
        }

        protected override void OnUpdateImpl()
        {
            // No per-frame logic - using DayChangedEvent
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<DayChangedEvent>(OnDayChanged);

            // Clean up ECS singleton
            if (m_Singleton.Entity != Entity.Null && EntityManager.Exists(m_Singleton.Entity))
            {
                EntityManager.DestroyEntity(m_Singleton.Entity);
            }

            Log.Info($"{nameof(CrisisMonitorSystem)} destroyed");
            base.OnDestroy();
        }

        private void OnDayChanged(DayChangedEvent evt)
        {
            if (!IsCrisisOrLater()) return;

            using var _ = PerformanceProfiler.Measure("CrisisMonitor.OnDayChanged");
            if (evt.DayNumber <= m_LastUpdateDay)
                return;

            m_LastUpdateDay = evt.DayNumber;
            UpdateCrisisMetrics();
        }

        private bool IsCrisisOrLater()
        {
            return m_CurrentActQuery.TryGetSingleton<CurrentActSingleton>(out var act)
                && act.CurrentAct >= Act.Crisis;
        }

        private void UpdateCrisisMetrics()
        {
            // OnStartRunning is canonical; this re-resolves when
            // ValidateAfterLoad runs before OnStartRunning in PLVS sequencing.
            m_ResidentHouseholdView ??= ServiceRegistry.Instance.Require<IResidentHouseholdView>();
            m_ResidentPopulationReader ??= ServiceRegistry.Instance.Require<IResidentPopulationReader>();

            // Readiness gate (A7). Crisis is a denominator over the population scalar
            // (AliveResidentCitizens); it does not read the household selection, so
            // ScalarReady is sufficient and gating on SelectionReady would add needless
            // latency. While not scalar-ready, skip this tick without touching the
            // persisted m_CrisisLevel: the next DayChanged retries with a valid scalar,
            // and a restored crisis level survives until then (does not collapse to 0%).
            if (!m_ResidentPopulationReader.IsScalarReady)
            {
                LogCrisisDecision(wrote: false);
                return;
            }

            var residentSnapshot = m_ResidentHouseholdView.Observe(ref m_ResidentHouseholdObserverVersion).Value;
            var populationSnapshot = m_ResidentPopulationReader.Observe(ref m_ResidentPopulationObserverVersion).Value;
            m_AffectedPopulation = CountAffectedResidentialPopulation(m_BlackoutResidentialQuery, residentSnapshot);
            m_TotalPopulation = populationSnapshot.AliveResidentCitizens;

            if (m_TotalPopulation == 0)
            {
                m_CrisisLevel = 0f;
            }
            else
            {
                m_CrisisLevel = Math.Min(100f, (m_AffectedPopulation * 100f) / m_TotalPopulation);
            }

            // Update ECS singleton
            UpdateSingleton();

            LogCrisisDecision(wrote: true);
            if (Log.IsDebugEnabled) Log.Debug($"[CrisisMonitor] Day {m_LastUpdateDay}: Crisis {m_CrisisLevel:F1}% ({m_AffectedPopulation}/{m_TotalPopulation})");
        }

        // [POP-READY] crisis-decision log (Verification table): proves CrisisMonitor never
        // writes a false 0% on pop=0 after load — a skipped (not-ready) tick logs wrote=false
        // and leaves the restored crisis level untouched.
        private void LogCrisisDecision(bool wrote)
        {
            if (!Log.IsDebugEnabled)
                return;

            ResidentPopulationReadiness readiness = m_ResidentPopulationReader?.Readiness ?? ResidentPopulationReadiness.NotReady;
            Log.Info($"[POP-READY] Crisis day={m_LastUpdateDay} readiness={readiness} pop={m_TotalPopulation} crisis={m_CrisisLevel:F1}% wrote={wrote}");
        }

        private void UpdateSingleton()
        {
            EnsureSingletonEntity(EntityManager);

            EntityManager.SetComponentData(m_Singleton.Entity, new CrisisStateSingleton
            {
                CrisisLevel = m_CrisisLevel
            });
        }

        private void EnsureSingletonEntity(EntityManager entityManager)
        {
            // EntityManager-based EnsureSingleton: called from the OnDayChanged
            // event handler and ICivicSingletonOwner.OnLoadRestore where this
            // system's SystemAPI context is not valid (CIVIC292/185). Canonical
            // Inv-2 contract (liveness → query-first → dedup → create-if-absent)
            // is centralized in CivicSystemBase.
            EnsureSingleton(ref m_Singleton, entityManager, CrisisStateSingleton.Default);
        }

        /// <summary>
        /// Count resident population in blacked-out residential buildings.
        /// </summary>
        private int CountAffectedResidentialPopulation(EntityQuery query, ResidentHouseholdSnapshot residentSnapshot)
        {
            int population = 0;

            using var entities = query.ToEntityArray(Allocator.Temp);
            m_RenterLookup.Update(this);

            var liveResidentsByHousehold = BuildLiveResidentsByHousehold(residentSnapshot, Allocator.Temp);
            try
            {
                foreach (var entity in entities)
                {
                    if (!EntityManager.IsComponentEnabled<BlackoutState>(entity))
                        continue;

                    if (!m_RenterLookup.TryGetBuffer(entity, out var renters))
                        continue;

                    foreach (var renter in renters)
                    {
                        long householdKey = MakeHouseholdKey(renter.m_Renter.Index, renter.m_Renter.Version);
                        if (liveResidentsByHousehold.TryGetValue(householdKey, out int liveResidents))
                            population += liveResidents;
                    }
                }
            }
            finally
            {
                if (liveResidentsByHousehold.IsCreated)
                    liveResidentsByHousehold.Dispose();
            }

            return population;
        }

        private static NativeParallelHashMap<long, int> BuildLiveResidentsByHousehold(
            ResidentHouseholdSnapshot snapshot,
            Allocator allocator)
        {
            int capacity = Math.Max(1, snapshot.EligibleHouseholds.Length);
            var result = new NativeParallelHashMap<long, int>(capacity, allocator);

            int count = Math.Min(snapshot.EligibleHouseholds.Length, snapshot.LiveCitizensPerHousehold.Length);
            for (int i = 0; i < count; i++)
            {
                Entity household = snapshot.EligibleHouseholds[i];
                result.TryAdd(MakeHouseholdKey(household.Index, household.Version), snapshot.LiveCitizensPerHousehold[i]);
            }

            return result;
        }

        private static long MakeHouseholdKey(int index, int version)
            => ((long)index << 32) ^ (uint)version;

        // ============================================================================
        // IResettable
        // ============================================================================

        public void ResetState()
        {
            m_CrisisLevel = 0f;
            m_TotalPopulation = 0;
            m_AffectedPopulation = 0;
            m_LastUpdateDay = 0;
            m_ResidentHouseholdObserverVersion = 0;
            m_ResidentPopulationObserverVersion = 0;
            m_RecalculateAfterLoad = false;
            m_Singleton.Invalidate();
        }

        public void ResetToBootDefaults(ResetReason reason) => ResetState();

        public void OnLoadRestore(EntityManager entityManager)
        {
            EnsureSingletonEntity(entityManager);
            entityManager.SetComponentData(m_Singleton.Entity, new CrisisStateSingleton
            {
                CrisisLevel = m_CrisisLevel
            });
        }

        public int HydrationOrder => HydrationPriority.DEFAULT;

        public void ValidateAfterLoad()
        {
            if (!m_RecalculateAfterLoad)
            {
                UpdateSingleton();
                return;
            }

            m_RecalculateAfterLoad = false;
            if (!IsCrisisOrLater())
            {
                UpdateSingleton();
                return;
            }

            int savedLastUpdateDay = m_LastUpdateDay;
            UpdateCrisisMetrics();
            m_LastUpdateDay = Math.Max(0, savedLastUpdateDay - 1);
        }

        // ============================================================================
        // Serialization
        // ============================================================================

        public void SetDefaults(Context context)
        {
            ResetState();
            UpdateSingleton();
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var state = new CrisisMonitorPersistState(
                    m_CrisisLevel,
                    m_TotalPopulation,
                    m_AffectedPopulation,
                    m_LastUpdateDay);
                CrisisMonitorCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
            SerializationGuard.LogSerialized(nameof(CrisisMonitorSystem), SaveVersions.GLOBAL);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, nameof(CrisisMonitorSystem)))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                CrisisMonitorCodec.Read(reader, out var state);
                m_CrisisLevel = state.CrisisLevel;
                m_TotalPopulation = state.TotalPopulation;
                m_AffectedPopulation = state.AffectedPopulation;
                m_LastUpdateDay = state.LastUpdateDay;

                // PLVS calls ValidateAfterLoad after RestoreSingletonOwners; defer
                // the act decision until then. Sibling Deserialize order is undefined.
                m_RecalculateAfterLoad = true;
                UpdateSingleton();

                Log.Info($"[{nameof(CrisisMonitorSystem)}] Deserialized: Crisis={m_CrisisLevel:F1}%, Pop={m_AffectedPopulation}/{m_TotalPopulation}");
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
