using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Game;
using Game.Common;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.Domain.Intel;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Spotters;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Cognitive;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Domains.Cognitive.Core.Systems;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Adapters;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.UI;
using CivicSurvival.Core.UI.DomainState;
using B = CivicSurvival.Core.UI.B;
using HeroStatusEnum = CivicSurvival.Core.Components.CrossDomain.HeroStatus;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Systems;
using CivicSurvival.Domains.Cognitive.Ops.Systems;
using CivicSurvival.Localization;

namespace CivicSurvival.Domains.Cognitive.UI
{
    /// <summary>
    /// UI system for Cognitive domain data.
    /// ECS-Pure: Reads directly from CognitiveState singleton.
    ///
    /// Migrated from CognitiveUIPanel → CivicUIPanelSystem.
    /// Gains: auto-disposed EntityQueries, proper ECS lifecycle.
    /// Removes #pragma warning disable CIVIC051 (now proper ECS system).
    /// </summary>
    [ActIndependent]
    [HandlesRequestKind(RequestKind.InternetMode)]
    [HandlesRequestKind(RequestKind.TelemarathonMode)]
    [HandlesRequestKind(RequestKind.TelemarathonActive)]
    [HandlesRequestKind(RequestKind.PropagandaCenterUpgrade)]
    public partial class CognitiveUISystem : CivicUIPanelSystem, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // Propaganda Center readout fallbacks (mirror the balance contract defaults; used only before
        // the config loads).
        private const int FALLBACK_CENTER_COST = 150000;
        private const int FALLBACK_CENTER_UPGRADE_COST = 80000;
        private const int FALLBACK_CENTER_MAX_TIER = 4;

        // Hero-RPS counter magnitudes (mirror the balance contract; used only before the config loads).
        private const float FALLBACK_HERO_COUNTER_STRONG = 0.7f;
        private const float FALLBACK_HERO_COUNTER_FLOOR = 0.15f;

        // Phase 13B forecast weighting — mirror of PsyOpsLaunchSystem's private constants
        // (COVERAGE_GAP_EPSILON, TARGET_BASE_WEIGHT, TARGET_INFECTION_WEIGHT_SCALE). Kept in sync by
        // hand: the launcher owns the RNG draw, this is the RNG-free preview of its deterministic slice.
        private const float FORECAST_COVERAGE_GAP_EPSILON = 1e-4f;
        private const int FORECAST_TARGET_BASE_WEIGHT = 10;
        private const int FORECAST_TARGET_INFECTION_WEIGHT_SCALE = 100;

        // ===== Narrative Integrity Thresholds =====
        private const float VIGILANT_THRESHOLD = 0.8f;
        private const float QUESTIONING_THRESHOLD = 0.7f;
        private const float ANGER_THRESHOLD = 0.3f;

        private EndFrameBarrier m_EndFrameBarrier = null!;
        private HeroDeploymentSystem? m_HeroDeploymentSystem;
        private CognitiveStateSystem? m_CognitiveStateSystem;
        private TelemarathonSystem? m_TelemarathonSystem;
        private PropagandaCenterSystem? m_CenterSystem;
        // CIVIC434 wire: m_CenterSystem is read from the update path (pause-poll in
        // OnPanelUpdate), so its resolve carries retry semantics instead of a one-shot cache.
        private readonly CivicDependencyWire m_CenterWire = new(nameof(CognitiveUISystem));
        // Phase 13B forecast: the shared cognitive-coverage projection (least-covered type = the
        // enemy's next likely carrier). Injected the same way the launcher does — via the service
        // registry null-object, so an absent reader reads as "no gap" (unknown), never a crash.
        private ICognitiveCoverageReader? m_CoverageReader;
        private IBuildingPlacementService m_PlacementCommand = null!;
        private EntityQuery m_CenterQuery;
        private EntityQuery m_StateQuery;
        private EntityQuery m_SickLeaveQuery;
        private EntityQuery m_SpotterCmQuery;
        private EntityQuery m_StatsQuery;
        private EntityQuery m_DistrictPowerQuery;
        private EntityQuery m_IpsoQuery;
        private EntityQuery m_TelemarathonQuery;
        private EntityQuery m_PsyOpsQuery;

        private ComponentLookup<CognitiveState> m_StateLookup;
        private ComponentLookup<SpotterCountermeasuresState> m_SpotterCmLookup;
        private ComponentLookup<CognitiveStatsState> m_StatsLookup;
        private ComponentLookup<TelemarathonRuntimeState> m_TelemarathonLookup;
        private ComponentLookup<IPSOState> m_IpsoLookup;
        private BufferLookup<InternetDisabledBuffer> m_InternetDisabledBufferLookup;
        private BufferLookup<CognitiveIntegrityBuffer> m_IntegrityBufferLookup;
        private BufferLookup<DistrictEntityEntry> m_DistrictEntityBufferLookup;

        // District data list, serialized to a JSON array string binding
        private readonly List<CognitiveDistrictEntry> m_DistrictsList = new();

        // Per-stratum opinion lanes + live PSYOPS contacts, each serialized to its own
        // JSON array string binding (same pipeline as m_DistrictsList).
        private readonly List<CognitiveStratumEntry> m_StrataList = new();
        private readonly List<CognitivePsyOpsEntry> m_PsyOpsList = new();

        // ── PSYOPS after-action report (info-war debriefing) ──
        // Mirrors ThreatUISystem's kinetic debriefing, but the trigger is two-staged: raid facts are
        // aggregated the moment a wave's raids finish LANDING (the attack snapshot is still alive),
        // and the report itself waits until the wave's landed window CLOSES (every attack expired) —
        // a landed raid presses for its whole window, so only the close-time read can show what the
        // strike actually did. m_LastPsyDebriefWave dedupes so each wave briefs once; the
        // m_PsyDebrief* fields are a FROZEN snapshot while the modal is up. Managed/transient (not
        // serialized) — a save mid-window just re-briefs at worst nothing. If a newer wave finishes
        // landing while an older one still awaits close (windows outlasting the wave cadence), the
        // newer wave takes the report over — the accepted cost of one report surface.
        private const float PSY_DEBRIEF_MIN_DISPLAY = 10f;
        private bool m_ShowPsyDebrief;
        private bool m_PsyDebriefVisibleInCoordinator;
        private double m_PsyDebriefShownTime;
        private int m_LastPsyDebriefWave = -1;   // highest wave already briefed (dedupe)
        private bool m_PsyDebriefAwaitingClose;  // raid facts captured; waiting for the landed window to close
        private bool m_PsyDebriefBaselinePending; // capture the infected-count baseline from this frame's dto
        private int m_PsyDebriefBaselineInfected; // city infected count when the wave finished landing
        private bool m_PsyDebriefPending;        // window closed this frame, awaiting snapshot+show
        private int m_PsyDebriefWave;
        private int m_PsyDebriefRaidCount;
        private int m_PsyDebriefPropaganda;
        private int m_PsyDebriefFakeVideo;
        private int m_PsyDebriefRumor;
        private int m_PsyDebriefBlunted;
        private int m_PsyDebriefStrataMask;
        private float m_PsyDebriefPeakIntensity;
        private float m_PsyDebriefMediaTrust;
        private int m_PsyDebriefHouseholdsImpact;
        private bool m_PsyDebriefFoodRunActive;
        private int m_PsyDebriefInfectedDelta;   // households newly infected over the landed window

        // ── PSYOPS inbound alert ──
        // The counterpart of the after-action report, at the other end of the raid: it fires while
        // the contact is still IN FLIGHT, because that is the only time the advice it carries can be
        // acted on. Once per wave (m_LastPsyInboundWave dedupes). It auto-closes when the raid lands
        // — advice about a window that has shut is a lie, and a stale modal would blame the player
        // for a beat he could no longer play. Transient, never serialized.
        private bool m_ShowPsyInbound;
        private bool m_PsyInboundVisibleInCoordinator;
        private int m_LastPsyInboundWave = -1;
        private int m_PsyInboundWave;
        private int m_PsyInboundCount;
        private int m_PsyInboundType = -1;
        private int m_PsyInboundStratum;
        private float m_PsyInboundEtaHours;
        private bool m_PsyInboundCounterReady;

        // HashSet for internet disabled lookup (reused to avoid GC)
        // Keys are district indices
        [NonEntityIndex] private readonly HashSet<int> m_InternetDisabledLookup = new();

