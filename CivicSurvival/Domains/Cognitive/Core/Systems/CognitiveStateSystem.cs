using Game;
using Game.Areas;
using CivicSurvival.Core.Features.Wellbeing;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.Spotters;
using CivicSurvival.Core.Components.Domain.Cognitive;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Cognitive.Core.Systems
{
    /// <summary>
    /// Sole owner of CognitiveState singleton.
    /// Manages cognitive state - enemy propaganda degrades district cognitive integrity.
    ///
    /// Responsibilities:
    /// - District integrity tracking (infection/recovery per internet mode)
    /// - Synchronous global internet mode changes from UI
    /// - Hero unit (Gerda) operations: deploy/recall/mode switch (from UI)
    /// - Post-load penalty restoration for deployed hero
    ///
    /// Mechanics (Global Internet Mode):
    /// - OPEN: Full infection rate, no recovery (propaganda spreads freely)
    /// - FIREWALL: 30% infection, 50% recovery, -10% commerce (filtered traffic)
    /// - BLACKOUT: No infection, full recovery, -25% commerce (total isolation)
    ///
    /// Per-district ISOLATE (only in OPEN mode):
    /// - Can cut internet to specific districts for targeted protection
    /// - In FIREWALL/BLACKOUT modes, global setting overrides per-district
    ///
    /// PERF: Throttled to 500ms - cognitive changes are slow, no need for per-frame precision.
    /// UI mode changes write owner state synchronously and force the next throttled update.
    /// </summary>
    [SingletonOwner(typeof(CognitiveState))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    [ActIndependent]
    public partial class CognitiveStateSystem : ThrottledSystemBase, IResettable, IPostLoadValidation, ICivicSingletonOwner<CognitiveState>
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CognitiveStateSystem");

        private static PenaltySource InternetModeToPenalty(GlobalInternetMode mode) => mode switch
        {
            GlobalInternetMode.Open => PenaltySource.None,
            GlobalInternetMode.Firewall => PenaltySource.FirewallActive,
            GlobalInternetMode.Blackout => PenaltySource.InternetBlackout,
            _ => throw new System.ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        // FIX N2-01: Hysteresis band prevents oscillation spam near CompromiseThreshold.
        // Compromise at threshold (0.5), recovery requires threshold + band (0.6).
        private const float RECOVERY_HYSTERESIS_BAND = 0.1f;

        protected override int UpdateInterval => Engine.Timing.UPDATE_INTERVAL_500_MS;

        private EntityQuery m_StateQuery;
        private EntityQuery m_HeroStateQuery;
        private EntityQuery m_SpotterCmQuery;
        private BufferLookup<CognitiveIntegrityBuffer> m_CogIntegrityBufferLookup;
        private BufferLookup<InternetDisabledBuffer> m_InternetDisabledBufferLookup;

        private DistrictPenaltySystem m_PenaltySystem = null!;
        [System.NonSerialized] private CivicDependencyWire m_PenaltyWire = null!;
        [System.NonSerialized] private bool m_PendingDistrictSync;

        protected override void OnCreate()
        {
            base.OnCreate();

            CognitiveState.EnsureExists(EntityManager);

            m_StateQuery = GetEntityQuery(ComponentType.ReadWrite<CognitiveState>());
            m_HeroStateQuery = GetEntityQuery(ComponentType.ReadOnly<HeroDeploymentState>());
            m_SpotterCmQuery = GetEntityQuery(ComponentType.ReadOnly<SpotterCountermeasuresState>());
            m_CogIntegrityBufferLookup = GetBufferLookup<CognitiveIntegrityBuffer>(false);
            m_InternetDisabledBufferLookup = GetBufferLookup<InternetDisabledBuffer>(true);

            m_PenaltyWire = new CivicDependencyWire(nameof(CognitiveStateSystem));

            var eventBus = EventBus;
            if (eventBus == null)
            {
                Log.Error(" EventBus not available - war/district events won't work");
            }
            else
            {
                eventBus.Subscribe<WarStartedEvent>(OnWarStarted);
                eventBus.Subscribe<DistrictLifecycleEvent>(OnDistrictLifecycle);
                // FIX A1a: Force immediate recalculation on wave phase transitions
                eventBus.Subscribe<ThreatNarrativeEvent>(OnWavePhaseChanged);
            }

            // FIX M-98: Restore penalties immediately after load via IPostLoadValidation
            // instead of deferring to first DayChangedEvent (0–24h gap at high game speed)

            Log.Info(" Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            CognitiveState.EnsureExists(EntityManager);
            WirePenaltySystem();

            // Sync existing districts (in case events were fired before we subscribed)
            m_CogIntegrityBufferLookup.Update(this);
            SyncExistingDistricts();

            Log.Info(" Started running, dependencies wired");
        }

        /// <summary>
        /// Sync existing districts into CognitiveIntegrityBuffer.
        /// Called once on startup to catch districts that were created before we subscribed.
        /// Includes virtual "Unzoned Area" (index 0) plus currently alive district entities.
        /// </summary>
        private void SyncExistingDistricts()
        {
            if (!SystemAPI.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
            {
                Log.Warn(" SyncExistingDistricts: CognitiveState singleton not found!");
                return;
            }

            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var buffer)) return;

            // LOAD-INVARIANT: ValidateAfterLoad can run before GameTime activation.
            if (!GameTimeSystem.TryGetGameHours(out _))
            {
                Log.Warn("[CognitiveStateSystem] TimeProvider unavailable; district sync will retry");
                m_PendingDistrictSync = true;
                return;
            }

            var existingIndices = new NativeHashSet<int>(buffer.Length, Allocator.Temp);

            // Build set of already tracked districts
            Log.Info($" SyncExistingDistricts: existing buffer has {buffer.Length} entries");
            for (int i = 0; i < buffer.Length; i++)
            {
                existingIndices.Add(buffer[i].DistrictIndex);
                if (Log.IsDebugEnabled) Log.Debug($" Existing district index: {buffer[i].DistrictIndex}");
            }

            int added = 0;
            int skipped = 0;
            int totalDistricts = 0;

            void SyncIndex(int index)
            {
                totalDistricts++;

                if (!existingIndices.Contains(index))
                {
                    buffer.Add(new CognitiveIntegrityBuffer
                    {
                        DistrictIndex = index,
                        Integrity = 1.0f,
                        IsCompromised = false
                    });
                    added++;
                    ApplyActivePenaltiesToDistrict(index);

                    string name = DistrictUtils.GetFallbackName(index);
                    Log.Info($" Added district {index} ({name}) to buffer");
                }
                else
                {
                    skipped++;
                }
            }

            SyncIndex(DistrictUtils.UNZONED_AREA_INDEX);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<District>>()
                         .WithNone<Temp, Deleted>()
                         .WithEntityAccess())
            {
                SyncIndex(entity.Index);
            }

            if (existingIndices.IsCreated) existingIndices.Dispose();

            Log.Info($" SyncExistingDistricts DONE: totalDistricts={totalDistricts}, added={added}, skipped={skipped}, bufferFinal={buffer.Length}");
            m_PendingDistrictSync = false;
        }

        protected override void OnDestroy()
        {
            if (ServiceRegistry.IsInitialized)
            {
                var eventBus = EventBus;
                if (eventBus != null)
                {
                    eventBus.Unsubscribe<WarStartedEvent>(OnWarStarted);
                    eventBus.Unsubscribe<DistrictLifecycleEvent>(OnDistrictLifecycle);
                    eventBus.Unsubscribe<ThreatNarrativeEvent>(OnWavePhaseChanged);
                }
            }

            base.OnDestroy();
            Log.Info(" Destroyed");
        }

        protected override bool ShouldSkipUpdate()
        {
            if (!SystemAPI.TryGetSingleton<CognitiveState>(out var state))
                return true;

            // NOTE M19: Pre-war requests accumulate until IsActive=true, then batch-processed.
            // Acceptable: internet mode = last-write-wins (no cost), hero deploy = act-gated (PreWar → return false)
            if (!state.IsActive)
                return true;

            return false;
        }

        // Track previous internet mode for penalty updates
        private GlobalInternetMode m_LastInternetMode = GlobalInternetMode.Open;

        protected override void OnThrottledUpdate()
        {
            m_CogIntegrityBufferLookup.Update(this);
            m_InternetDisabledBufferLookup.Update(this);

            WirePenaltySystem();

            if (!GameTimeSystem.TryGetGameHours(out _))
            {
                Log.Warn("[CognitiveStateSystem] TimeProvider unavailable; throttled update skipped");
                m_PendingDistrictSync = true;
                return;
            }
            if (m_PendingDistrictSync)
                SyncExistingDistricts();

            if (!m_StateQuery.TryGetSingletonRW<CognitiveState>(out var stateRef))
                return;

            if (!SystemAPI.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return;

            // Read hero deployment state once for effective rate calculation.
            // HeroDeploymentSystem runs UpdateBefore CSS, so the value is current this tick.
            HeroDeploymentState heroState = m_HeroStateQuery.TryGetSingleton<HeroDeploymentState>(out var hs)
                ? hs : HeroDeploymentState.Default;

            var globalMode = stateRef.ValueRO.InternetMode;

            // Per-district ISOLATE only matters in OPEN mode
            NativeParallelHashSet<int> internetDisabledLookup = default;
            bool hasLookup = false;
            if (globalMode == GlobalInternetMode.Open)
            {
                internetDisabledLookup = BuildInternetDisabledLookup(out hasLookup);
            }

            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var integrityBuffer)) return;

            float deltaTimeHours = GameRate.HoursDelta(ThrottledDeltaSeconds);

            // Get effective rates (already account for global mode and hero status)
            float infectionRate = CognitiveRates.EffectiveInfectionRate(stateRef.ValueRO, heroState);
            float recoveryRate = CognitiveRates.EffectiveRecoveryRate(stateRef.ValueRO, heroState);

            // Process each district
            for (int i = 0; i < integrityBuffer.Length; i++)
            {
                var entry = integrityBuffer[i];

                // Determine if this district has internet access
                bool isIsolated = globalMode == GlobalInternetMode.Open &&
                                  hasLookup &&
                                  internetDisabledLookup.Contains(entry.DistrictIndex);

                // Calculate integrity change based on global mode
                float change;
                if (globalMode == GlobalInternetMode.Firewall)
                {
                    // FIREWALL: Both infection and recovery happen (partial filtering)
                    // FIX W2-H3: Critical boost is district property, not mode reward
                    float effectiveFwRecovery = recoveryRate;
                    if (entry.Integrity < stateRef.ValueRO.CriticalThreshold)
                        effectiveFwRecovery *= stateRef.ValueRO.CriticalRecoveryMultiplier;
                    change = (effectiveFwRecovery - infectionRate) * deltaTimeHours;
                }
                else if (globalMode == GlobalInternetMode.Blackout || isIsolated)
                {
                    // BLACKOUT or OPEN+ISOLATE: Pure recovery
                    // S17b-2 FIX: For isolated districts in Open mode, EffectiveRecoveryRate is 0
                    // (Open = no global recovery). Use base RecoveryRate — isolate means
                    // "this district acts as if in Blackout for recovery purposes".
                    float effectiveRecovery = isIsolated ? stateRef.ValueRO.RecoveryRate : recoveryRate;
                    if (entry.Integrity < stateRef.ValueRO.CriticalThreshold)
                    {
                        effectiveRecovery *= stateRef.ValueRO.CriticalRecoveryMultiplier;
                    }
                    change = effectiveRecovery * deltaTimeHours;
                }
                else
                {
                    // OPEN mode with internet: Pure infection
                    change = -infectionRate * deltaTimeHours;
                }

                // Apply change with clamping
                float newIntegrity = math.clamp(entry.Integrity + change, 0f, 1f);

                // Check for threshold crossing (FIX N2-01: hysteresis prevents oscillation)
                bool wasCompromised = entry.IsCompromised;
                // Clamp to 1.0: a config CompromiseThreshold >= (1 - RECOVERY_HYSTERESIS_BAND) would
                // otherwise push the recovery threshold above 1.0, and newIntegrity (<=1.0) could never
                // clear it — recovery would be permanently locked (S-PSY-01).
                float threshold = math.min(1f, stateRef.ValueRO.CompromiseThreshold + (wasCompromised ? RECOVERY_HYSTERESIS_BAND : 0f));
                bool isNowCompromised = newIntegrity < threshold;

                if (!wasCompromised && isNowCompromised)
                {
                    OnDistrictCompromised(entry.DistrictIndex, newIntegrity);
                }
                else if (wasCompromised && !isNowCompromised)
                {
                    OnDistrictRecovered(entry.DistrictIndex, newIntegrity);
                }

                // Update buffer entry
                entry.Integrity = newIntegrity;
                entry.IsCompromised = isNowCompromised;
                integrityBuffer[i] = entry;
            }

            if (internetDisabledLookup.IsCreated)
                internetDisabledLookup.Dispose();

            // Update global commerce penalties when mode changes. No phantom event after a
            // load: ValidateAfterLoad seeds m_LastInternetMode from the restored InternetMode
            // before the first tick, so this transition only fires on a real switch.
            if (globalMode != m_LastInternetMode)
            {
                UpdateGlobalPenalties(m_LastInternetMode, globalMode);
                EventBus?.SafePublish(new InternetModeChangedEvent(m_LastInternetMode, globalMode), nameof(CognitiveStateSystem));
                m_LastInternetMode = globalMode;
                Log.Info($" Internet mode changed to: {globalMode}");
            }
        }

        /// <summary>
        /// Update global commerce penalties when internet mode changes.
        /// </summary>
        private void UpdateGlobalPenalties(GlobalInternetMode oldMode, GlobalInternetMode newMode)
        {
#pragma warning disable CIVIC256 // Resolved in OnCreate — null only if PenaltySystem missing
            if (m_PenaltySystem == null)
                return;
#pragma warning restore CIVIC256

            // Remove old penalties (applied to all tracked districts)
            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return;
            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var integrityBuffer)) return;

            var oldPenalty = InternetModeToPenalty(oldMode);
            var newPenalty = InternetModeToPenalty(newMode);

            for (int i = 0; i < integrityBuffer.Length; i++)
            {
                int districtIndex = integrityBuffer[i].DistrictIndex;

                if (oldPenalty != PenaltySource.None)
                    m_PenaltySystem.RemovePenalty(districtIndex, oldPenalty);

                if (newPenalty != PenaltySource.None)
                    m_PenaltySystem.RegisterPenalty(districtIndex, newPenalty);
            }
        }

        private void ApplyActivePenaltiesToDistrict(int districtIndex)
        {
            // GretaDeployed is registered by HeroDeploymentSystem (separate concern, separate listener).
            var internetPenalty = InternetModeToPenalty(m_LastInternetMode);
            if (internetPenalty != PenaltySource.None)
                m_PenaltySystem?.RegisterPenalty(districtIndex, internetPenalty);
        }

        /// <summary>
        /// Build HashSet of district indices with internet disabled.
        /// </summary>
        private NativeParallelHashSet<int> BuildInternetDisabledLookup(out bool hasData)
        {
            hasData = false;

            if (!m_SpotterCmQuery.TryGetSingletonEntity<SpotterCountermeasuresState>(out var cmEntity))
                return default; // S1168: NativeParallelHashSet is a value type — default is not null, caller gates on hasData
            if (!m_InternetDisabledBufferLookup.TryGetBuffer(cmEntity, out var internetBuffer))
                return default;
            if (internetBuffer.Length == 0)
                return default;

            var lookup = new NativeParallelHashSet<int>(internetBuffer.Length, Allocator.Temp);
            for (int i = 0; i < internetBuffer.Length; i++)
            {
                lookup.Add(internetBuffer[i].DistrictIndex);
            }

            hasData = true;
            return lookup;
        }

        private void OnDistrictCompromised(int districtIndex, float integrity)
        {
            m_PenaltySystem?.RegisterPenalty(districtIndex, PenaltySource.CognitiveCompromised);
            EventBus?.SafePublish(new CognitiveCompromisedEvent(districtIndex, integrity), "CognitiveStateSystem");
            Log.Info($" District {districtIndex} COMPROMISED! Integrity: {integrity:P0}");
        }

        private void OnDistrictRecovered(int districtIndex, float integrity)
        {
            m_PenaltySystem?.RemovePenalty(districtIndex, PenaltySource.CognitiveCompromised);
            EventBus?.SafePublish(new CognitiveRecoveredEvent(districtIndex, integrity), "CognitiveStateSystem");
            Log.Info($" District {districtIndex} RECOVERED. Integrity: {integrity:P0}");
        }

