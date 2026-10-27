using System;
using System.Collections.Generic;
using CivicSurvival.Core.Features.Wellbeing;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Components.Domain.Mobilization;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Debug;
using CivicSurvival.Core.Interfaces.Domain.Corruption;
using CivicSurvival.Core.Interfaces.Domain.Mobilization;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Systems.Scheduling;

using CivicSurvival.Core.Services;
namespace CivicSurvival.Domains.Mobilization.Systems
{
    /// <summary>
    /// Manpower management system.
    /// Optimized with Frame Throttling for population queries and heavy logic.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(MobilizationStateSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    public partial class MobilizationSystem : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation, ICivicSingletonOwner<MobilizationStateSingleton>, IMobilizationManpowerReader
#if DEBUG
        , IMobilizationDebugMutator
#endif
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("MobilizationSystem");

        private DistrictPenaltySystem? m_PenaltySystem;
        [NonSerialized]
        private CivicSingletonHandle<MobilizationStateSingleton> m_Singleton;
        private EntityQuery m_SingletonQuery;

        [Persist] private int m_UsedManpower;
        [Persist] private int m_Casualties;
        [Persist] private bool m_ConscriptionActive;
        [Persist(Default = -1)] private int m_LastWarDay = -1;

        // Draft-evasion level (0-1, "ухилянти"): integrated from the Core draft-fear lever the
        // Cognitive launcher republishes from live landed Propaganda raids. Persisted — the
        // sociological damage outlives the raid window and only decays with time.
        [Persist] private float m_DodgerLevel;
        // Integration anchor (TotalGameHours). Transient: re-anchors on the first tick after
        // load so a load never produces a catch-up jump.
        [NonSerialized] private float m_LastDodgerHour = -1f;
        // Rising/falling edge latch for the greppable log marker (Axiom 1).
        [NonSerialized] private bool m_DodgerActive;
        // Level at the last breakdown-dirtying publish. The dirty threshold MUST compare against
        // this, never against the per-frame delta: at frame cadence a single step of the
        // integration (~6e-5 at typical pressure) sits below any sane epsilon, and gating the
        // ASSIGNMENT on it silently freezes the level at zero forever (observed live:
        // [PsyOpsFear] ACTIVE for a full propaganda window, [Dodgers] never rose).
        [NonSerialized] private float m_LastPublishedDodgerLevel;
        /// <summary>Accumulated drift from the last published level that re-dirties the breakdown.</summary>
        private const float DODGER_LEVEL_EPSILON = 0.0001f;

        private const int HASH_INIT = 17;
        private const int HASH_MULTIPLIER = 31;

        /// <summary>
        /// Stable allocation identity. Type is metadata on the value, not part of
        /// identity: releases can arrive with AATypeHash=0 after the AA was already
        /// destroyed, but they still refer to the same entity allocation.
        /// </summary>
        public readonly struct AllocKey : System.IEquatable<AllocKey>
        {
            public readonly int EntityIndex;
            public readonly int EntityVersion;

            public AllocKey(int entityIndex, int entityVersion)
            {
                EntityIndex = entityIndex;
                EntityVersion = entityVersion;
            }

            public bool Equals(AllocKey other) =>
                EntityIndex == other.EntityIndex && EntityVersion == other.EntityVersion;

            public override bool Equals(object obj) => obj is AllocKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = HASH_INIT;
                    hash = hash * HASH_MULTIPLIER + EntityIndex;
                    hash = hash * HASH_MULTIPLIER + EntityVersion;
                    return hash;
                }
            }

            public override string ToString() => $"Alloc:{EntityIndex}v{EntityVersion}";

            public static bool operator ==(AllocKey left, AllocKey right) => left.Equals(right);
            public static bool operator !=(AllocKey left, AllocKey right) => !left.Equals(right);
        }

        private struct AllocEntry
        {
            public int TypeHash;
            public int Count;

            public AllocEntry(int typeHash, int count)
            {
                TypeHash = typeHash;
                Count = count;
            }
        }

        private Dictionary<AllocKey, AllocEntry> m_Allocations = new();

        private readonly List<KeyValuePair<AllocKey, AllocEntry>> m_ForceReleaseSortedScratch = new();

        private ManpowerBreakdown m_CachedBreakdown;
        private int m_CachedPopulation;
        [RuntimeInputDirtyCursor("Frame throttle for expensive citizen-count refresh; not a snapshot producer/consumer version cursor.")]
        [NonSerialized] private uint m_LastUpdateFrame;
#pragma warning disable CIVIC324 // Debug-only morale override; intentionally session-local and cleared by ResetBootDefaultsFields.
        [NonSerialized] private bool m_DebugMoraleOverrideActive;
        [NonSerialized] private float m_DebugMoraleOverride;