        // District name resolution
        private NameSystemFacade? m_NameService;
        private readonly Dictionary<long, string> m_NameCache = new();
        private readonly Dictionary<long, Entity> m_EntityLookup = new();
        [NonEntityIndex] private readonly Dictionary<int, long> m_DistrictKeyByIndex = new();
        private int m_NameCacheRefreshCounter;
        private const int NAME_CACHE_REFRESH_INTERVAL = 300;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_StateQuery = GetEntityQuery(
                ComponentType.ReadOnly<CognitiveState>());
            m_SickLeaveQuery = GetEntityQuery(
                ComponentType.ReadOnly<FakeSickLeave>(),
                ComponentType.Exclude<Deleted>());
            m_SpotterCmQuery = GetEntityQuery(
                ComponentType.ReadOnly<SpotterCountermeasuresState>());
            m_StatsQuery = GetEntityQuery(
                ComponentType.ReadOnly<CognitiveStatsState>());
            m_TelemarathonQuery = GetEntityQuery(
                ComponentType.ReadOnly<TelemarathonRuntimeState>());
            m_DistrictPowerQuery = GetEntityQuery(
                ComponentType.ReadOnly<DistrictPowerBufferSingleton>());
            m_IpsoQuery = GetEntityQuery(
                ComponentType.ReadOnly<IPSOState>());
            m_PsyOpsQuery = GetEntityQuery(
                ComponentType.ReadOnly<PsyOpsAttack>());
            m_CenterQuery = GetEntityQuery(
                ComponentType.ReadOnly<PropagandaCenterState>());
            m_StateLookup = GetComponentLookup<CognitiveState>(true);
            m_SpotterCmLookup = GetComponentLookup<SpotterCountermeasuresState>(true);
            m_StatsLookup = GetComponentLookup<CognitiveStatsState>(true);
            m_TelemarathonLookup = GetComponentLookup<TelemarathonRuntimeState>(true);
            m_IpsoLookup = GetComponentLookup<IPSOState>(true);
            m_InternetDisabledBufferLookup = GetBufferLookup<InternetDisabledBuffer>(true);
            m_IntegrityBufferLookup = GetBufferLookup<CognitiveIntegrityBuffer>(true);
            m_DistrictEntityBufferLookup = GetBufferLookup<DistrictEntityEntry>(true);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_HeroDeploymentSystem ??= World.GetExistingSystemManaged<HeroDeploymentSystem>();
            m_CognitiveStateSystem ??= World.GetExistingSystemManaged<CognitiveStateSystem>();
            m_TelemarathonSystem ??= World.GetExistingSystemManaged<TelemarathonSystem>();
            m_CenterWire.EnsureWired(() =>
            {
                m_CenterSystem ??= World.GetExistingSystemManaged<PropagandaCenterSystem>();
                return m_CenterSystem != null;
            });
            m_PlacementCommand = ServiceRegistry.Instance.Require<IBuildingPlacementService>();
        }

        protected override void ConfigureBindings()
        {
            Bindings.Add<string>(B.CognitiveState, "{}");
            // Unified string-binding pipeline: JSON array string parsed on the UI
            // by useSafeJsonArray. BindingRegistry dedups identical payloads.
            Bindings.Add<string>(B.CognitiveDistricts, JsonBuilder.EmptyArray);
            Bindings.Add<string>(B.CognitiveStrata, JsonBuilder.EmptyArray);
            Bindings.Add<string>(B.CognitivePsyOps, JsonBuilder.EmptyArray);
        }

        protected override void ConfigureTriggers()
        {
            Triggers.AddWarTrigger<int>(B.DeployHero, FeatureIds.Cognitive, RequestResultBridge.HeroAction, OnDeployHero);
            Triggers.Add(B.RecallHero, FeatureIds.Cognitive, RequestResultBridge.HeroAction, OnRecallHero);
            Triggers.AddWarTrigger<int>(B.SetHeroMode, FeatureIds.Cognitive, RequestResultBridge.HeroAction, OnSetHeroMode);
            // Not a war trigger: picking the speaker is a choice, not an operation. With no hero
            // deployed the switch is free and instant (TrySetHeroArchetype), so before the war it
            // only records who Deploy will put on air — and Deploy stays act-gated on its own.
            // Registered as a war trigger it was refused wholesale in PreWar, which bounced the
            // selection back and fired a "not until the war" toast at a player just reading the
            // archetypes. Cost and switch cooldown live inside the command and still apply.
            Triggers.Add<int>(B.SetHeroArchetype, FeatureIds.Cognitive, RequestResultBridge.HeroAction, OnSetHeroArchetype);
            // Broadcast coverage-allocation split — sync host-direct (pause-safe, like SetHeroArchetype).
            // Fire-and-forget: the drill-down only opens for an online Center, so no request-result surface.
            Triggers.Add<int, int, int>(B.SetBroadcastAllocation, FeatureIds.Cognitive, OnSetBroadcastAllocation);

            // Telemarathon triggers
            Triggers.Add<int>(B.SetNarrativeMode, FeatureIds.Cognitive, RequestResultBridge.TelemarathonMode, OnSetNarrativeMode);
            Triggers.Add<bool>(B.SetTelemarathonActive, FeatureIds.Cognitive, RequestResultBridge.TelemarathonActive, OnSetTelemarathonActive);

            // Global Internet Mode trigger
            Triggers.AddWarTrigger<int>(B.SetInternetMode, FeatureIds.Cognitive, RequestResultBridge.InternetMode, OnSetInternetMode);

            // Propaganda Center: placement activates the vanilla tool synchronously from the
            // UI callback so building works while paused (Axiom 14, AA precedent). Upgrade is a sync
            // host-direct command (like SetHeroArchetype). The Center's prefab reuses the Patriot visual.
            Triggers.Add(B.PlacePropagandaCenter, FeatureIds.Cognitive, RequestResultBridge.PropagandaCenterPlacement, OnPlacePropagandaCenter);
            Triggers.Add(B.UpgradePropagandaCenter, FeatureIds.Cognitive, RequestResultBridge.PropagandaCenterUpgrade, OnUpgradePropagandaCenter);

            // Info-war after-action report dismiss (fire-and-forget UI, mirror of DismissDebriefing).
            Triggers.Add(B.DismissPsyDebriefing, FeatureIds.Cognitive, OnDismissPsyDebriefing);
            Triggers.Add(B.DismissPsyInbound, FeatureIds.Cognitive, OnDismissPsyInbound);
        }

        protected override void OnPanelUpdate()
        {
            // While paused, the GameSimulation rebuild of the derived Center mirror never runs —
            // but pause-safe surfaces keep changing the installs (pause placement via the
            // ModificationEnd commit, pause bulldoze via the demolition system). Poll the
            // pause-tick refresh here (UIUpdate ticks while paused, ~500 ms throttle) so the
            // panel and its unlock gates track those changes; unpaused, the GameSim tick owns
            // the mirror and this stays out of the way (Axiom 14).
            if (TriggerOutcome.IsSimulationPaused(World))
            {
                // Retry the wire before the poll: if the Center system was not resolvable at
                // OnStartRunning, the pause-poll must not stay silently dead for the session.
                m_CenterWire.EnsureWired(() =>
                {
                    m_CenterSystem ??= World.GetExistingSystemManaged<PropagandaCenterSystem>();
                    return m_CenterSystem != null;
                });
                m_CenterSystem?.RefreshDerivedState();
            }

            // Refresh name cache periodically (picks up renamed districts)
            if (++m_NameCacheRefreshCounter >= NAME_CACHE_REFRESH_INTERVAL)
            {
                m_NameCacheRefreshCounter = 0;
                m_NameCache.Clear();
            }

            // Read state from singleton
            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return;

            UpdateLookups();
            if (!m_StateLookup.HasComponent(stateEntity))
                return;

            var state = m_StateLookup[stateEntity];
            var heroState = SystemAPI.TryGetSingleton<HeroDeploymentState>(out var hs)
                ? hs : HeroDeploymentState.Default;

            var dto = new CognitiveDto
            {
                CognitiveActive = state.IsActive,
                InfectionRate = CognitiveRates.EffectiveInfectionRate(state, heroState),
                RecoveryRate = CognitiveRates.EffectiveRecoveryRate(state, heroState),
                PenaltyThreshold = state.CompromiseThreshold,
                InternetMode = (int)state.InternetMode,
                CommercePenalty = state.CurrentCommercePenalty,
                HeroStatus = (int)heroState.HeroStatus,
                HeroDeployCost = heroState.HeroDeployCost,
                HeroInfectionReduction = heroState.HeroInfectionReduction,
                HeroRecoveryBonus = heroState.HeroRecoveryBonus,
                HeroArchetype = (int)heroState.Archetype,
                ArestovychDebtRatio = heroState.ArestovychTrustDebt,
                HeroActionRequestJson = RequestResultBridge.Get(RequestResultBridge.HeroAction).ToJson(),
                InternetModeRequestJson = RequestResultBridge.Get(RequestResultBridge.InternetMode).ToJson(),
                TelemarathonModeRequestJson = RequestResultBridge.Get(RequestResultBridge.TelemarathonMode).ToJson(),
                TelemarathonActiveRequestJson = RequestResultBridge.Get(RequestResultBridge.TelemarathonActive).ToJson()
            };
            FillHeroEligibility(ref dto);

            // Rumor tell: aggregate Food resource-run intensity (0-1) so the opinion board can
            // surface an active panic-buying run. Derived, read-only (published by PsyOpsLaunchSystem).
            dto.RumorFoodRunIntensity = ResourceRunAdapter.GetFoodRunIntensity(World);

            // FakeVideo tell: how many citizens sit home on fake sick leave right now. A chunk
            // count over the FakeSickLeave markers — no per-entity read, no dependency touch.
            dto.SickLeaveActiveCount = m_SickLeaveQuery.CalculateEntityCount();

            // Archetype timing flags need the absolute game hour (PsyOpsAttack stamps + hero
            // cooldowns are all TotalGameHours). Resolve once and reuse for the PSYOPS lanes below.
            // No time provider yet → skip this publish entirely (the next 500ms republish retries):
            // substituting hour 0 into comparisons with absolute stamps would fake an Arestovych
            // debt-collapse window and corrupt the live in-flight HeroShield read.
            if (!GameTimeSystem.TryGetGameHours(out float currentHour))
                return;
            dto.ArestovychDebtCollapsing = heroState.IsArestovychDebtCollapsing(currentHour);
            dto.HeroArchetypeSwitchReady = currentHour >= heroState.ArchetypeSwitchCooldownEndHour;

            // Build internet disabled lookup
            m_InternetDisabledLookup.Clear();
            if (m_SpotterCmQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var cmEntity)
                && m_SpotterCmLookup.HasComponent(cmEntity)
                && m_InternetDisabledBufferLookup.HasBuffer(cmEntity))
            {
                var internetBuffer = m_InternetDisabledBufferLookup[cmEntity];
                for (int i = 0; i < internetBuffer.Length; i++)
                {
                    m_InternetDisabledLookup.Add(internetBuffer[i].DistrictIndex);
                }
            }

            // Read integrity buffer
            if (!m_IntegrityBufferLookup.HasBuffer(stateEntity)) return;
            var buffer = m_IntegrityBufferLookup[stateEntity];

            // Rebuild entity lookup for name resolution (~20 entries, trivial at 500ms throttle)
            RebuildEntityLookup();

            int compromisedCount = 0;
            float totalIntegrity = 0f;
            m_DistrictsList.Clear();

            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                totalIntegrity += entry.Integrity;
                if (entry.IsCompromised)
                    compromisedCount++;

                bool hasInternet = CognitiveDistrictEligibility.CanInternetReach(
                    state.InternetMode,
                    entry.DistrictIndex,
                    m_InternetDisabledLookup.Contains(entry.DistrictIndex));

                string districtName;
                if (entry.DistrictIndex == 0)
                {
                    districtName = LocalizationManager.T("UI_DISTRICT_UNZONED");
                }
                else if (m_DistrictKeyByIndex.TryGetValue(entry.DistrictIndex, out var districtKey)
                    && m_NameCache.TryGetValue(districtKey, out var cachedName)
                    && IsCachedDistrictNameStillValid(districtKey))
                {
                    districtName = cachedName;
                }
                else
                {
                    if (m_DistrictKeyByIndex.TryGetValue(entry.DistrictIndex, out districtKey))
                        m_NameCache.Remove(districtKey);
                    var resolved = ResolveDistrictName(entry.DistrictIndex);
                    if (resolved != null)
                    {
                        if (m_DistrictKeyByIndex.TryGetValue(entry.DistrictIndex, out districtKey))
                            m_NameCache[districtKey] = resolved;
                        districtName = resolved;
                    }
                    else
                    {
                        districtName = $"District {entry.DistrictIndex}";
                    }
                }

                m_DistrictsList.Add(new CognitiveDistrictEntry
                {
                    DistrictIndex = entry.DistrictIndex,
                    Name = districtName,
                    Integrity = entry.Integrity,
                    HasInternet = hasInternet,
                    IsCompromised = entry.IsCompromised,
                    IsUnzoned = entry.DistrictIndex == 0
                });
            }

            m_DistrictsList.Sort((a, b) => a.Integrity.CompareTo(b.Integrity));

            float avgIntegrity = buffer.Length > 0 ? totalIntegrity / buffer.Length : 1f;

            dto.TotalDistricts = buffer.Length;
            dto.CompromisedDistricts = compromisedCount;
            dto.AvgIntegrity = avgIntegrity;
            dto.ProtestRisk = CalculateProtestRisk(avgIntegrity);
            dto.DominantNarrative = GetDominantNarrative(avgIntegrity, state.IsActive);
            PublishJsonWhenComplete(B.CognitiveDistricts, NoSourceChecks, () => SerializeCognitiveDistricts(m_DistrictsList));

            // Propaganda Center posture — read once here (before the stratum lanes, which need the
            // Phase-13A coverage-capacity ceiling + allocation split) and reused for the citywide
            // readout below. Default (all-zero) when no Center exists → honest zero coverage.
            bool hasCenter = m_CenterQuery.TryGetSingleton<PropagandaCenterState>(out var centerState);

            // One PsyOpsAttack snapshot per panel update, owned here and shared by the stratum
            // lanes (enemy-reach aggregate) and the PSYOPS contacts — the same query used to be
            // snapshotted twice per update (2 Temp allocations + 2 completion barriers).
            // PERF-LOCK: the IsEmptyIgnoreFilter branch keeps the no-raid steady state free of the
            // ToComponentDataArray dependency-complete — raids are absent most of the game, and
            // snapping an empty query would still pay the sync every ~500 ms panel update.
            var attacks = m_PsyOpsQuery.IsEmptyIgnoreFilter
                ? default
                : m_PsyOpsQuery.ToComponentDataArray<PsyOpsAttack>(Allocator.Temp);
            try
            {
                // Household-level stats — captured once and reused by the forecast (13B) + the
                // legible-counter signals (13C), both of which key on the per-stratum read model.
                CognitiveStatsState stats = default;
                bool hasStats = m_StatsQuery.TryGetSingletonEntity<CognitiveStatsState>(out var statsEntity);
                if (hasStats)
                {
                    stats = m_StatsLookup[statsEntity];
                    dto.TotalHouseholds = stats.TotalHouseholds;
                    dto.AvgInfection = stats.AvgInfectionLevel;
                    dto.AvgResistance = stats.AvgResistance;
                    dto.AvgTrauma = stats.AvgTrauma;
                    dto.HouseholdsUnderBlackout = stats.HouseholdsUnderBlackout;
                    dto.HouseholdsWithEnvy = stats.HouseholdsWithEnvy;
                    dto.HouseholdsUnderImpact = stats.HouseholdsUnderImpact;
                    dto.HouseholdsInfected = stats.HouseholdsInfected;
                    dto.VulnerableHouseholds = stats.VulnerableHouseholds;
                    dto.AvgBlackoutHours = stats.AvgBlackoutHours;
                    dto.BlackoutVulnerability = stats.BlackoutVulnerabilityMult;

                    BuildStrataLanes(in stats, in centerState, attacks, currentHour);
                }
                else
                {
                    // No stats singleton yet → empty lanes; the UI renders an honest zero /
                    // "classifying…" state (no template fallback).
                    m_StrataList.Clear();
                }
                PublishJsonWhenComplete(B.CognitiveStrata, NoSourceChecks, () => SerializeCognitiveStrata(m_StrataList));

                // Phase 13B — recompute the next raid's likely type + seam one step ahead (RNG-free),
                // gated by the shared intel fog. Off while the cognitive war is not live.
                PopulateForecast(ref dto, state.IsActive, in stats, hasStats);

                // Live discrete PSYOPS contacts (telegraph): incoming raids read under intel fog so the
                // player can re-posture the speaker archetype before land (live response). Phase 13C
                // legible-counter signals (hero shield + target coverage + reach households) are attached
                // per contact here, where the stats + hero state + config are all in scope.
                BuildPsyOpsContacts(currentHour, in heroState, in stats, hasStats, dto.ArestovychDebtCollapsing, attacks);

                // Info-war after-action: aggregate the raids of any wave that has just finished
                // landing (needs the live attack snapshot, so it runs here inside the try), then
                // hold the report until the wave's landed window closes — only then does the
                // infected-delta read say what the strike actually did. The snapshot of derived
                // readouts (media trust / impact / food-run) + the modal show are deferred to
                // after the dto is fully populated below.
                DetectPsyDebriefWave(attacks);
                DetectPsyDebriefWindowClose(attacks);

                // …and its opposite end: a raid is IN THE AIR and can still be answered.
                DetectPsyInboundWave(currentHour, in heroState, attacks);
            }
            finally
            {
                if (attacks.IsCreated) attacks.Dispose();
            }
            PublishJsonWhenComplete(B.CognitivePsyOps, NoSourceChecks, () => SerializeCognitivePsyOps(m_PsyOpsList));

            // Telemarathon state
            if (m_TelemarathonQuery.TryGetSingletonEntity<TelemarathonRuntimeState>(out var telemarathonEntity))
            {
                var telemarathon = m_TelemarathonLookup[telemarathonEntity];
                dto.TelemarathonActive = telemarathon.IsActive;
                dto.NarrativeMode = (int)telemarathon.Mode;
                dto.MediaTrust = telemarathon.Trust;
                dto.IsInShock = telemarathon.IsInShock;
                dto.ShockHoursRemaining = telemarathon.ShockHoursRemaining;
                dto.AudienceFatigue = telemarathon.AudienceFatigue;
            }

            // IPSO state
            if (m_IpsoQuery.TryGetSingletonEntity<IPSOState>(out var ipsoEntity))
            {
                var ipso = m_IpsoLookup[ipsoEntity];
                dto.IpsoActive = ipso.IsActive;
                dto.IpsoIntensity = (int)Math.Round(ipso.GlobalExposure * 100f);
                dto.IpsoDistrictCount = ipso.AffectedDistrictCount;
                dto.IpsoTotalDistricts = ipso.TotalDistrictCount;
            }

            // Propaganda Center: built/online (the unlock gate), broadcast-capacity budget,
            // telecom reach, and the placement/upgrade request results for the UI shell to surface.
            var centerCfg = BalanceConfig.Current?.Cognitive;
            dto.CenterMaxTier = centerCfg?.PropagandaCenterMaxTier ?? FALLBACK_CENTER_MAX_TIER;
            dto.CenterCost = centerCfg?.PropagandaCenterCost ?? FALLBACK_CENTER_COST;
            dto.CenterUpgradeCost = centerCfg?.PropagandaCenterUpgradeCost ?? FALLBACK_CENTER_UPGRADE_COST;
            if (hasCenter)
            {
                dto.CenterBuilt = centerState.IsBuilt;
                dto.CenterOnline = centerState.IsOnline;
                dto.CenterTier = centerState.UpgradeTier;
                dto.BroadcastCapacity = centerState.BroadcastCapacity;
                dto.BroadcastCapacityFree = centerState.CapacityFree;
                dto.CenterReach = centerState.CenterReach;
                dto.TelecomFactor = centerState.TelecomFactor;
                // Phase 13A — coverage-capacity in households + the current allocation split.
                dto.CoverageCapacityHouseholds = (int)Math.Round(centerState.CoverageCapacityHouseholds);
                dto.AllocWeightPoor = centerState.AllocWeightPoor;
                dto.AllocWeightMiddle = centerState.AllocWeightMiddle;
                dto.AllocWeightWealthy = centerState.AllocWeightWealthy;
            }
            dto.PropagandaCenterPlacementRequestJson = RequestResultBridge.Get(RequestResultBridge.PropagandaCenterPlacement).ToJson();
            dto.PropagandaCenterUpgradeRequestJson = RequestResultBridge.Get(RequestResultBridge.PropagandaCenterUpgrade).ToJson();

            // Info-war after-action report: snapshot the derived consequence readouts (now that the
            // dto carries them) and drive the modal coordinator. Writes dto.ShowPsyDebrief + the frozen
            // snapshot fields the PsyDebriefingModal reads.
            UpdatePsyDebriefingModal(ref dto);

            PublishWhenComplete(B.CognitiveState, NoSourceChecks, () => dto);
        }

        /// <summary>
        /// Aggregate the raids of the newest wave that has just finished landing into the after-action
        /// snapshot. A psy raid lands ~6 game-hours after launch, long past its PsyAttack phase, so the
        /// trigger is "every raid of wave N has left InFlight" (≥1 landed, 0 still incoming) — not a
        /// phase edge. <see cref="m_LastPsyDebriefWave"/> dedupes so each wave briefs exactly once. Only
        /// the raid-derived aggregates are captured here (the attack snapshot is live in this scope);
        /// the consequence-readout snapshot + the modal show are deferred to
        /// <see cref="UpdatePsyDebriefingModal"/> once the dto is fully built.
        /// </summary>
        private void DetectPsyDebriefWave(NativeArray<PsyOpsAttack> attacks)
        {
            if (!attacks.IsCreated || attacks.Length == 0) return;

            // Newest wave with at least one landed (or landed-then-expiring-this-frame) raid.
            int candidateWave = -1;
            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                if (a.Phase >= PsyOpsAttackPhase.Landed && a.Wave > candidateWave)
                    candidateWave = a.Wave;
            }
            if (candidateWave <= m_LastPsyDebriefWave) return; // already briefed, or nothing landed yet

            // Hold the report until the whole wave has arrived — a raid of this wave still in flight
            // means the after-action picture is incomplete.
            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                if (a.Wave == candidateWave && a.Phase == PsyOpsAttackPhase.InFlight)
                    return;
            }

            int raids = 0, propaganda = 0, fakeVideo = 0, rumor = 0, blunted = 0, strataMask = 0;
            float peak = 0f;
            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                if (a.Wave != candidateWave || a.Phase < PsyOpsAttackPhase.Landed) continue;
                raids++;
                switch (a.Type)
                {
                    case PsyOpsAttackType.Propaganda: propaganda++; break;
                    case PsyOpsAttackType.FakeVideo: fakeVideo++; break;
                    case PsyOpsAttackType.Rumor: rumor++; break;
                    default: break; // a future carrier still counts in RaidCount, just not per-type
                }
                if (a.LandedBlunted) blunted++;
                strataMask |= StratumBit(a.Target);
                if (a.HasSpread && a.SecondaryTarget != SocialStratum.Unknown)
                    strataMask |= StratumBit(a.SecondaryTarget);
                if (a.Intensity > peak) peak = a.Intensity;
            }
            if (raids == 0) return; // defensive — candidateWave had a landed raid, so unreachable

            m_PsyDebriefWave = candidateWave;
            m_PsyDebriefRaidCount = raids;
            m_PsyDebriefPropaganda = propaganda;
            m_PsyDebriefFakeVideo = fakeVideo;
            m_PsyDebriefRumor = rumor;
            m_PsyDebriefBlunted = blunted;
            m_PsyDebriefStrataMask = strataMask;
            m_PsyDebriefPeakIntensity = peak;
            m_LastPsyDebriefWave = candidateWave;
            // Raid facts are frozen, but the report waits for the landed window to close — the
            // infected-count baseline is captured from this frame's dto (UpdatePsyDebriefingModal),
            // and the delta read at close is the damage line the modal shows.
            m_PsyDebriefAwaitingClose = true;
            m_PsyDebriefBaselinePending = true;
        }

        /// <summary>
        /// Second stage of the after-action trigger: the report shows only once the briefed wave's
        /// landed window has CLOSED (no attack of that wave left alive — expired entities are
        /// destroyed by the launcher). A landed raid presses for its whole window, so the close-time
        /// infected delta is the first honest "what it did" read. An empty snapshot counts as
        /// closed — the last attack's destroy can empty the query in one pass.
        /// </summary>
        private void DetectPsyDebriefWindowClose(NativeArray<PsyOpsAttack> attacks)
        {
            if (!m_PsyDebriefAwaitingClose)
                return;

            if (attacks.IsCreated)
            {
                for (int i = 0; i < attacks.Length; i++)
                {
                    var a = attacks[i];
                    if (a.Wave == m_PsyDebriefWave && a.Phase != PsyOpsAttackPhase.Expired)
                        return; // window still open — an attack of this wave is in flight or landed
                }
            }

            m_PsyDebriefAwaitingClose = false;
            m_PsyDebriefPending = true;
        }

        // Poor(1)/Middle(2)/Wealthy(3) → bit 1/2/4; Unknown (no classified stratum) → no bit.
        private static int StratumBit(SocialStratum s) =>
            s is SocialStratum.Poor or SocialStratum.Middle or SocialStratum.Wealthy
                ? 1 << ((int)s - 1)
                : 0;