#pragma warning disable CIVIC235 // One-time activation: must activate even if system was throttled
        private void OnWarStarted(WarStartedEvent evt)
        {
            ForceNextUpdate();
#pragma warning restore CIVIC235
            if (!m_StateQuery.TryGetSingletonRW<CognitiveState>(out var stateRef))
                return;

            if (stateRef.ValueRO.IsActive)
                return;

            stateRef.ValueRW.IsActive = true;
            Log.Info(" ACTIVATED - War has begun. Enemy propaganda operations commencing.");
        }

        /// <summary>
        /// FIX A1a: Force immediate cognitive state recalculation on wave phase transitions.
        /// Without this, CognitiveStateSystem may not fire for up to 500ms after phase change,
        /// causing stale cognitive data for ExodusSystem and other consumers.
        /// </summary>
#pragma warning disable CIVIC235 // ForceNextUpdate is safe when disabled — just sets flag
        private void OnWavePhaseChanged(ThreatNarrativeEvent evt)
        {
            if (evt.Type == ThreatNarrativeEventType.WavePhaseChanged)
                ForceNextUpdate();
#pragma warning restore CIVIC235
        }

        private void OnDistrictLifecycle(DistrictLifecycleEvent evt)
        {
            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity)) return;
            m_CogIntegrityBufferLookup.Update(this);
            if (!m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var buffer)) return;

            // GretaDeployed is registered/removed by HeroDeploymentSystem (separate listener).
            if (evt.Lifecycle == DistrictLifecycle.Created)
            {
                // Idempotent: skip if already tracked (SyncExistingDistricts may have added it first)
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i].DistrictIndex == evt.DistrictIndex)
                    {
                        if (Log.IsDebugEnabled) Log.Debug($" District {evt.DistrictIndex} already tracked, skipping");
                        return;
                    }
                }

                if (!GameTimeSystem.TryGetGameHours(out _))
                {
                    Log.Warn($"[CognitiveStateSystem] TimeProvider unavailable; district {evt.DistrictIndex} add will retry via sync");
                    m_PendingDistrictSync = true;
                    return;
                }