#pragma warning restore CIVIC324
        [NonSerialized] private bool m_LoggedMissingPenaltySystem;
        private bool m_IsDirty;

        private const int POPULATION_REFRESH_INTERVAL = 128;
        // Use config value — ManpowerLogic.IsCritical reads BalanceConfig.Current.Mobilization.CriticalThreshold

        [Persist(Unclamped = true)] private double m_LastCriticalEventHour;

        [Persist(Unclamped = true)] private float m_CallToArmsCooldownEndHour;

        [Persist(Unclamped = true)] private float m_ConscriptionCooldownEndHour;

        private EntityQuery m_ReleaseRequestQuery;
        private EntityQuery m_CasualtyRequestQuery;
        private EntityQuery m_CorruptionQuery;
        private EntityQuery m_DraftExemptionQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
        private ComponentLookup<MobilizationStateSingleton> m_SingletonLookup;
        private EntityQuery m_AAInstallationQuery;
        private IShadowReputationService m_ReputationService = null!;

        // ============================================================
        // IMobilizationService Implementation
        // ============================================================

        public bool IsConscriptionActive => m_ConscriptionActive;

        public int AvailableManpower
        {
            get
            {
                UpdateBreakdown(false);
                return m_CachedBreakdown.AvailableManpower;
            }
        }

        private static string AllocationId(AllocKey key, int typeHash) =>
            $"Alloc:{typeHash}@{key.EntityIndex}v{key.EntityVersion}";

        public bool CanRecruit(int amount)
        {
            UpdateBreakdown(false);
            return m_CachedBreakdown.AvailableManpower >= amount;
        }

#pragma warning disable CIVIC231 // Public API — callers are responsible for act check
        public bool TryRecruit(int amount, int typeHash, int idx, int ver)
#pragma warning restore CIVIC231
        {
            if (!CanRecruit(amount)) return false;

            // XSTEP: crew commit-on-place routes through Core CrewMath (shared with AA release / future
            // server recompute); inline `used + amount` here would re-derive the transition off-Core.
            m_UsedManpower = CrewMath.Commit(m_UsedManpower, amount);

            var key = new AllocKey(idx, ver);
            m_Allocations.TryGetValue(key, out var entry);
            if (entry.TypeHash == 0 && typeHash != 0)
                entry.TypeHash = typeHash;
            entry.Count += amount;
            m_Allocations[key] = entry;

            m_IsDirty = true;

            int available = CrewMath.Available(m_CachedBreakdown.TotalManpower, m_UsedManpower);
            string allocationId = AllocationId(key, entry.TypeHash);
            EventBus?.SafePublish(new ManpowerRecruitedEvent(amount, allocationId, available));
            Log.Info($"Recruited {amount} for '{allocationId}' - {available} available");

            CheckCritical();
            return true;
        }

        public void Release(int amount, int typeHash, int idx, int ver)
        {
            amount = System.Math.Max(0, amount);
            var key = new AllocKey(idx, ver);
            int eventTypeHash = typeHash;
            int released = 0;
            if (m_Allocations.TryGetValue(key, out var entry))
            {
                if (entry.TypeHash != 0)
                    eventTypeHash = entry.TypeHash;

                released = System.Math.Min(amount, System.Math.Max(0, entry.Count));
                int newAlloc = entry.Count - released;
                if (newAlloc <= 0) m_Allocations.Remove(key);
                else
                {
                    entry.Count = newAlloc;
                    m_Allocations[key] = entry;
                }
            }

            // XSTEP: crew release routes through Core CrewMath (same accumulator AA release decrements);
            // inline `max(0, used - released)` here would fork the committed-pool transition off-Core.
            m_UsedManpower = CrewMath.Release(m_UsedManpower, released);
            m_IsDirty = true;
            UpdateBreakdown(true);
            int availableAfter = CrewMath.Available(m_CachedBreakdown.TotalManpower, m_UsedManpower);
            EventBus?.SafePublish(new ManpowerReleasedEvent(released, AllocationId(key, eventTypeHash), availableAfter));
        }

        public bool IsCallToArmsOnCooldown
        {
            get
            {
                var tp = GameTimeSystem.Instance;
                return tp != null && tp.Current.TotalGameHours < m_CallToArmsCooldownEndHour;
            }
        }

        public bool IsConscriptionReactivationOnCooldown
        {
            get
            {
                var tp = GameTimeSystem.Instance;
                return tp != null && tp.Current.TotalGameHours < m_ConscriptionCooldownEndHour;
            }
        }

        // ============================================================
        // Lifecycle & Update
        // ============================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ReleaseRequestQuery = GetEntityQuery(ComponentType.ReadWrite<ManpowerReleaseRequest>());
            m_CasualtyRequestQuery = GetEntityQuery(ComponentType.ReadWrite<CasualtyReportRequest>());
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_AAInstallationQuery = GetEntityQuery(ComponentType.ReadOnly<AirDefenseInstallation>());
            m_CorruptionQuery = GetEntityQuery(ComponentType.ReadOnly<CorruptionSingleton>());
            m_DraftExemptionQuery = GetEntityQuery(ComponentType.ReadOnly<DraftExemptionSingleton>());
            m_SingletonLookup = GetComponentLookup<MobilizationStateSingleton>(false);
            m_SingletonQuery = GetEntityQuery(ComponentType.ReadWrite<MobilizationStateSingleton>());
            m_Singleton = CreateSingletonHandle<MobilizationStateSingleton>(m_SingletonQuery);

            SubscribeRequired<WarDayChangedEvent>(OnWarDayChanged);

            RestoreMobilizationSingleton();

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IMobilizationManpowerReader>(this);
            }