#pragma warning disable CIVIC098 // ModalCoordinator.Instance is static readonly = new(), never null
        /// <summary>
        /// Snapshot the derived consequence readouts (media trust, households under impact, food-run) the
        /// moment a report is pending, then drive the modal coordinator — an exact mirror of the kinetic
        /// debriefing's show / auto-dismiss / save-load-wraparound handling in ThreatUISystem. Writes the
        /// dto fields the PsyDebriefingModal reads.
        /// </summary>
        private void UpdatePsyDebriefingModal(ref CognitiveDto dto)
        {
            // Damage baseline: the city infected count the moment the wave finished landing. The
            // close-time read minus this is the strike's dose (ambient IPSO can contaminate the
            // delta, but over a landed window the raid channel dominates — an honest estimate).
            if (m_PsyDebriefBaselinePending)
            {
                m_PsyDebriefBaselinePending = false;
                m_PsyDebriefBaselineInfected = dto.HouseholdsInfected;
            }

            if (m_PsyDebriefPending)
            {
                m_PsyDebriefPending = false;
                m_PsyDebriefMediaTrust = dto.MediaTrust;
                m_PsyDebriefHouseholdsImpact = dto.HouseholdsUnderImpact;
                m_PsyDebriefFoodRunActive = dto.RumorFoodRunIntensity > 0f;
                m_PsyDebriefInfectedDelta = Math.Max(0, dto.HouseholdsInfected - m_PsyDebriefBaselineInfected);
                m_ShowPsyDebrief = true;
                m_PsyDebriefVisibleInCoordinator = false;

                // Telemetry mirror of the after-action modal — published once per debriefed wave
                // (the pending latch dedupes), with the same frozen numbers the player sees.
                EventBus?.SafePublish(new PsyWaveResultEvent(
                    m_PsyDebriefWave, m_PsyDebriefRaidCount, m_PsyDebriefPropaganda,
                    m_PsyDebriefRumor, m_PsyDebriefFakeVideo, m_PsyDebriefBlunted,
                    m_PsyDebriefStrataMask, m_PsyDebriefPeakIntensity, m_PsyDebriefMediaTrust,
                    m_PsyDebriefHouseholdsImpact, m_PsyDebriefFoodRunActive), nameof(CognitiveUISystem));
            }

            // Save/load time wraparound: ElapsedTime resets to ~0 while the shown-time stamp keeps its
            // old value → auto-dismiss would never fire. Drop the modal (mirror of the kinetic guard).
            if (m_ShowPsyDebrief && m_PsyDebriefShownTime > SystemAPI.Time.ElapsedTime)
            {
                m_ShowPsyDebrief = false;
                m_PsyDebriefVisibleInCoordinator = false;
                ModalCoordinator.Instance.Dismiss("PsyDebriefing");
            }

            if (m_ShowPsyDebrief && m_PsyDebriefVisibleInCoordinator && ModalCoordinator.Instance.ActiveId != "PsyDebriefing")
                m_PsyDebriefVisibleInCoordinator = false;

            if (m_ShowPsyDebrief && !m_PsyDebriefVisibleInCoordinator && ModalCoordinator.Instance.TryShow("PsyDebriefing"))
            {
                m_PsyDebriefVisibleInCoordinator = true;
#pragma warning disable CIVIC034 // Transient UI timer — compared with ElapsedTime, not persisted
                m_PsyDebriefShownTime = SystemAPI.Time.ElapsedTime;
#pragma warning restore CIVIC034
            }

            if (m_ShowPsyDebrief
                && m_PsyDebriefVisibleInCoordinator
                && SystemAPI.Time.ElapsedTime - m_PsyDebriefShownTime >= PSY_DEBRIEF_MIN_DISPLAY)
            {
                m_ShowPsyDebrief = false;
                m_PsyDebriefVisibleInCoordinator = false;
                ModalCoordinator.Instance.Dismiss("PsyDebriefing");
            }

            dto.ShowPsyDebrief = m_ShowPsyDebrief;
            dto.PsyDebriefWave = m_PsyDebriefWave;
            dto.PsyDebriefRaidCount = m_PsyDebriefRaidCount;
            dto.PsyDebriefPropaganda = m_PsyDebriefPropaganda;
            dto.PsyDebriefFakeVideo = m_PsyDebriefFakeVideo;
            dto.PsyDebriefRumor = m_PsyDebriefRumor;
            dto.PsyDebriefBlunted = m_PsyDebriefBlunted;
            dto.PsyDebriefStrataMask = m_PsyDebriefStrataMask;
            dto.PsyDebriefPeakIntensity = m_PsyDebriefPeakIntensity;
            dto.PsyDebriefMediaTrust = m_PsyDebriefMediaTrust;
            dto.PsyDebriefHouseholdsImpact = m_PsyDebriefHouseholdsImpact;
            dto.PsyDebriefFoodRunActive = m_PsyDebriefFoodRunActive;
            dto.PsyDebriefInfectedDelta = m_PsyDebriefInfectedDelta;

            dto.ShowPsyInbound = m_ShowPsyInbound;
            dto.PsyInboundWave = m_PsyInboundWave;
            dto.PsyInboundCount = m_PsyInboundCount;
            dto.PsyInboundType = m_PsyInboundType;
            dto.PsyInboundStratum = m_PsyInboundStratum;
            dto.PsyInboundEtaHours = m_PsyInboundEtaHours;
            dto.PsyInboundCounterReady = m_PsyInboundCounterReady;
        }

        private void OnDismissPsyDebriefing() => ClearDebriefingAlert();

        /// <summary>
        /// Raise the inbound alert for a wave whose raid is still in the air. Reads the carrier and
        /// the seam through the SAME intel fog as the contact telegraph: unread ones are reported as
        /// unknown (-1 / Unknown), never guessed. Fires once per wave; auto-clears when nothing is
        /// in flight any more, because the counter-play it describes has expired.
        /// </summary>
        private void DetectPsyInboundWave(float currentHour, in HeroDeploymentState heroState, NativeArray<PsyOpsAttack> attacks)
        {
            if (!attacks.IsCreated || attacks.Length == 0)
            {
                // No live raids at all (e.g. a new city with none launched yet). Same shut-window
                // meaning as the inFlight == 0 branch below, so drop any carried-over modal too —
                // a stale alert from the previous city must not survive the empty query.
                m_PsyInboundCount = 0;
                ClearInboundAlert();
                return;
            }

            bool hasInsider = false;
            int intelLevel = 0;
            if (SystemAPI.TryGetSingleton<IntelStateSingleton>(out var intel))
            {
                hasInsider = intel.HasInsider;
                intelLevel = intel.IntelUpgradeLevel;
            }

            int inFlight = 0;
            int wave = -1;
            int type = -1;
            int stratum = 0;
            float eta = 0f;

            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                if (a.Phase != PsyOpsAttackPhase.InFlight)
                    continue;

                inFlight++;
                float etaHours = Math.Max(0f, a.LandTimeHours - currentHour);
                int fog = ComputePsyOpsFog(a.Phase, etaHours, hasInsider, intelLevel);

                // The dominant carrier is the one the alert names; a secondary leak does not
                // overwrite it (the player answers the dominant, the leak rides along).
                if (wave < 0 || a.IsDominant)
                {
                    wave = a.Wave;
                    eta = etaHours;
                    type = fog >= FOG_DETECTED ? (int)a.Type : -1;
                    stratum = fog >= FOG_REVEALED ? (int)a.Target : (int)SocialStratum.Unknown;
                }
            }

            m_PsyInboundCount = inFlight;

            if (inFlight == 0)
            {
                // Landed / expired: the window is shut, so the advice is void. Drop the modal even
                // if the player never acknowledged it — the after-action report takes over.
                ClearInboundAlert();
                return;
            }

            // Live counter-readiness: a deployed speaker whose archetype hard-counters this carrier.
            // Unknown carrier → unknown answer (false), which is honest rather than reassuring.
            bool heroActive = heroState.HeroStatus != HeroStatusEnum.Inactive;
            m_PsyInboundCounterReady = heroActive && type >= 0
                && StratumDefense.Counters(heroState.Archetype, (PsyOpsAttackType)type);

            m_PsyInboundWave = wave;
            m_PsyInboundType = type;
            m_PsyInboundStratum = stratum;
            m_PsyInboundEtaHours = eta;

            // Arm the alert for a not-yet-surfaced wave, but DON'T advance the dedupe latch here — a
            // raid that only wanted to show is not "briefed". The coordinator can hold this back
            // (a higher-priority PsyDebriefing owns the slot); while it does, this block re-arms each
            // update and the TryShow below keeps retrying. The latch advances only once the modal
            // actually reaches the player (real show, below), so a wave whose inbound window closed
            // — coordinator busy until the raid lands — never falsely counts as shown and never
            // suppresses itself.
            if (wave > m_LastPsyInboundWave)
            {
                m_ShowPsyInbound = true;
                m_PsyInboundVisibleInCoordinator = false;
            }

            if (m_ShowPsyInbound && m_PsyInboundVisibleInCoordinator && ModalCoordinator.Instance.ActiveId != "PsyRaidInbound")
                m_PsyInboundVisibleInCoordinator = false;

            if (m_ShowPsyInbound && !m_PsyInboundVisibleInCoordinator && ModalCoordinator.Instance.TryShow("PsyRaidInbound"))
            {
                m_PsyInboundVisibleInCoordinator = true;
                m_LastPsyInboundWave = wave; // dedupe only after the player has actually seen it
            }
        }

        private void OnDismissPsyInbound() => ClearInboundAlert();

        /// <summary>
        /// Drop the after-action report and clear its coordinator slot. Idempotent
        /// (ModalCoordinator.Dismiss is a no-op when the id is not active), so it serves the dismiss
        /// trigger and the post-load reset alike.
        /// </summary>
        private void ClearDebriefingAlert()
        {
            m_ShowPsyDebrief = false;
            m_PsyDebriefVisibleInCoordinator = false;
            ModalCoordinator.Instance.Dismiss("PsyDebriefing");
        }

        /// <summary>
        /// Drop the inbound alert and clear its coordinator slot. Idempotent (the Dismiss is a no-op
        /// when the id is not active), so the dismiss trigger, the shut-window auto-close (empty
        /// query and nothing-in-flight alike), and the post-load reset all share this one path.
        /// </summary>
        private void ClearInboundAlert()
        {
            m_ShowPsyInbound = false;
            m_PsyInboundVisibleInCoordinator = false;
            ModalCoordinator.Instance.Dismiss("PsyRaidInbound");
        }

        /// <summary>
        /// Re-arm the info-war telegraph and after-action report on load. Load-over-running-game
        /// reuses this system instance and does NOT call OnCreate/OnStartRunning (same reason the
        /// kinetic ThreatUISystem re-arms in its own ValidateAfterLoad), so the per-wave dedupe
        /// latches and the transient modal state would carry the PREVIOUS city's values into the
        /// new one. PsyOpsAttack.Wave is per-city and restarts low each save, so a stale high-water
        /// latch silently gates BOTH modals off until the new city out-waves the old one. Reset the
        /// latches to their declared start and drop both modals. The static ModalCoordinator is
        /// itself reset by PLVS before this runs; these Dismiss calls are the instance-side half so
        /// the show/visible flags cannot re-raise a carried-over modal on the next update. PLVS
        /// calls this on every load.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_LastPsyDebriefWave = -1;
            m_LastPsyInboundWave = -1;
            m_PsyDebriefPending = false;
            m_PsyDebriefShownTime = 0;
            m_PsyInboundCount = 0;
            ClearDebriefingAlert();
            ClearInboundAlert();
        }