#pragma warning disable CIVIC230 // DynamicBuffer: each district event creates unique entry
                buffer.Add(new CognitiveIntegrityBuffer
                {
                    DistrictIndex = evt.DistrictIndex,
#pragma warning restore CIVIC230
                    Integrity = 1.0f,
                    IsCompromised = false
                });

                // T10-6 FIX: Apply current internet mode penalty to new district
                var penalty = InternetModeToPenalty(m_LastInternetMode);
                if (penalty != PenaltySource.None)
                    m_PenaltySystem?.RegisterPenalty(evt.DistrictIndex, penalty);

                if (Log.IsDebugEnabled) Log.Debug($" Added district {evt.DistrictIndex} to tracking");
            }
            else if (evt.Lifecycle == DistrictLifecycle.Destroyed)
            {
                for (int i = buffer.Length - 1; i >= 0; i--)
                {
                    if (buffer[i].DistrictIndex == evt.DistrictIndex)
                    {
                        if (buffer[i].IsCompromised)
                        {
                            m_PenaltySystem?.RemovePenalty(evt.DistrictIndex, PenaltySource.CognitiveCompromised);
                        }

                        // S18-H3 FIX: Remove internet mode penalty (Firewall/Blackout) on destroy
                        var internetPenalty = InternetModeToPenalty(m_LastInternetMode);
                        if (internetPenalty != PenaltySource.None)
                            m_PenaltySystem?.RemovePenalty(evt.DistrictIndex, internetPenalty);

                        buffer.RemoveAt(i);
                        if (Log.IsDebugEnabled) Log.Debug($" Removed district {evt.DistrictIndex} from tracking");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Reset all serializable state to defaults.
        /// </summary>
        public void ResetState()
        {
            if (!m_StateQuery.TryGetSingletonRW<CognitiveState>(out var stateRef))
                return;

            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return;

            // FIX L2: Remove active penalties before resetting state
            if (m_LastInternetMode != GlobalInternetMode.Open)
            {
                m_CogIntegrityBufferLookup.Update(this);
                UpdateGlobalPenalties(m_LastInternetMode, GlobalInternetMode.Open);
            }
            // GretaDeployed penalties are managed by HeroDeploymentSystem; its own ResetState handles them.

            if (!EntityManager.HasBuffer<CognitiveIntegrityBuffer>(stateEntity)) return;
            var buffer = EntityManager.GetBuffer<CognitiveIntegrityBuffer>(stateEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].IsCompromised)
                    m_PenaltySystem?.RemovePenalty(buffer[i].DistrictIndex, PenaltySource.CognitiveCompromised);
            }

            stateRef.ValueRW = CognitiveState.Default;
            buffer.Clear();

            m_LastInternetMode = GlobalInternetMode.Open; // CIVIC229 FIX

            Log.Info(" State reset");
        }

#if DEBUG
        public bool DebugOverrideIntegrity(float value)
        {
            if (!m_StateQuery.TryGetSingletonRW<CognitiveState>(out var stateRef))
                return false;

            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity))
                return false;

            if (!EntityManager.HasBuffer<CognitiveIntegrityBuffer>(stateEntity))
                return false;

            var buffer = EntityManager.GetBuffer<CognitiveIntegrityBuffer>(stateEntity);
            float nextIntegrity = math.clamp(value, 0f, 1f);

            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                bool wasCompromised = entry.IsCompromised;
                // Same clamped hysteresis as the live update path (S-PSY-01): recovery threshold
                // never exceeds 1.0, so a high CompromiseThreshold cannot lock recovery.
                float threshold = math.min(1f, stateRef.ValueRO.CompromiseThreshold + (wasCompromised ? RECOVERY_HYSTERESIS_BAND : 0f));
                bool isNowCompromised = nextIntegrity < threshold;

                if (!wasCompromised && isNowCompromised)
                    OnDistrictCompromised(entry.DistrictIndex, nextIntegrity);
                else if (wasCompromised && !isNowCompromised)
                    OnDistrictRecovered(entry.DistrictIndex, nextIntegrity);

                entry.Integrity = nextIntegrity;
                entry.IsCompromised = isNowCompromised;
                buffer[i] = entry;
            }

            Log.Info($"[DEBUG] City cognitive integrity override: {nextIntegrity:F2}");
            return true;
        }