#if DEBUG
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IMobilizationDebugMutator>(this);
            }
#endif
            m_IsDirty = true;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_ReputationService = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowReputationService.Instance);
            m_PenaltySystem ??= FeatureRegistry.Instance.Query<DistrictPenaltySystem>();
            if (m_PenaltySystem == null && !m_LoggedMissingPenaltySystem)
            {
                Log.Warn("DistrictPenaltySystem unavailable; mobilization will use neutral social penalties");
                m_LoggedMissingPenaltySystem = true;
            }
            RestoreMobilizationSingleton();
            UpdateBreakdown(true, refreshPopulation: true);
        }

        protected override void OnUpdateImpl()
        {
            IntegrateDodgerLevel();

            // Player mobilization commands are drained by MobilizationRequestIntakeSystem
            // (ModificationEnd, pause-safe per Axiom 14) — not here.
            ProcessReleaseRequests();
            ProcessCasualtyRequests();

            UpdateBreakdown(false);
        }

        /// <summary>
        /// Integrate the draft-evasion level from the Core draft-fear lever (landed enemy
        /// Propaganda pressure on the draft strata, hero-countered at the source). Gain scales
        /// with the pressure and — while conscription is active — with the fear multiplier;
        /// decay is proportional to the ACCUMULATED level (exponential relaxation), so any
        /// pressure produces an effect proportional to its strength and the level settles at
        /// the equilibrium <c>gain / DodgerDecayPerHour</c> (clamped to 1). A constant decay
        /// would instead create a dead zone where weak early-wave propaganda (pressure below
        /// decay/accum ≈ 0.33) could never lift the level off zero — observed live as
        /// <c>[PsyOpsFear] ACTIVE</c> with no <c>[Dodgers]</c> ever following.
        /// </summary>
        private void IntegrateDodgerLevel()
        {
            var tp = GameTimeSystem.Instance;
            if (tp == null)
                return;

            float now = tp.Current.TotalGameHours;
            if (m_LastDodgerHour < 0f)
            {
                m_LastDodgerHour = now;
                return;
            }

            // Negative delta (load anomaly) re-anchors; oversized delta is clamped so a stall
            // never integrates a burst of fear in one step.
            float dh = now - m_LastDodgerHour;
            m_LastDodgerHour = now;
            if (dh <= 0f)
                return;
            dh = System.Math.Min(dh, 1f);

            var mobCfg = BalanceConfig.Current.Mobilization;
            float pressure = DraftFearAdapter.GetPropagandaFear(World);
            float gain = pressure * mobCfg.DodgerAccumPerHour
                * (m_ConscriptionActive ? mobCfg.DodgerConscriptionFearMult : 1f);
            // The integration is UNCONDITIONAL — only the breakdown-dirty is thresholded, and
            // against the accumulated drift since the last publish (see the field note above).
            m_DodgerLevel = System.Math.Clamp(
                m_DodgerLevel + (gain - mobCfg.DodgerDecayPerHour * m_DodgerLevel) * dh, 0f, 1f);

            if (System.Math.Abs(m_DodgerLevel - m_LastPublishedDodgerLevel) > DODGER_LEVEL_EPSILON)
            {
                m_LastPublishedDodgerLevel = m_DodgerLevel;
                m_IsDirty = true;
            }

            bool active = m_DodgerLevel > 0.001f;
            if (active != m_DodgerActive)
            {
                m_DodgerActive = active;
                if (active)
                    Log.Info($"[Dodgers] draft evasion RISING level={m_DodgerLevel:F3} pressure={pressure:F2} conscription={m_ConscriptionActive}");
                else
                    Log.Info("[Dodgers] draft evasion drained — manpower back to full");
            }
        }

        /// <summary>
        /// W2 row 166 (SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2). The cached
        /// singleton handle may point at an entity destroyed by load. Re-resolve
        /// from the world first, dedup, and create only if genuinely absent.
        /// Idempotent; called from lifecycle/restore before update code consumes
        /// the cached handle.
        /// </summary>
        private void RestoreMobilizationSingleton()
        {
            var previous = m_Singleton.Entity;
            var entity = EnsureSingleton(ref m_Singleton, MobilizationStateSingleton.Default);
            if (entity != previous)
                m_IsDirty = true;
        }

        public void OnLoadRestore(EntityManager entityManager)
        {
            // Merge policy: PreferRestoredPayload. Mobilization's durable payload
            // lives in persisted system fields; PLVS recreates the singleton before
            // ValidateAfterLoad rebuilds allocation-derived UsedManpower and writes
            // the authoritative breakdown.
            EnsureSingleton(ref m_Singleton, entityManager, MobilizationStateSingleton.Default);
            m_IsDirty = true;
        }

        private void UpdateBreakdown(bool force, bool refreshPopulation = false)
        {
            m_SingletonLookup.Update(this);
            uint currentFrame = (uint)UnityEngine.Time.frameCount;
            bool populationIntervalElapsed = currentFrame - m_LastUpdateFrame >= (uint)POPULATION_REFRESH_INTERVAL;

            bool shouldRefresh = force ||
                                m_IsDirty ||
                                populationIntervalElapsed ||
                                m_CachedPopulation == 0;

            if (!shouldRefresh) return;

            using (PerformanceProfiler.Measure("Mobilization.UpdateBreakdown"))
            {
                if (refreshPopulation || m_CachedPopulation == 0 || populationIntervalElapsed)
                {
                    m_CachedPopulation = this.GetCitizenCount();
                }

                var corruption = m_CorruptionQuery.TryGetSingleton<CorruptionSingleton>(out var cor)
                    ? cor : CorruptionSingleton.Default;
                int exportPercentage = corruption.ExportPercentage;

                // Draft-Exemption scheme (Corruption domain, Core singleton — Axiom 5):
                // sold deferrals bite a reversible slice; sold disabilities are permanent heads.
                var draftExemption = m_DraftExemptionQuery.TryGetSingleton<DraftExemptionSingleton>(out var de)
                    ? de : DraftExemptionSingleton.Default;

                float happinessPenalty = GetAverageHappinessPenalty();
                var timeProvider = GameTimeSystem.Instance;
                int warDay = timeProvider?.Current.WarDay ?? m_LastWarDay;

                m_CachedBreakdown = ManpowerLogic.BuildBreakdown(
                    m_CachedPopulation, exportPercentage, happinessPenalty, m_DodgerLevel, warDay,
                    m_UsedManpower, m_Casualties,
                    draftExemption.RatePercent, draftExemption.DisabilityHeadcount,
                    m_ConscriptionActive
                );
                ApplyDebugMoraleOverrideIfNeeded();

                if (m_UsedManpower > m_CachedBreakdown.TotalManpower && m_CachedBreakdown.TotalManpower >= 0)
                {
                    ForceReleaseExcess(m_UsedManpower - m_CachedBreakdown.TotalManpower);
                    m_CachedBreakdown = ManpowerLogic.BuildBreakdown(
                        m_CachedPopulation, exportPercentage, happinessPenalty, m_DodgerLevel, warDay,
                        m_UsedManpower, m_Casualties,
                        draftExemption.RatePercent, draftExemption.DisabilityHeadcount,
                        m_ConscriptionActive);
                    ApplyDebugMoraleOverrideIfNeeded();
                }

                // Predicted force-release: how much crew DeactivateConscription would
                // pull off the guns right now. Computed from the final breakdown
                // factors (after ForceReleaseExcess re-build and the debug-morale
                // override) so the number matches the actual force-release that the
                // next deactivation would trigger.
                int predictedConscriptionRelease = 0;
                if (m_ConscriptionActive)
                {
                    int totalWithoutConscription = ManpowerLogic.EffectiveTotal(
                        m_CachedBreakdown.BasePool,
                        m_CachedBreakdown.PatriotismFactor,
                        m_CachedBreakdown.MoraleFactor,
                        m_CachedBreakdown.FatigueFactor,
                        m_CachedBreakdown.DodgerFactor,
                        m_CachedBreakdown.Casualties,
                        draftExemption.RatePercent,
                        draftExemption.DisabilityHeadcount,
                        isConscription: false);
                    predictedConscriptionRelease = ManpowerLogic.PredictedForceRelease(m_UsedManpower, totalWithoutConscription);
                }

                var singletonEntity = m_Singleton.Entity;
                if (singletonEntity != Entity.Null && m_SingletonLookup.HasComponent(singletonEntity))
                {
                    m_SingletonLookup[singletonEntity] = new MobilizationStateSingleton
                    {
                        AvailableManpower = m_CachedBreakdown.AvailableManpower,
                        UsedManpower = m_CachedBreakdown.UsedManpower,
                        TotalManpower = m_CachedBreakdown.TotalManpower,
                        BasePool = m_CachedBreakdown.BasePool,
                        Casualties = m_CachedBreakdown.Casualties,
                        Population = m_CachedBreakdown.Population,
                        PatriotismFactor = m_CachedBreakdown.PatriotismFactor,
                        MoraleFactor = m_CachedBreakdown.MoraleFactor,
                        FatigueFactor = m_CachedBreakdown.FatigueFactor,
                        DodgerFactor = m_CachedBreakdown.DodgerFactor,
                        DodgerCount = m_CachedBreakdown.DodgerCount,
                        DeferralManpower = m_CachedBreakdown.DeferralManpower,
                        DisabilityManpower = m_CachedBreakdown.DisabilityManpower,
                        ConscriptionBonus = m_CachedBreakdown.ConscriptionBonus,
                        IsConscriptionActive = m_CachedBreakdown.IsConscriptionActive,
                        IsWarFatigued = m_CachedBreakdown.IsWarFatigued,
                        WarDay = m_CachedBreakdown.WarDay,
                        CallToArmsCooldownEndHour = m_CallToArmsCooldownEndHour,
                        IsCallToArmsOnCooldown = IsCallToArmsOnCooldown,
                        ConscriptionCooldownEndHour = m_ConscriptionCooldownEndHour,
                        IsConscriptionReactivationOnCooldown = IsConscriptionReactivationOnCooldown,
                        PredictedConscriptionRelease = predictedConscriptionRelease,
                        SocialPenaltyProducerReady = m_PenaltySystem != null
                    };
                }

                m_LastUpdateFrame = currentFrame;
                m_IsDirty = false;
            }
        }

        public void ReportCasualties(int amount, int typeHash, int idx, int ver)
        {
            amount = System.Math.Max(0, amount);
            m_Casualties = System.Math.Max(0, m_Casualties + amount);

            var key = new AllocKey(idx, ver);
            int eventTypeHash = typeHash;
            int released = 0;
            if (m_Allocations.TryGetValue(key, out var entry))
            {
                if (entry.TypeHash != 0)
                    eventTypeHash = entry.TypeHash;

                released = System.Math.Min(amount, System.Math.Max(0, entry.Count));
                int newAlloc = entry.Count - released;
                if (newAlloc <= 0) m_Allocations.Remove(key);
                else
                {
                    entry.Count = newAlloc;
                    m_Allocations[key] = entry;
                }
            }

            // XSTEP: casualty-driven crew release routes through Core CrewMath — the committed pool
            // shrinks by the same transition as a clean release, kept single-source in Core.
            m_UsedManpower = CrewMath.Release(m_UsedManpower, released);
            m_IsDirty = true;
            Log.Warn($"CASUALTIES: {amount} ({AllocationId(key, eventTypeHash)})");
            EventBus?.SafePublish(new ManpowerCasualtiesEvent(amount, m_Casualties, $"hash={eventTypeHash}"), "MobilizationSystem");
            CheckCritical();
        }

        public bool ActivateConscription()
        {
            if (m_ConscriptionActive) return false;
            if (IsConscriptionReactivationOnCooldown) return false;
            m_ConscriptionActive = true;
            var config = BalanceConfig.Current.Mobilization;
            ApplyReputationDelta(config.ConscriptionReputation, "Conscription activated");

            var tp = GameTimeSystem.Instance;
            if (tp != null)
                m_ConscriptionCooldownEndHour = tp.Current.TotalGameHours + config.ConscriptionCooldownHours;

            m_IsDirty = true;
            Log.Info($"CONSCRIPTION ACTIVATED, reactivation cooldown until hour {m_ConscriptionCooldownEndHour:F1}");
            EventBus?.SafePublish(new ConscriptionActivatedEvent(), "MobilizationSystem");
            return true;
        }

        public void DeactivateConscription()
        {
            if (!m_ConscriptionActive) return;
            m_ConscriptionActive = false;
            m_IsDirty = true;
            EventBus?.SafePublish(new ConscriptionDeactivatedEvent(), "MobilizationSystem");
        }

        public bool CallToArms()
        {
            if (IsCallToArmsOnCooldown) return false;
            if (m_Casualties <= 0) return false;
            var config = BalanceConfig.Current.Mobilization;
            int recovered = System.Math.Min(m_Casualties, config.CallToArmsRecovery);
            m_Casualties -= recovered;

            var tp = GameTimeSystem.Instance;
            if (tp != null)
                m_CallToArmsCooldownEndHour = tp.Current.TotalGameHours + config.CallToArmsCooldownHours;

            ApplyReputationDelta(config.CallToArmsReputation, "Call to Arms");

            m_IsDirty = true;
            Log.Info($"CALL TO ARMS: {recovered} recovered, cooldown until hour {m_CallToArmsCooldownEndHour:F1}");
            EventBus?.SafePublish(new CallToArmsEvent(recovered, m_Casualties), "MobilizationSystem");
            return true;
        }

        private void ApplyReputationDelta(float delta, string reason)
            => m_ReputationService.ModifyTrust(delta, reason);

        private float GetAverageHappinessPenalty()
        {
            if (m_PenaltySystem == null)
            {
                m_PenaltySystem = ResolvePenaltySystem();
                if (m_PenaltySystem == null)
                {
                    if (!m_LoggedMissingPenaltySystem)
                    {
                        m_LoggedMissingPenaltySystem = true;
                        Log.Warn("DistrictPenaltySystem unavailable; using neutral happiness penalty");
                    }
                    return 0f;
                }
            }
            var allPenalties = m_PenaltySystem.GetAllPenalties();
            if (allPenalties == null || allPenalties.Count == 0) return m_PenaltySystem.GetTotalPositiveHappinessPenalty(0);
            float sum = 0f;
            int positiveCount = 0;
            foreach (var kvp in allPenalties)
            {
                float positivePenalty = m_PenaltySystem.GetTotalPositiveHappinessPenalty(kvp.Key);
                if (positivePenalty <= 0f)
                    continue;

                sum += positivePenalty;
                positiveCount++;
            }

            return positiveCount > 0 ? sum / positiveCount : 0f;
        }

        private DistrictPenaltySystem? ResolvePenaltySystem()
        {
            return m_PenaltySystem ?? World.GetExistingSystemManaged<DistrictPenaltySystem>();
        }

        private void ForceReleaseExcess(int deficit)
        {
            if (deficit <= 0) return;
            int released = 0;
            m_ForceReleaseSortedScratch.Clear();
            m_ForceReleaseSortedScratch.AddRange(m_Allocations);
            var sorted = m_ForceReleaseSortedScratch;
            sorted.Sort((a, b) => a.Value.Count.CompareTo(b.Value.Count));

            EntityCommandBuffer ecb = default;
            bool hasEcb = false;

            if (sorted.Count > 0)
            {
                int requestCount = 0;
                foreach (var kvp in sorted)
                {
                    if (released >= deficit) break;

                    var key = kvp.Key;
                    int remaining = deficit - released;
                    int crewToRelease = System.Math.Min(kvp.Value.Count, remaining);
                    int newCrew = kvp.Value.Count - crewToRelease;
                    released += crewToRelease;

                    if (newCrew <= 0)
                        m_Allocations.Remove(key);
                    else
                    {
                        var entry = kvp.Value;
                        entry.Count = newCrew;
                        m_Allocations[key] = entry;
                    }

                    if (!hasEcb)
                    {
                        ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                        hasEcb = true;
                    }

                    var requestEntity = ecb.CreateEntity();
                    ecb.AddComponent(requestEntity, new ForceCrewReleaseRequest
                    {
                        AAEntityIndex = key.EntityIndex,
                        AAEntityVersion = key.EntityVersion,
                        NewCrewCount = newCrew
                    });
                    RequestMetaWriter.AddInternal(ecb, requestEntity, nameof(ForceCrewReleaseRequest), key.EntityIndex.ToString());
                    requestCount++;
                }
                Log.Warn($"FORCE RELEASE: {released} manpower freed, {requestCount} AA crew release requests sent");
            }

            // XSTEP: force-release of excess crew decrements the SAME committed accumulator via Core
            // CrewMath — the last inline `max(0, used - released)` would re-fork the transition off-Core.
            m_UsedManpower = CrewMath.Release(m_UsedManpower, released);
            EventBus?.SafePublish(new ManpowerForceReleasedEvent(released, m_CachedBreakdown.TotalManpower), "MobilizationSystem");
        }

        private void ApplyDebugMoraleOverrideIfNeeded()
        {
            if (!m_DebugMoraleOverrideActive)
                return;

            float morale = System.Math.Clamp(m_DebugMoraleOverride, 0f, 1f);
            int rawTotal = ManpowerLogic.CalculateTotalManpower(
                m_CachedBreakdown.BasePool,
                m_CachedBreakdown.PatriotismFactor,
                morale,
                m_CachedBreakdown.FatigueFactor,
                m_CachedBreakdown.DodgerFactor,
                m_CachedBreakdown.IsConscriptionActive);
            // Reuse the authoritative breakdown's exemption heads — the debug override
            // only reshapes morale, not the Draft-Exemption scheme state.
            int total = System.Math.Max(0, rawTotal
                - m_CachedBreakdown.Casualties
                - m_CachedBreakdown.DeferralManpower
                - m_CachedBreakdown.DisabilityManpower);
            int available = System.Math.Max(0, total - m_CachedBreakdown.UsedManpower);

            m_CachedBreakdown = new ManpowerBreakdown(
                m_CachedBreakdown.Population,
                m_CachedBreakdown.BasePool,
                m_CachedBreakdown.PatriotismFactor,
                morale,
                m_CachedBreakdown.FatigueFactor,
                m_CachedBreakdown.DodgerFactor,
                m_CachedBreakdown.ConscriptionBonus,
                total,
                m_CachedBreakdown.UsedManpower,
                available,
                m_CachedBreakdown.Casualties,
                m_CachedBreakdown.DodgerCount,
                m_CachedBreakdown.DeferralManpower,
                m_CachedBreakdown.DisabilityManpower,
                m_CachedBreakdown.WarDay,
                m_CachedBreakdown.IsWarFatigued,
                m_CachedBreakdown.IsConscriptionActive);
        }

        /// <summary>
        /// Pause-safe single-action apply, called by MobilizationRequestIntakeSystem
        /// (ModificationEnd, AXIOM 14) per request. All state touched here is managed
        /// (fields, cooldowns, reputation service, EventBus) — no SystemAPI, safe to
        /// run outside this system's OnUpdate. Request iteration/destruction stays in
        /// the intake — this system is not the request consumer.
        /// </summary>
        internal bool ApplyMobilizationActionImmediate(MobilizationActionType action, out RequestKind kind, out string failReason)
        {
            failReason = "";
            kind = action == MobilizationActionType.CallToArms
                ? RequestKind.Mobilization
                : RequestKind.ConscriptionToggle;

            bool success = true;
            switch (action)
            {
                case MobilizationActionType.ActivateConscription:
                    success = MobilizationEligibility.CanToggleConscription(
                                  reactivating: true,
                                  IsConscriptionReactivationOnCooldown,
                                  out failReason)
                              && ActivateConscription();
                    break;
                case MobilizationActionType.DeactivateConscription: DeactivateConscription(); break;
                case MobilizationActionType.CallToArms:
                    success = MobilizationEligibility.CanCallToArms(
                        true,
                        m_Casualties,
                        IsCallToArmsOnCooldown,
                        out failReason)
                        && CallToArms();
                    break;
                default: success = false; break;
            }

            if (!success && string.IsNullOrEmpty(failReason))
            {
                failReason = action == MobilizationActionType.CallToArms
                    ? ReasonIds.MobCallToArmsRejected
                    : ReasonIds.MobRejected;
            }

            return success;
        }

        private void ProcessReleaseRequests()
        {
            if (m_ReleaseRequestQuery.IsEmpty) return;
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            foreach (var (request, entity) in SystemAPI.Query<RefRW<ManpowerReleaseRequest>>().WithEntityAccess())
            {
                if (!hasEcb)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    hasEcb = true;
                }

                if (!request.ValueRO.DomainApplied)
                {
                    Release(request.ValueRO.Count, request.ValueRO.AATypeHash, request.ValueRO.EntityIndex, request.ValueRO.EntityVersion);
                    request.ValueRW.DomainApplied = true;
                }
                ecb.DestroyEntity(entity);
            }
        }

        private void ProcessCasualtyRequests()
        {
            if (m_CasualtyRequestQuery.IsEmpty) return;
            EntityCommandBuffer ecb = default;
            bool hasEcb = false;
            foreach (var (request, entity) in SystemAPI.Query<RefRW<CasualtyReportRequest>>().WithEntityAccess())
            {
                if (!hasEcb)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    hasEcb = true;
                }

                if (!request.ValueRO.DomainApplied)
                {
                    ReportCasualties(request.ValueRO.Count, request.ValueRO.AATypeHash, request.ValueRO.EntityIndex, request.ValueRO.EntityVersion);
                    request.ValueRW.DomainApplied = true;
                }
                ecb.DestroyEntity(entity);
            }
        }

        private void OnWarDayChanged(WarDayChangedEvent evt)
        {
            m_LastWarDay = evt.WarDay;
            m_IsDirty = true;
        }

        private void CheckCritical()
        {
            // Force refresh to avoid stale data after TryRecruit/ReportCasualties
            UpdateBreakdown(true);
            int available = m_CachedBreakdown.AvailableManpower;
            int total = m_CachedBreakdown.TotalManpower;
            // Predicate via the shared Core rule (same home UI reads). The total > 0
            // gate stays as the event-payload division guard below: IsCritical treats
            // total <= 0 as critical, but firing the event with available/total would
            // divide by zero, so the no-manpower case stays a silent non-fire as before.
            if (total > 0 && ManpowerLogic.IsCritical(available, total))
            {
                // LOAD-INVARIANT: request/event paths can reach CheckCritical before GameTime activation.
                if (!GameTimeSystem.TryGetGameHours(out var currentHour))
                    return;
                if (currentHour - m_LastCriticalEventHour > GameRate.HOURS_PER_DAY)
                {
                    m_LastCriticalEventHour = currentHour;
                    Log.Warn("CRITICAL MANPOWER!");
                    EventBus?.SafePublish(new ManpowerCriticalEvent(available, total, (float)available / total), "MobilizationSystem");
                }
            }
        }

        [CompletesDependency("ValidateAfterLoad: rebuild allocation map before retained Release/Casualty requests replay; CalculateEntityCount is diagnostic-only")]
        public void ValidateAfterLoad()
        {
            m_Allocations.Clear();
            int rebuiltManpower = 0;
            if (!m_AAInstallationQuery.IsEmptyIgnoreFilter)
            {
                var entities = m_AAInstallationQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
                foreach (var e in entities)
                {
                    if (!EntityManager.HasComponent<AirDefenseInstallation>(e)) continue;
                    if (EntityManager.HasComponent<Deleted>(e) || EntityManager.HasComponent<Destroyed>(e)) continue;

                    var aa = EntityManager.GetComponentData<AirDefenseInstallation>(e);
                    var building = aa.GetBuildingEntity();
                    if (building == Entity.Null ||
                        !EntityManager.Exists(building) ||
                        EntityManager.HasComponent<Deleted>(building) ||
                        EntityManager.HasComponent<Destroyed>(building))
                    {
                        continue;
                    }

                    if (aa.CrewAssigned > 0)
                    {
                        var key = new AllocKey(e.Index, e.Version);
                        m_Allocations[key] = new AllocEntry((int)aa.Type, aa.CrewAssigned);
                        rebuiltManpower += aa.CrewAssigned;
                    }
                }
                if (entities.IsCreated) entities.Dispose();
            }
            if (rebuiltManpower != m_UsedManpower)
                Log.Warn($"[Mobilization] UsedManpower mismatch: saved={m_UsedManpower} rebuilt={rebuiltManpower}");
            m_UsedManpower = rebuiltManpower;
            // W2 row 166: re-resolve the singleton BEFORE the post-load breakdown
            // write, so the rebuilt values are actually stored (the stale cached
            // handle made the HasComponent guard skip the write, leaving default
            // Mobilization UI for 1+ frames).
            RestoreMobilizationSingleton();
            UpdateBreakdown(true, refreshPopulation: true);
            int pendingReleaseRequests = m_ReleaseRequestQuery.IsEmptyIgnoreFilter ? 0 : m_ReleaseRequestQuery.CalculateEntityCount();
            int pendingCasualtyRequests = m_CasualtyRequestQuery.IsEmptyIgnoreFilter ? 0 : m_CasualtyRequestQuery.CalculateEntityCount();
            Log.Info($"ValidateAfterLoad: rebuilt used={m_UsedManpower}, available={m_CachedBreakdown.AvailableManpower}, total={m_CachedBreakdown.TotalManpower}, aaEntities={m_AAInstallationQuery.CalculateEntityCount()}, pendingRelease={pendingReleaseRequests}, pendingCasualty={pendingCasualtyRequests}");
        }

        public void ResetState()
        {
            ResetBootDefaultsFields();
            RestoreMobilizationSingleton();
            UpdateBreakdown(true, refreshPopulation: true);
        }

        private void ResetBootDefaultsFields()
        {
            ResetPersistFields();
            m_Allocations.Clear();
            m_ForceReleaseSortedScratch.Clear();
            m_CachedBreakdown = default;
            m_CachedPopulation = 0;
            m_LastUpdateFrame = 0;
            m_DebugMoraleOverrideActive = false;
            m_DebugMoraleOverride = 0f;
            m_LastDodgerHour = -1f;
            m_DodgerActive = false;
            m_LastPublishedDodgerLevel = 0f;
            m_Singleton.Invalidate();
            m_IsDirty = true;
        }