#pragma warning restore CIVIC098

        private void UpdateLookups()
        {
            m_StateLookup.Update(this);
            m_SpotterCmLookup.Update(this);
            m_StatsLookup.Update(this);
            m_TelemarathonLookup.Update(this);
            m_IpsoLookup.Update(this);
            m_InternetDisabledBufferLookup.Update(this);
            m_IntegrityBufferLookup.Update(this);
            m_DistrictEntityBufferLookup.Update(this);
        }

        /// <summary>
        /// Build entity index → Entity map from DistrictEntityEntry buffer (one pass).
        /// Called on cache refresh so ResolveDistrictName does O(1) lookup instead of O(N) scan.
        /// </summary>
        private void RebuildEntityLookup()
        {
            m_EntityLookup.Clear();
            m_DistrictKeyByIndex.Clear();
            if (m_DistrictPowerQuery.TryGetSingletonEntity<DistrictPowerBufferSingleton>(out var powerSingleton)
                && m_DistrictEntityBufferLookup.HasBuffer(powerSingleton))
            {
                var entityBuffer = m_DistrictEntityBufferLookup[powerSingleton];
                for (int i = 0; i < entityBuffer.Length; i++)
                {
                    var districtRef = entityBuffer[i].District;
                    var entity = districtRef.ToEntity();
                    if (World.EntityManager.Exists(entity))
                    {
                        long districtKey = districtRef.Packed;
                        m_EntityLookup[districtKey] = entity;
                        m_DistrictKeyByIndex[districtRef.Index] = districtKey;
                    }
                }
            }
        }

        /// <summary>
        /// Lazy name resolution via cached entity lookup (O(1) per district).
        /// </summary>
        private string? ResolveDistrictName(int districtIndex)
        {
            // Require throws when the facade is missing — no null path to guard.
            m_NameService ??= ServiceRegistry.Instance.Require<NameSystemFacade>();

            // FIX L10: Validate entity still exists (district may have been deleted/recycled)
            if (m_DistrictKeyByIndex.TryGetValue(districtIndex, out var districtKey)
                && m_EntityLookup.TryGetValue(districtKey, out var entity)
                && World.EntityManager.Exists(entity))
            {
                string name = m_NameService.GetRenderedLabelName(entity);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return null;
        }

        private bool IsCachedDistrictNameStillValid(long districtKey)
        {
            return m_EntityLookup.TryGetValue(districtKey, out var entity)
                && World.EntityManager.Exists(entity);
        }

        private static int CalculateProtestRisk(float avgIntegrity)
        {
            if (avgIntegrity > QUESTIONING_THRESHOLD) return 0;
            if (avgIntegrity > 0.5f) return 1;
            if (avgIntegrity > ANGER_THRESHOLD) return 2;
            return 3;
        }

        private static string GetDominantNarrative(float avgIntegrity, bool isActive)
        {
            if (!isActive)
                return "All quiet on the information front.";

            if (avgIntegrity > VIGILANT_THRESHOLD)
                return "People are vigilant but calm.";
            if (avgIntegrity > QUESTIONING_THRESHOLD)
                return "Some are questioning official statements...";
            if (avgIntegrity > 0.5f)
                return "\"Why is the government hiding the truth?!\"";
            if (avgIntegrity > ANGER_THRESHOLD)
                return "\"How dare you turn off the Wi-Fi?!\"";
            if (avgIntegrity > 0.1f)
                return "\"The mayor is a puppet! Wake up, sheeple!\"";

            return "\"BURN IT ALL DOWN! STORM THE CITY HALL!\"";
        }

        private void FillHeroEligibility(ref CognitiveDto dto)
        {
            m_HeroDeploymentSystem ??= World.GetExistingSystemManaged<HeroDeploymentSystem>();
            if (m_HeroDeploymentSystem == null)
            {
                dto.CanDeployHero = false;
                dto.DeployHeroLockedReasonId = ReasonIds.HeroSystemUnavailable;
                dto.CanRecallHero = false;
                dto.RecallHeroLockedReasonId = ReasonIds.HeroSystemUnavailable;
                dto.CanSetHeroCounter = false;
                dto.SetHeroCounterLockedReasonId = ReasonIds.HeroSystemUnavailable;
                dto.CanSetHeroLecturing = false;
                dto.SetHeroLecturingLockedReasonId = ReasonIds.HeroSystemUnavailable;
                return;
            }

            dto.CanDeployHero = m_HeroDeploymentSystem.CanDeployHero(HeroStatusEnum.Deployed, out var deployReason);
            dto.DeployHeroLockedReasonId = deployReason;
            // Unlock gate: a hero cannot be deployed without an online Propaganda Center.
            if (dto.CanDeployHero && !IsCenterOnline())
            {
                dto.CanDeployHero = false;
                dto.DeployHeroLockedReasonId = ReasonIds.CenterRequired;
            }
            dto.CanRecallHero = m_HeroDeploymentSystem.CanRecallHero(out var recallReason);
            dto.RecallHeroLockedReasonId = recallReason;
            dto.CanSetHeroCounter = m_HeroDeploymentSystem.CanSetHeroMode(HeroStatusEnum.Deployed, out var counterReason);
            dto.SetHeroCounterLockedReasonId = counterReason;
            dto.CanSetHeroLecturing = m_HeroDeploymentSystem.CanSetHeroMode(HeroStatusEnum.Lecturing, out var lecturingReason);
            dto.SetHeroLecturingLockedReasonId = lecturingReason;
        }

        private TriggerOutcome OnDeployHero(in ScenarioGuard guard, int mode)
        {
            if (!TryMapHeroMode(mode, allowInactive: false, out var heroMode))
            {
                Log.Warn($"DeployHero rejected: invalid mode {mode}");
                return TriggerOutcome.Reject(ReasonIds.HeroInvalidMode);
            }

            // Unlock gate: heroes are counter-propaganda — the Center must be online.
            if (!IsCenterOnline())
            {
                Log.Info("DeployHero rejected: Propaganda Center not online");
                return TriggerOutcome.Reject(ReasonIds.CenterRequired);
            }

            // No pause reject here, by design — the consumer runs in ModificationEnd and
            // pays synchronously through BudgetTransactionResolver (AXIOM 14).
            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, new HeroActionRequest
            {
                Action = HeroActionType.Deploy,
                Mode = heroMode
            });
            Log.Info($"Created HeroActionRequest: Deploy {heroMode}");
            return TriggerOutcome.HandOffToEcs(ecb, entity, SystemAPI.Time.ElapsedTime, TriggerOutcome.CurrentSimulationFrame(World));
        }

        private TriggerOutcome OnRecallHero()
        {
            // No pause reject here, by design — the consumer runs in ModificationEnd (AXIOM 14).
            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, new HeroActionRequest
            {
                Action = HeroActionType.Recall,
                Mode = HeroStatusEnum.Inactive
            });
            Log.Info("Created HeroActionRequest: Recall");
            return TriggerOutcome.HandOffToEcs(ecb, entity, SystemAPI.Time.ElapsedTime, TriggerOutcome.CurrentSimulationFrame(World));
        }

        private TriggerOutcome OnSetHeroMode(in ScenarioGuard guard, int mode)
        {
            if (!TryMapHeroMode(mode, allowInactive: true, out var heroMode))
            {
                Log.Warn($"SetHeroMode rejected: invalid mode {mode}");
                return TriggerOutcome.Reject(ReasonIds.HeroInvalidMode);
            }

            // No pause reject here, by design — the consumer runs in ModificationEnd and
            // pays synchronously through BudgetTransactionResolver (AXIOM 14).
            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, new HeroActionRequest
            {
                Action = HeroActionType.SetMode,
                Mode = heroMode
            });
            Log.Info($"Created HeroActionRequest: SetMode {heroMode}");
            return TriggerOutcome.HandOffToEcs(ecb, entity, SystemAPI.Time.ElapsedTime, TriggerOutcome.CurrentSimulationFrame(World));
        }

        private TriggerOutcome OnSetNarrativeMode(int mode)
        {
            if (!TryMapNarrativeMode(mode, out var narrativeMode))
            {
                Log.Warn($"SetNarrativeMode rejected: invalid mode {mode}");
                return TriggerOutcome.Reject(ReasonIds.CognitiveInvalidNarrativeMode);
            }

            m_TelemarathonSystem ??= World.GetExistingSystemManaged<TelemarathonSystem>();
            if (m_TelemarathonSystem == null)
            {
                Log.Warn("SetNarrativeMode rejected: TelemarathonSystem unavailable");
                return TriggerOutcome.Reject(ReasonIds.GenericSingletonNotReady);
            }

            if (!m_TelemarathonSystem.TrySetMode(narrativeMode, out var reasonId))
                return TriggerOutcome.Reject(reasonId);

            Log.Info($"SetNarrativeMode applied synchronously: {narrativeMode}");
            return TriggerOutcome.SyncSuccess();
        }

        private TriggerOutcome OnSetTelemarathonActive(bool active)
        {
            // Unlock gate: Telemarathon is a broadcast countermeasure — starting it requires
            // an online Center. Stopping it (active == false) is always allowed.
            if (active && !IsCenterOnline())
            {
                Log.Info("SetTelemarathonActive rejected: Propaganda Center not online");
                return TriggerOutcome.Reject(ReasonIds.CenterRequired);
            }

            m_TelemarathonSystem ??= World.GetExistingSystemManaged<TelemarathonSystem>();
            if (m_TelemarathonSystem == null)
            {
                Log.Warn("SetTelemarathonActive rejected: TelemarathonSystem unavailable");
                return TriggerOutcome.Reject(ReasonIds.GenericSingletonNotReady);
            }

            if (!m_TelemarathonSystem.TrySetActive(active, out var reasonId))
                return TriggerOutcome.Reject(reasonId);

            Log.Info($"SetTelemarathonActive applied synchronously: {active}");
            return TriggerOutcome.SyncSuccess();
        }

        private TriggerOutcome OnSetInternetMode(in ScenarioGuard guard, int mode)
        {
            if (!TryMapInternetMode(mode, out var requestMode))
            {
                Log.Warn($"SetInternetMode rejected: invalid mode {mode}");
                return TriggerOutcome.Reject(ReasonIds.CognitiveInvalidMode);
            }

            m_CognitiveStateSystem ??= World.GetExistingSystemManaged<CognitiveStateSystem>();
            if (m_CognitiveStateSystem == null)
            {
                Log.Warn("SetInternetMode rejected: CognitiveStateSystem unavailable");
                return TriggerOutcome.Reject(ReasonIds.GenericSingletonNotReady);
            }

            if (!m_CognitiveStateSystem.TrySetInternetMode(requestMode, out var reasonId))
                return TriggerOutcome.Reject(reasonId);

            Log.Info($"SetInternetMode applied synchronously: {requestMode}");
            return TriggerOutcome.SyncSuccess();
        }

        /// <summary>True when a Propaganda Center is online — the unlock condition for the
        /// counter-propaganda actions (heroes + Telemarathon). Buckwheat (physical aid) is not gated.</summary>
        private bool IsCenterOnline()
            => m_CenterQuery.TryGetSingleton<PropagandaCenterState>(out var center) && center.IsOnline;

        // Propaganda Center placement — activates the vanilla placement tool synchronously so building
        // works while the simulation is paused (Axiom 14, AA precedent). Prefab is fixed (the Telecenter
        // building .cok); mode 0 (budget-only). Supersede so a re-click cancels the prior pending tool.
        private TriggerOutcome OnPlacePropagandaCenter()
        {
            Log.Info("OnPlacePropagandaCenter called");
            return TriggerOutcome.Supersede(ReasonIds.CenterCancelled, token =>
                ActivateCenterPlacementImmediately(token));
        }

        private void ActivateCenterPlacementImmediately(RequestToken token)
        {
            var result = m_PlacementCommand.TryActivatePlacement(
                BuildingPlacementKind.PropagandaCenter,
                "Telecenter",
                (byte)0,
                token);

            if (!result.Activated)
                RequestResultBridge.Complete(token, RequestStatus.Failed, result.ReasonId.ToString());
        }

        // Propaganda Center upgrade — synchronous host-direct command (pause-safe, like SetHeroArchetype):
        // grows broadcast capacity by one tier at a budget cost. Growth = upgrades, not copies.
        private TriggerOutcome OnUpgradePropagandaCenter()
        {
            m_CenterSystem ??= World.GetExistingSystemManaged<PropagandaCenterSystem>();
            if (m_CenterSystem == null)
            {
                Log.Warn("UpgradePropagandaCenter rejected: PropagandaCenterSystem unavailable");
                return TriggerOutcome.Reject(ReasonIds.CenterUnavailable);
            }

            if (!m_CenterSystem.TryUpgradeCenter(out var reasonId))
                return TriggerOutcome.Reject(reasonId);

            Log.Info("UpgradePropagandaCenter applied synchronously");
            return TriggerOutcome.SyncSuccess();
        }

        private TriggerOutcome OnSetHeroArchetype(int archetype)
        {
            if (!TryMapHeroArchetype(archetype, out var heroArchetype))
            {
                Log.Warn($"SetHeroArchetype rejected: invalid archetype {archetype}");
                return TriggerOutcome.Reject(ReasonIds.HeroInvalidMode);
            }

            // Pause-safe live response (Axiom 14): re-posturing under the telegraph must work while
            // paused, so the switch commits synchronously on the UI thread (host-direct budget deduct)
            // instead of routing through the GameSimulation-phase request pipeline. Precedent:
            // SetInternetMode / IAAPlacementCommandService. Cost + cooldown still apply.
            m_HeroDeploymentSystem ??= World.GetExistingSystemManaged<HeroDeploymentSystem>();
            if (m_HeroDeploymentSystem == null)
            {
                Log.Warn("SetHeroArchetype rejected: HeroDeploymentSystem unavailable");
                return TriggerOutcome.Reject(ReasonIds.HeroSystemUnavailable);
            }

            if (!m_HeroDeploymentSystem.TrySetHeroArchetype(heroArchetype, out var reasonId))
                return TriggerOutcome.Reject(reasonId);

            Log.Info($"SetHeroArchetype applied synchronously: {heroArchetype}");
            return TriggerOutcome.SyncSuccess();
        }

        // Pause-safe host-direct coverage-allocation command (Axiom 14, mirrors SetHeroArchetype): split
        // the Center's household coverage ceiling across Poor/Middle/Wealthy. Args are permille (0..1000);
        // the backend re-normalizes to sum 1. Fire-and-forget — the allocation drill-down opens only for
        // an online Center, so there is no request-result surface to complete.
        private void OnSetBroadcastAllocation(int poorPermille, int middlePermille, int wealthyPermille)
        {
            m_CenterSystem ??= World.GetExistingSystemManaged<PropagandaCenterSystem>();
            if (m_CenterSystem == null)
            {
                Log.Warn("SetBroadcastAllocation rejected: PropagandaCenterSystem unavailable");
                return;
            }

            const float permille = 1000f;
            if (!m_CenterSystem.SetBroadcastAllocation(
                    poorPermille / permille, middlePermille / permille, wealthyPermille / permille, out var reasonId))
                Log.Info($"SetBroadcastAllocation rejected: {reasonId}");
        }

        private static bool TryMapHeroArchetype(int archetype, out HeroArchetype heroArchetype)
        {
            switch (archetype)
            {
                case 0:
                    heroArchetype = HeroArchetype.Voice;
                    return true;
                case 1:
                    heroArchetype = HeroArchetype.Arestovych;
                    return true;
                case 2:
                    heroArchetype = HeroArchetype.Patriot;
                    return true;
                default:
                    heroArchetype = default;
                    return false;
            }
        }

        private static bool TryMapHeroMode(int mode, bool allowInactive, out HeroStatusEnum heroMode)
        {
            switch (mode)
            {
                case 0 when allowInactive:
                    heroMode = HeroStatusEnum.Inactive;
                    return true;
                case 1:
                    heroMode = HeroStatusEnum.Deployed;
                    return true;
                case 2:
                    heroMode = HeroStatusEnum.Lecturing;
                    return true;
                default:
                    heroMode = default;
                    return false;
            }
        }

        private static bool TryMapNarrativeMode(int mode, out NarrativeMode narrativeMode)
        {
            switch (mode)
            {
                case 0:
                    narrativeMode = NarrativeMode.Soothing;
                    return true;
                case 1:
                    narrativeMode = NarrativeMode.Alarmist;
                    return true;
                case 2:
                    narrativeMode = NarrativeMode.Realistic;
                    return true;
                default:
                    narrativeMode = default;
                    return false;
            }
        }

        private static bool TryMapInternetMode(int mode, out GlobalInternetMode internetMode)
        {
            switch (mode)
            {
                case 0:
                    internetMode = GlobalInternetMode.Open;
                    return true;
                case 1:
                    internetMode = GlobalInternetMode.Firewall;
                    return true;
                case 2:
                    internetMode = GlobalInternetMode.Blackout;
                    return true;
                default:
                    internetMode = default;
                    return false;
            }
        }

        private static string SerializeCognitiveDistricts(List<CognitiveDistrictEntry> districts)
        {
            if (districts == null || districts.Count == 0) return JsonBuilder.EmptyArray;
            var sb = new StringBuilder(512);
            sb.Append('[');
            for (int i = 0; i < districts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                districts[i].WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// Build the per-stratum opinion lanes (Poor/Middle/Wealthy) from the read model.
        /// Always publishes all three lanes — the city's population is a fact from frame one, so the
        /// crowd data is never gated behind classification. During the resolver ramp the counts read
        /// zero (an honest early-city shape the UI renders as "classifying…"), not a fabricated crowd.
        /// </summary>
        private void BuildStrataLanes(in CognitiveStatsState stats, in PropagandaCenterState center, NativeArray<PsyOpsAttack> attacks, float currentHour)
        {
            m_StrataList.Clear();

            // Phase 13A — the combined coverage fraction (min of telecom signal and capacity) comes from
            // the shared coverage projection, the single owner of the combine, so covered-households here
            // never re-derives the math. Live enemy reach per stratum aggregates the live raids once —
            // in-flight AND already-landed-not-yet-expired, the same set the contact telegraph shows as
            // cards — under the same intel fog that telegraph uses (see ComputeEnemyReachByStratum).
            m_CoverageReader ??= ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullCognitiveCoverageReader.Instance);
            var coverage = m_CoverageReader.GetCoverage();

            bool hasInsider = false;
            int intelLevel = 0;
            if (SystemAPI.TryGetSingleton<IntelStateSingleton>(out var intel))
            {
                hasInsider = intel.HasInsider;
                intelLevel = intel.IntelUpgradeLevel;
            }
            ComputeEnemyReachByStratum(in stats, attacks, currentHour, hasInsider, intelLevel,
                out int reachPoor, out int reachMiddle, out int reachWealthy, out bool anyFogged);

            m_StrataList.Add(BuildStratumLane(SocialStratum.Poor, stats.PoorHouseholds, stats.PoorAvgInfection,
                stats.PoorAvgResistance, center.AllocWeightPoor, center.CoverageCapacityHouseholds, coverage, reachPoor,
                stats.PoorCoverageFraction, anyFogged));
            m_StrataList.Add(BuildStratumLane(SocialStratum.Middle, stats.MiddleHouseholds, stats.MiddleAvgInfection,
                stats.MiddleAvgResistance, center.AllocWeightMiddle, center.CoverageCapacityHouseholds, coverage, reachMiddle,
                stats.MiddleCoverageFraction, anyFogged));
            m_StrataList.Add(BuildStratumLane(SocialStratum.Wealthy, stats.WealthyHouseholds, stats.WealthyAvgInfection,
                stats.WealthyAvgResistance, center.AllocWeightWealthy, center.CoverageCapacityHouseholds, coverage, reachWealthy,
                stats.WealthyCoverageFraction, anyFogged));
        }

        /// <summary>
        /// Assemble one stratum lane with the Phase-13A households currency: allocated (weight × ceiling),
        /// covered (combined coverage fraction × households), and the live enemy-reach aggregate.
        /// </summary>
        private static CognitiveStratumEntry BuildStratumLane(
            SocialStratum stratum, int households, float infection, float resistance,
            float allocWeight, float capacityHouseholds, IReadOnlyList<StratumCoverage> coverage, int enemyReach,
            float signalFraction, bool hasFoggedReach)
        {
            int allocated = (int)Math.Round(allocWeight * capacityHouseholds);
            float coverFraction = CombinedCoverageFor(coverage, stratum);
            int covered = (int)Math.Round(coverFraction * households);
            return new CognitiveStratumEntry
            {
                Stratum = (int)stratum,
                Count = households,
                Infection = infection,
                Resistance = resistance,
                AllocatedHouseholds = allocated,
                CoveredHouseholds = covered,
                EnemyReachHouseholds = enemyReach,
                SignalCoverageFraction = Math.Clamp(signalFraction, 0f, 1f),
                HasFoggedReach = hasFoggedReach
            };
        }

        /// <summary>Combined coverage fraction (min of telecom signal and Phase-13A capacity) for a
        /// stratum from the shared projection; 0 if the projection has no entry yet.</summary>
        private static float CombinedCoverageFor(IReadOnlyList<StratumCoverage> coverage, SocialStratum stratum)
        {
            for (int i = 0; i < coverage.Count; i++)
                if (coverage[i].Stratum == stratum)
                    return coverage[i].CoverageFraction;
            return 0f;
        }

        /// <summary>
        /// Aggregate the live enemy reach into each stratum from the live <see cref="PsyOpsAttack"/>
        /// entities — the sum of the same 13C reach proxy (Intensity × vulnerability × households) over
        /// raids targeting that seam. "Live" is every raid the contact telegraph still shows as a card:
        /// in-flight PLUS already-landed ones that have not yet Expired (only Expired and inert shapes
        /// are skipped, exactly as <see cref="BuildPsyOpsContacts"/> does), so the lane total and the
        /// visible cards stay in step through a raid's whole landed window.
        /// One pass over the (small) raid set; 0 for a stratum under no raid.
        /// The attacks array is the shared per-update snapshot owned by OnPanelUpdate.
        /// Fog contract: a raid contributes to the sum only once its own fog reads REVEALED — the same
        /// gate the contact telegraph applies to its per-contact ReachHouseholds, so the lanes can never
        /// leak what the cards withhold. An unrevealed raid instead raises <paramref name="anyFogged"/>
        /// for ALL strata (its seam is itself fogged until REVEALED, so a per-stratum marker would leak
        /// the target); the UI renders that as an honest "+?" instead of a false calm zero.
        /// </summary>
        private void ComputeEnemyReachByStratum(in CognitiveStatsState stats, NativeArray<PsyOpsAttack> attacks,
            float currentHour, bool hasInsider, int intelLevel,
            out int poor, out int middle, out int wealthy, out bool anyFogged)
        {
            poor = 0; middle = 0; wealthy = 0; anyFogged = false;
            if (!attacks.IsCreated || attacks.Length == 0)
                return;
            var cog = BalanceConfig.Current?.Cognitive;
            if (cog == null)
                return;

            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                if (a.Phase == PsyOpsAttackPhase.Expired || (a.Seed == 0 && a.Intensity <= 0f && a.LandTimeHours <= 0f))
                    continue;
                float etaHours = Math.Max(0f, a.LandTimeHours - currentHour);
                if (ComputePsyOpsFog(a.Phase, etaHours, hasInsider, intelLevel) < FOG_REVEALED)
                {
                    anyFogged = true;
                    continue;
                }
                int r = EnemyReachProxy(a.Intensity, a.Target, in stats, cog);
                switch (a.Target)
                {
                    case SocialStratum.Poor: poor += r; break;
                    case SocialStratum.Middle: middle += r; break;
                    case SocialStratum.Wealthy: wealthy += r; break;
                    default: break; // Unknown / unclassified → no seam to aggregate
                }
            }
        }

        /// <summary>13C reach proxy: estimated households a strike of this intensity reaches in its target
        /// stratum (≈ Intensity × stratum vulnerability × stratum households). Shared by the per-contact
        /// telegraph and the per-stratum aggregate so the two never diverge.</summary>
        private static int EnemyReachProxy(float intensity, SocialStratum target, in CognitiveStatsState stats, CognitiveConfig cog)
        {
            int households = HouseholdsFor(target, in stats);
            float vuln = VulnMultForStratum(target, cog);
            return (int)Math.Round(Math.Clamp(intensity, 0f, 1f) * vuln * households);
        }

        /// <summary>
        /// Build the live discrete PSYOPS contacts from the in-flight <see cref="PsyOpsAttack"/>
        /// snapshot (taken once per panel update in OnPanelUpdate). Times are derived from absolute
        /// TotalGameHours stamps minus the current hour so the interception window ticks down each
        /// republish. Expired/inert contacts are skipped.
        ///
        /// Each contact is projected under an intel fog: the shared Intel state
        /// (<see cref="IntelStateSingleton"/>, insider / upgrade level — read cross-domain from Core
        /// per Axiom 5) plus proximity to land decide how much of the telegraph is revealed. The
        /// projection genuinely withholds fogged fields (carrier/seam/exact timing) so the client
        /// only receives what the player has paid to see — "pay for precision" == fog width.
        /// </summary>
        private void BuildPsyOpsContacts(float currentHour, in HeroDeploymentState heroState, in CognitiveStatsState stats, bool hasStats, bool arestovychSuppressed, NativeArray<PsyOpsAttack> attacks)
        {
            m_PsyOpsList.Clear();
            if (!attacks.IsCreated || attacks.Length == 0)
                return;

            // One insider / a higher upgrade level lifts the fog over BOTH the physical wave and this
            // mental telegraph — a single source, no separate cognitive-intel currency.
            bool hasInsider = false;
            int intelLevel = 0;
            if (SystemAPI.TryGetSingleton<IntelStateSingleton>(out var intel))
            {
                hasInsider = intel.HasInsider;
                intelLevel = intel.IntelUpgradeLevel;
            }

            // Phase 13C — loop-invariant hero-RPS inputs (the two honest signals are per-contact, but
            // the archetype / active-state / effective counter magnitudes are city-wide this frame).
            var cog = BalanceConfig.Current?.Cognitive;
            var archetype = heroState.Archetype;
            bool heroActive = heroState.HeroStatus != HeroStatusEnum.Inactive;
            float heroFloor = cog?.HeroCounterFloor ?? FALLBACK_HERO_COUNTER_FLOOR;
            // While an Arestovych debt-collapse window is open the strong counter drops to the floor
            // (the exposed lie loses its bite) — the shared helper keeps this in lockstep with
            // MentalHealthResolverSystem's effective value.
            float heroStrong = StratumDefense.EffectiveHeroCounterStrong(cog?.HeroCounterStrong ?? FALLBACK_HERO_COUNTER_STRONG, heroFloor, arestovychSuppressed);

            for (int i = 0; i < attacks.Length; i++)
            {
                var a = attacks[i];
                // Skip default-constructed/inert shapes and decayed contacts.
                if (a.Phase == PsyOpsAttackPhase.Expired || (a.Seed == 0 && a.Intensity <= 0f && a.LandTimeHours <= 0f))
                    continue;

                float windowHours = Math.Max(0f, a.InterceptWindowEndHours - currentHour);
                float windowTotal = Math.Max(0.001f, a.InterceptWindowEndHours - a.LaunchTimeHours);
                float windowFraction = Math.Min(1f, Math.Max(0f, windowHours / windowTotal));
                float etaHours = Math.Max(0f, a.LandTimeHours - currentHour);

                int fog = ComputePsyOpsFog(a.Phase, etaHours, hasInsider, intelLevel);
                bool detected = fog >= FOG_DETECTED;
                bool revealed = fog >= FOG_REVEALED;

                // Carrier hidden until Detected; exact seam withheld until Revealed; timing/strength
                // coarsened into a band while fogged. Dominance is a read the enemy only exposes to
                // a player with intel — off until Detected.
                float bucketStep = detected ? FOG_INTENSITY_BUCKET_DETECTED : FOG_INTENSITY_BUCKET_HIDDEN;
                float shownIntensity = revealed ? a.Intensity : BucketIntensity(a.Intensity, bucketStep);

                // Phase 13C — two honest signals, never withheld beyond the same fog that gates the
                // type/seam, never multiplied together:
                //  • hero shield = the exact RPS cut the resolver applies (× (1 − heroEff)):
                //    LANDED → the frozen land snapshot (the historical verdict of the strike — a
                //    later speaker switch must not rewrite it); IN-FLIGHT → recomputed live under
                //    the current posture (the actionable forecast), known once the carrier is
                //    Detected;
                //  • target coverage + reach households — the defence context + absolute reach into
                //    the seam — known once the seam is Revealed.
                bool landedPhase = a.Phase == PsyOpsAttackPhase.Landed; // landed ⇒ fog Revealed ⇒ detected
                float heroShield = 0f;
                int blunted = 0;
                int landedBy = -1;
                if (landedPhase)
                {
                    heroShield = a.LandedHeroShield;
                    blunted = a.LandedBlunted ? 1 : 0;
                    landedBy = a.LandedByArchetype;
                }
                else if (detected)
                {
                    heroShield = StratumDefense.HeroTypeEffectiveness(archetype, a.Type, heroActive, heroStrong, heroFloor);
                    // Blunted = the strong RPS branch (a real hard-counter), not the weak blanket floor.
                    blunted = heroShield > heroFloor + FORECAST_COVERAGE_GAP_EPSILON ? 1 : 0;
                }
                float targetCoverage = 0f;
                int reachHouseholds = 0;
                if (revealed && hasStats && cog != null)
                {
                    targetCoverage = CoverageFractionFor(a.Target, in stats);
                    reachHouseholds = EnemyReachProxy(a.Intensity, a.Target, in stats, cog);
                }

                m_PsyOpsList.Add(new CognitivePsyOpsEntry
                {
                    PsyOpsType = detected ? (int)a.Type : -1,
                    TargetStratum = revealed ? (int)a.Target : (int)SocialStratum.Unknown,
                    Phase = (int)a.Phase,
                    WindowHours = revealed ? windowHours : (float)Math.Ceiling(windowHours),
                    WindowFraction = windowFraction,
                    EtaHours = revealed ? etaHours : (float)Math.Ceiling(etaHours),
                    Intensity = shownIntensity,
                    IsDominant = detected && a.IsDominant,
                    FogState = fog,
                    HeroShield = heroShield,
                    Blunted = blunted,
                    TargetCoverage = targetCoverage,
                    ReachHouseholds = reachHouseholds,
                    LandedByArchetype = landedBy,
                    // Stable per-contact React key: the launch-frozen Seed (serialized), not the
                    // list index — survives reorder/expiry of neighbouring contacts.
                    ContactId = unchecked((int)a.Seed)
                });
            }
        }

        // ── Live-response intel fog ───────────────────────────────
        private const int FOG_HIDDEN = 0;
        private const int FOG_DETECTED = 1;
        private const int FOG_REVEALED = 2;
        // Proximity narrowing (game-hours to land): the telegraph sharpens as it nears its target even
        // without intel — within REVEAL it reads exact, within DETECT its carrier/coarse values surface.
        private const float FOG_PROXIMITY_REVEAL_HOURS = 1f;
        private const float FOG_PROXIMITY_DETECT_HOURS = 3f;
        // Intensity bucket steps applied while fogged (coarser the less you know).
        private const float FOG_INTENSITY_BUCKET_HIDDEN = 0.34f;
        private const float FOG_INTENSITY_BUCKET_DETECTED = 0.25f;
        // Floor for the bucket step so the coarsening division can never hit zero.
        private const float FOG_MIN_BUCKET_STEP = 1e-4f;

        /// <summary>
        /// Phase 13B — recompute the next raid's LIKELY carrier + seam ONE STEP AHEAD, RNG-free,
        /// gated by the shared intel fog. Mirrors the deterministic slice of the launcher's targeting
        /// WITHOUT calling PickDominantType/PickTargetStratum (both consume the save-seeded m_Random —
        /// previewing them would desync the real raid draw and the save stream):
        ///  • type = the LEAST-covered attack type from the cognitive coverage projection (unique min →
        ///    that type; a tie or no active hero → unknown), same min/epsilon logic as the launcher;
        ///  • seam = argmax StratumWeight over the per-stratum read model — surfaced as most-LIKELY,
        ///    because the real pick is weighted-random, not deterministic.
        /// Fog is intel-only (insider / upgrade level): a pre-launch forecast has no in-flight eta term.
        /// </summary>
        private void PopulateForecast(ref CognitiveDto dto, bool warActive, in CognitiveStatsState stats, bool hasStats)
        {
            // Off while the cognitive war is not live — there is no honest "next raid" to read.
            if (!warActive)
            {
                dto.ForecastType = -1;
                dto.ForecastStratum = (int)SocialStratum.Unknown;
                dto.ForecastFog = FOG_HIDDEN;
                return;
            }

            bool hasInsider = false;
            int intelLevel = 0;
            if (SystemAPI.TryGetSingleton<IntelStateSingleton>(out var intel))
            {
                hasInsider = intel.HasInsider;
                intelLevel = intel.IntelUpgradeLevel;
            }

            int fog = ComputeForecastFog(hasInsider, intelLevel);
            dto.ForecastFog = fog;
            // Type revealed at Detected+, seam only at Revealed (intel-gated fidelity).
            dto.ForecastType = fog >= FOG_DETECTED ? PredictForecastType() : -1;
            dto.ForecastStratum = fog >= FOG_REVEALED && hasStats
                ? (int)PredictForecastStratum(in stats)
                : (int)SocialStratum.Unknown;
        }

        /// <summary>Intel-only fog for the pre-launch forecast (no proximity term — nothing is in flight yet).</summary>
        private static int ComputeForecastFog(bool hasInsider, int intelLevel)
        {
            if (hasInsider || intelLevel >= 2)
                return FOG_REVEALED;
            if (intelLevel >= 1)
                return FOG_DETECTED;
            return FOG_HIDDEN;
        }

        /// <summary>
        /// Least-covered attack type from the shared coverage projection (the seam the launcher's
        /// gap-bias exploits). Unique min → that type; a tie or a flat projection (no active hero) → -1
        /// (unknown). RNG-free — no tiebreak draw, unlike PsyOpsLaunchSystem.PickDominantType.
        /// </summary>
        private int PredictForecastType()
        {
            m_CoverageReader ??= ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullCognitiveCoverageReader.Instance);
            var list = m_CoverageReader.GetCoverage();
            if (list.Count == 0)
                return -1;

            // Per-type covers are city-wide (identical on every stratum entry) — read entry 0.
            var c = list[0];
            float minCover = Math.Min(c.PropagandaCover, Math.Min(c.RumorCover, c.FakeVideoCover));
            float maxCover = Math.Max(c.PropagandaCover, Math.Max(c.RumorCover, c.FakeVideoCover));
            if (maxCover - minCover <= FORECAST_COVERAGE_GAP_EPSILON)
                return -1; // flat projection (no hero seam) → unknown

            int atMin = 0;
            int which = -1;
            if (c.PropagandaCover <= minCover + FORECAST_COVERAGE_GAP_EPSILON) { atMin++; which = (int)PsyOpsAttackType.Propaganda; }
            if (c.RumorCover <= minCover + FORECAST_COVERAGE_GAP_EPSILON) { atMin++; which = (int)PsyOpsAttackType.Rumor; }
            if (c.FakeVideoCover <= minCover + FORECAST_COVERAGE_GAP_EPSILON) { atMin++; which = (int)PsyOpsAttackType.FakeVideo; }
            return atMin == 1 ? which : -1; // unique least-covered → that type; tie → unknown
        }

        /// <summary>
        /// Most-likely target seam = argmax StratumWeight over the per-stratum read model (mirror of
        /// PsyOpsLaunchSystem.StratumWeight — base weight + infection bias, zero for an empty stratum),
        /// taking the max instead of the weighted-random pick. Ties / nothing classified → Unknown.
        /// </summary>
        private static SocialStratum PredictForecastStratum(in CognitiveStatsState stats)
        {
            int wPoor = ForecastStratumWeight(stats.PoorHouseholds, stats.PoorAvgInfection);
            int wMiddle = ForecastStratumWeight(stats.MiddleHouseholds, stats.MiddleAvgInfection);
            int wWealthy = ForecastStratumWeight(stats.WealthyHouseholds, stats.WealthyAvgInfection);
            int best = Math.Max(wPoor, Math.Max(wMiddle, wWealthy));
            if (best <= 0)
                return SocialStratum.Unknown; // nothing classified yet
            // First-max wins on a tie — deterministic, matches the "most-likely" surface.
            if (wPoor == best) return SocialStratum.Poor;
            if (wMiddle == best) return SocialStratum.Middle;
            return SocialStratum.Wealthy;
        }

        private static int ForecastStratumWeight(int count, float avgInfection)
        {
            if (count <= 0)
                return 0;
            int infectionBias = (int)Math.Round(Math.Clamp(avgInfection, 0f, 1f) * FORECAST_TARGET_INFECTION_WEIGHT_SCALE);
            return FORECAST_TARGET_BASE_WEIGHT + infectionBias;
        }

        // Phase 13C — per-stratum reads for the reach-households proxy (target-seam only; 0 when fogged).
        // if/else-if with an explicit final else (matches the StratumDefense / CognitiveCoverageCacheSystem
        // convention) — the Unknown/unclassified case folds to the neutral value without a silent-discard
        // switch arm (CIVIC019).
        private static float CoverageFractionFor(SocialStratum stratum, in CognitiveStatsState stats)
        {
            if (stratum == SocialStratum.Poor) return stats.PoorCoverageFraction;
            if (stratum == SocialStratum.Middle) return stats.MiddleCoverageFraction;
            if (stratum == SocialStratum.Wealthy) return stats.WealthyCoverageFraction;
            return 0f; // Unknown / unclassified → no coverage read
        }

        private static int HouseholdsFor(SocialStratum stratum, in CognitiveStatsState stats)
        {
            if (stratum == SocialStratum.Poor) return stats.PoorHouseholds;
            if (stratum == SocialStratum.Middle) return stats.MiddleHouseholds;
            if (stratum == SocialStratum.Wealthy) return stats.WealthyHouseholds;
            return 0; // Unknown / unclassified → no households
        }

        private static float VulnMultForStratum(SocialStratum stratum, CognitiveConfig cfg)
        {
            if (stratum == SocialStratum.Poor) return cfg.VulnMultPoor;
            if (stratum == SocialStratum.Middle) return cfg.VulnMultMiddle;
            if (stratum == SocialStratum.Wealthy) return cfg.VulnMultWealthy;
            return 1f; // Unknown / unclassified → neutral vulnerability
        }

        /// <summary>
        /// Fog tier over an incoming telegraph: the intel baseline (insider / upgrade level) lifted by
        /// proximity to land — the closer the strike, the clearer it reads even with no intel. A landed
        /// attack is fully revealed (you already see what struck).
        /// </summary>
        private static int ComputePsyOpsFog(PsyOpsAttackPhase phase, float etaHours, bool hasInsider, int intelLevel)
        {
            if (phase != PsyOpsAttackPhase.InFlight)
                return FOG_REVEALED;

            int fog;
            if (hasInsider || intelLevel >= 2)
                fog = FOG_REVEALED;
            else if (intelLevel >= 1)
                fog = FOG_DETECTED;
            else
                fog = FOG_HIDDEN;

            if (etaHours <= FOG_PROXIMITY_REVEAL_HOURS)
                return FOG_REVEALED;
            if (etaHours <= FOG_PROXIMITY_DETECT_HOURS && fog < FOG_DETECTED)
                fog = FOG_DETECTED;
            return fog;
        }

        /// <summary>Round an intensity up onto a coarse bucket step (0..1) — the fogged low/med/high read.</summary>
        private static float BucketIntensity(float intensity, float step)
        {
            float safeStep = Math.Max(FOG_MIN_BUCKET_STEP, step);
            float clamped = Math.Min(1f, Math.Max(0f, intensity));
            float bucketed = (float)(Math.Ceiling(clamped / safeStep) * safeStep);
            return Math.Min(1f, bucketed);
        }

        private static string SerializeCognitiveStrata(List<CognitiveStratumEntry> strata)
        {
            if (strata == null || strata.Count == 0) return JsonBuilder.EmptyArray;
            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < strata.Count; i++)
            {
                if (i > 0) sb.Append(',');
                strata[i].WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string SerializeCognitivePsyOps(List<CognitivePsyOpsEntry> contacts)
        {
            if (contacts == null || contacts.Count == 0) return JsonBuilder.EmptyArray;
            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < contacts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                contacts[i].WriteTo(sb);
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    // CognitiveDistrictEntry moved to Core/UI/DomainState/CognitiveDistrictEntry.cs
    // in C8; serialization goes through the generated WriteTo.
}