#endif

        public bool TrySetInternetMode(GlobalInternetMode mode, out ReasonId reasonId)
        {
            reasonId = ReasonId.None;
            if (mode != GlobalInternetMode.Open
                && mode != GlobalInternetMode.Firewall
                && mode != GlobalInternetMode.Blackout)
            {
                reasonId = ReasonIds.InternetModeInvalid;
                return false;
            }

            if (!m_StateQuery.TryGetSingletonRW<CognitiveState>(out var stateRef))
            {
                reasonId = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            if (stateRef.ValueRO.InternetMode == mode)
                return true;

            m_CogIntegrityBufferLookup.Update(this);
            WirePenaltySystem();

            var previousMode = stateRef.ValueRO.InternetMode;
            stateRef.ValueRW.InternetMode = mode;
            UpdateGlobalPenalties(previousMode, mode);
            m_LastInternetMode = mode;
            ForceNextUpdate();
            Log.Info($" Internet mode set synchronously: {mode}");
            return true;
        }

        /// <summary>
        /// IPostLoadValidation: re-register internet-mode and CognitiveCompromised penalties
        /// immediately after load, rather than waiting for the first day-change tick (up to a 24h
        /// gap at high speed).
        ///
        /// These penalties DO persist: BlackoutSystem snapshots ThreadSafeDistrictState.m_Penalties
        /// (their runtime registry) and reloads it. This pass is an idempotent defensive re-sync from
        /// the authoritative cognitive state (InternetMode + the IsCompromised buffer), not the sole
        /// restoration path — RegisterPenalty's AddSource is a no-op when the flag is already set, so it
        /// only fills a gap if the DistrictState snapshot and cognitive state diverge (e.g. a district
        /// dropped during key remap). Both paths resolve districts through the same DistrictKeyCodec
        /// engine remap, so they converge on one runtime index.
        /// GretaDeployed restoration is owned by HeroDeploymentSystem.
        /// </summary>
        public void ValidateAfterLoad()
        {
            CognitiveState.EnsureExists(EntityManager);
            if (!m_StateQuery.TryGetSingletonEntity<CognitiveState>(out var stateEntity)) return;

            if (!WirePenaltySystem())
            {
                Log.Warn("DistrictPenaltySystem not found — cannot restore cognitive penalties");
                return;
            }

            if (!EntityManager.Exists(stateEntity)) return;
            var state = EntityManager.GetComponentData<CognitiveState>(stateEntity);

            m_CogIntegrityBufferLookup.Update(this);
            m_LastInternetMode = state.InternetMode;
            SyncExistingDistricts();

            // S18-H5 FIX: Restore internet mode penalties after load
            if (state.InternetMode != GlobalInternetMode.Open)
            {
                // Force re-registration by simulating Open → current mode transition
                UpdateGlobalPenalties(GlobalInternetMode.Open, state.InternetMode);
                Log.Info($"Post-load: restored {state.InternetMode} penalties");
            }

            // S18-H6 FIX: Restore CognitiveCompromised penalties after load.
            // Persisted compromised status is the source of truth; do not suppress its
            // penalty behind act gates or the loaded status/penalty pair diverges.
            if (m_CogIntegrityBufferLookup.TryGetBuffer(stateEntity, out var integrityBuffer))
            {
                int restoredCount = 0;
                for (int i = 0; i < integrityBuffer.Length; i++)
                {
                    if (integrityBuffer[i].IsCompromised)
                    {
                        m_PenaltySystem.RegisterPenalty(integrityBuffer[i].DistrictIndex, PenaltySource.CognitiveCompromised);
                        restoredCount++;
                    }
                }
                if (restoredCount > 0)
                    Log.Info($"Post-load: restored CognitiveCompromised penalties for {restoredCount} districts");
            }

            // GretaDeployed penalty restoration is owned by HeroDeploymentSystem.ValidateAfterLoad
        }

        private bool WirePenaltySystem()
        {
            return m_PenaltyWire.EnsureWired(() =>
            {
                m_PenaltySystem = World.GetExistingSystemManaged<DistrictPenaltySystem>();
                if (m_PenaltySystem == null)
                {
                    Log.Warn(" DistrictPenaltySystem not found — penalties will be unavailable");
                    return false;
                }

                return true;
            });
        }
    }
}