#if DEBUG
        public void DebugSetMoraleFactor(float value, string source)
        {
            m_DebugMoraleOverride = System.Math.Clamp(value, 0f, 1f);
            m_DebugMoraleOverrideActive = true;
            m_IsDirty = true;
            UpdateBreakdown(true);
            Log.Info($"[DEBUG] {source}: morale factor set to {m_DebugMoraleOverride:F2}");
        }

        public void DebugResetMobilization(string source)
        {
            ResetState();
            Log.Info($"[DEBUG] {source}: mobilization reset");
        }
#endif

        public void SetDefaults(Context context)
        {
            ResetState();
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IMobilizationManpowerReader>(this);
            }

#if DEBUG
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IMobilizationDebugMutator>(this);
            }
#endif

            // Symmetric to OnCreate's SubscribeRequired: without this the EventBus
            // retains a delegate bound to this destroyed system instance across every
            // world reload, so handlers accumulate and fire on a dead EntityManager.
            UnsubscribeSafe<WarDayChangedEvent>(OnWarDayChanged);

            var singletonEntity = m_Singleton.Entity;
            if (singletonEntity != Entity.Null && EntityManager.Exists(singletonEntity))
            {
                EntityManager.DestroyEntity(singletonEntity);
                m_Singleton.Invalidate();
            }

            m_Allocations.Clear();
            base.OnDestroy();
            Log.Info("Destroyed");
        }
    }
}
