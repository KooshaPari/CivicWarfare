using Game.Common;
using CivicSurvival.Core.Systems.Scheduling;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.AirDefense;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Logic;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Domains.AirDefense.Logic;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Manages Patriot ballistic missile interception.
    ///
    /// Responsibilities:
    /// - Patriot AA-prop engagement (InterceptChanceBallistic > 0)
    ///
    /// SRP: Ballistic interception only, no Shahed targeting.
    ///
    /// Data Flow:
    /// - Reads: SpotterPenaltyState (GlobalPenalty)
    /// - Writes: BallisticInterceptState.IsIntercepted (via ECB)
    ///
    /// CROSS-DOMAIN STALENESS — ACCEPTED (H12):
    /// No ordering vs SpotterAggregateSystem (Spotters domain). SpotterPenaltyState changes on
    /// wave events (~1/min). 1-frame stale on a minute-scale event is irrelevant.
    ///
    /// H19 — DESIGN: No Telemarathon detection bonus (ADO has it). Ballistic = radar-based (Patriot),
    /// not visual spotting. May be intentional — needs design review before adding.
    /// </summary>
    [ActIndependent]
    public partial class BallisticDefenseSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("BallisticDefenseSystem");

        // C-5: 4th self-defending consumer. A stale post-load ballistic snapshot
        // must not make Patriots waste ammo / intercepts on a phantom.
        private static int s_DroppedStaleSnapshotCount;
        public static int DroppedStaleSnapshotCount => System.Threading.Volatile.Read(ref s_DroppedStaleSnapshotCount);
        // Count of frames where the source reported more snapshots than the array holds
        // (count/array generation mismatch). 0 with the current single-NativeList source;
        // a non-zero value means a future IBallisticSnapshotSource impl broke the atomicity.
        private static int s_ClampedSnapshotCount;
        public static int ClampedSnapshotCount => System.Threading.Volatile.Read(ref s_ClampedSnapshotCount);
        // Reset every PERF report cycle (PerfReportSections), same lifecycle as s_EcbCommand counters.
        public static void ResetCounters()
        {
            System.Threading.Interlocked.Exchange(ref s_DroppedStaleSnapshotCount, 0);
            System.Threading.Interlocked.Exchange(ref s_ClampedSnapshotCount, 0);
        }

        private const int RANDOM_PRIME_SEED = 7919;
        private const int DROP_LOG_THROTTLE_FRAMES = 60;

        // Cache PID to avoid Process.GetCurrentProcess() allocation on every seed (OnCreate).
        private static readonly int s_ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        private EntityQuery m_BallisticQuery;
        private EntityQuery m_SpotterPenaltyQuery;
        private ComponentLookup<BallisticInterceptState> m_InterceptStateLookup;
        private ComponentLookup<SpotterPenaltyState> m_SpotterPenaltyLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<Destroyed> m_DestroyedLookup;
        private ComponentLookup<PendingDestruction> m_PendingDestructionLookup;
        // Live RW re-read of ammo/cooldown by aaData.GetEntity() — never cached (mutates per shot).
        // RW so TryInterceptBallistic can write ammo/cooldown back through the lookup.
        private ComponentLookup<AirDefenseInstallation> m_AALookup;
        private ComponentLookup<AirDefenseCooldown> m_CooldownLookup;
        // Shared live-AA snapshot (Simulate/Transform completion paid only on AA-set change).
        private LiveAACacheSystem m_LiveAACache = null!;
        private IBallisticSnapshotSource m_BallisticSnapshotSource = null!;
        [System.NonSerialized] private CivicDependencyWire m_DependencyWire = null!;
        [System.NonSerialized] private ThreatGenerationClock m_threatGenerationClock = null!;
        [System.NonSerialized] private CivicSingletonHandle<SpotterPenaltyState> m_SpotterPenalty;

        private SerializableRandom m_Random;
        private InterceptBarrier m_InterceptBarrier = null!;
        [System.NonSerialized] private bool m_InterceptFiredThisFrame;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Game time = 0.0f at cold start → timeSeed was always 7919 (constant).
            // Use TickCount ^ PID ^ prime for real entropy at the OnCreate seed path.
            int seed = unchecked(System.Environment.TickCount ^ s_ProcessId ^ RANDOM_PRIME_SEED);
            m_Random = new SerializableRandom(seed);

            // FIX: Use ThreatPosition instead of Transform to avoid CS2 Job crashes
            m_BallisticQuery = GetEntityQuery(
                ComponentType.ReadOnly<Ballistic>(),
                ComponentType.ReadOnly<ThreatPosition>()
            );

            m_SpotterPenaltyQuery = GetEntityQuery(ComponentType.ReadOnly<SpotterPenaltyState>());
            m_SpotterPenalty = CreateSingletonHandle<SpotterPenaltyState>(m_SpotterPenaltyQuery);
            m_InterceptStateLookup = GetComponentLookup<BallisticInterceptState>(false);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_DestroyedLookup = GetComponentLookup<Destroyed>(true);
            m_PendingDestructionLookup = GetComponentLookup<PendingDestruction>(true);
            m_SpotterPenaltyLookup = GetComponentLookup<SpotterPenaltyState>(true);
            // RW: live re-read AND write-back of ammo/cooldown by aaData.GetEntity().
            m_AALookup = GetComponentLookup<AirDefenseInstallation>(false);
            m_CooldownLookup = GetComponentLookup<AirDefenseCooldown>(false);
            m_DependencyWire = new CivicDependencyWire(nameof(BallisticDefenseSystem));
            m_InterceptedThisFrame = new NativeHashSet<Entity>(32, Allocator.Persistent);

            // InterceptBarrier is a CoreKernel scheduling anchor — vanilla-style resolve.
            m_InterceptBarrier = World.GetOrCreateSystemManaged<InterceptBarrier>();

            // PERF: Skip entirely when no ballistic threats (RequireForUpdate)
            RequireForUpdate(m_BallisticQuery);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_DependencyWire.EnsureWired(() =>
            {
                m_BallisticSnapshotSource = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullBallisticSnapshotSource.Instance);
                return m_BallisticSnapshotSource != null;
            });
            // C-5: threat-generation clock (process-lifetime; resolved here, never in
            // OnUpdate — CIVIC018-safe).
            m_threatGenerationClock ??= ServiceRegistry.Instance.Require<ThreatGenerationClock>();
            // Shared live-AA cache — feature-gated system, resolve via FeatureRegistry in
            // OnStartRunning (CIVIC400/403; mirrors ADO resolving ResidentialCacheSystem).
            m_LiveAACache ??= FeatureRegistry.Instance.Require<LiveAACacheSystem>();
            ResolveSingletonReadOnly(ref m_SpotterPenalty);
        }

        public void ValidateAfterLoad() => ReResolveRuntimeRefs();

        private void ReResolveRuntimeRefs()
        {
            if (!ServiceRegistry.IsInitialized)
                return;

            m_BallisticSnapshotSource = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullBallisticSnapshotSource.Instance);
            m_threatGenerationClock = ServiceRegistry.Instance.Require<ThreatGenerationClock>();
            ResolveSingletonReadOnly(ref m_SpotterPenalty);
        }

        protected override void OnDestroy()
        {
            // FIX P0-5: Dispose same-frame tracking HashSet
            if (m_InterceptedThisFrame.IsCreated)
                m_InterceptedThisFrame.Dispose();

            base.OnDestroy();
        }

        protected override void OnUpdateImpl()
        {
            m_InterceptStateLookup.Update(this);
            m_DeletedLookup.Update(this);
            m_DestroyedLookup.Update(this);
            m_PendingDestructionLookup.Update(this);
            m_SpotterPenaltyLookup.Update(this);
            m_AALookup.Update(this);
            m_CooldownLookup.Update(this);

            bool hasSnapshots = m_BallisticSnapshotSource.IsBallisticSnapshotCreated
                && m_BallisticSnapshotSource.BallisticCount != 0;
            if (!hasSnapshots)
                return;

            ProcessBallisticEngagements();
            if (m_InterceptFiredThisFrame) // H4: only register when ECB commands were actually produced
                m_InterceptBarrier.AddJobHandleForProducer(Dependency);
        }

        // FIX P0-5: Track intercepted ballistics within same frame to prevent double targeting
        // R9-H2: NativeHashSet<Entity> (Index+Version) instead of int (Index-only)
        private NativeHashSet<Entity> m_InterceptedThisFrame;

        /// <summary>
        /// Process ballistic missile engagements via Patriot AA props.
        /// </summary>
        private void ProcessBallisticEngagements()
        {
            m_InterceptFiredThisFrame = false;
            // FIX P0-5: Clear same-frame tracking (ECB writes are deferred, so we track locally)
            m_InterceptedThisFrame.Clear();

            ProcessBuildingPatriotEngagements();
        }

        /// <summary>
        /// Process Patriot AA-prop engagements.
        /// </summary>
        private void ProcessBuildingPatriotEngagements()
        {
            if (!GameTimeSystem.TryGetTotalGameSeconds(out var now))
                return;

            // Read spotter penalty from CrossDomain singleton through a cached entity.
            float spotterPenalty = 0f;
            var spotterPenaltyEntity = ResolveSingletonReadOnly(ref m_SpotterPenalty);
            if (spotterPenaltyEntity != Entity.Null
                && m_SpotterPenaltyLookup.TryGetComponent(spotterPenaltyEntity, out var spotterPenaltyState))
                spotterPenalty = spotterPenaltyState.GlobalPenalty;

            var ballisticSnapshotSource = m_BallisticSnapshotSource;
            if (ballisticSnapshotSource == null)
                return;
            NativeArray<BallisticSnapshotInfo>.ReadOnly ballisticSnapshots = ballisticSnapshotSource.BallisticSnapshots;
            int ballisticCount = ballisticSnapshotSource.BallisticCount;

            // Defense-in-depth: BallisticCount and BallisticSnapshots come from the same
            // NativeList today, so they cannot diverge. But the source is an interface — a
            // future impl could hand back a count and an array captured at different
            // generations. Clamp the loop bound to the real array length so a larger count
            // can never index past the buffer; in release the NativeArray bounds check is
            // stripped, so that read would be a native AV instead of an exception.
            int snapshotLength = ballisticSnapshots.IsCreated ? ballisticSnapshots.Length : 0;
            if (ballisticCount > snapshotLength)
            {
                int total = System.Threading.Interlocked.Increment(ref s_ClampedSnapshotCount);
                if (total == 1 || total % DROP_LOG_THROTTLE_FRAMES == 0)
                    Log.Warn($"Ballistic snapshot count {ballisticCount} exceeds array length {snapshotLength} — clamping. Source count/array generation mismatch. total={ClampedSnapshotCount}");
                ballisticCount = snapshotLength;
            }
            else if (ballisticCount < 0)
            {
                ballisticCount = 0;
            }

            // C-5: count/log stale-generation snapshots ONCE per frame here. The
            // per-AA loop below still skips them (functional), but counting there
            // inflated the diagnostic by the AA-installation count.
            int staleSnapshots = 0;
            for (int i = 0; i < ballisticCount; i++)
            {
                var s = ballisticSnapshots[i];
                if (s.ThreatGeneration == ThreatGenerationClock.Unstamped
                    || s.ThreatGeneration != m_threatGenerationClock.Current)
                    staleSnapshots++;
            }
            if (staleSnapshots > 0)
            {
                int total = System.Threading.Interlocked.Add(ref s_DroppedStaleSnapshotCount, staleSnapshots);
                // Log first detection, then whenever the batch crosses a throttle boundary.
                if (total == staleSnapshots
                    || total / DROP_LOG_THROTTLE_FRAMES != (total - staleSnapshots) / DROP_LOG_THROTTLE_FRAMES)
                    Log.Warn($"Dropped {staleSnapshots} stale/unstamped ballistic snapshot(s) (current generation={m_threatGenerationClock.Current}) — post-load phantom. total={DroppedStaleSnapshotCount}");
            }

            // SSOT: AA come from the shared live-AA cache (Simulate/Transform completion paid only
            // on AA-set change inside LiveAACacheSystem, not per frame). Ammo/cooldown are NEVER
            // cached — re-read live by aaData.GetEntity() through the RW ComponentLookups below.
            var liveAA = m_LiveAACache.GetLiveAASnapshot();

            for (int a = 0; a < liveAA.Length; a++)
            {
                var aaData = liveAA[a];

                // Only Patriot has InterceptChanceBallistic > 0 — cheap capability gate (stable,
                // cached at rebuild; capability never mutates live).
                if (aaData.InterceptChanceBallistic <= 0) continue;

                var aaEntity = aaData.GetEntity();
                // Re-read LIVE ammo/crew/cooldown. The cached Entity is Index+Version (Axiom 11);
                // after a load-boundary destroy+create the version mismatches → TryGetComponent
                // returns false → skip (no interception against a wrong AA). The order-version
                // rebuild closes that window the same frame anyway.
                if (!m_AALookup.TryGetComponent(aaEntity, out var aa))
                    continue;
                if (!m_CooldownLookup.TryGetComponent(aaEntity, out var cd))
                    continue;

                if (now < cd.ReadyAtGameSeconds) continue;
                if (aa.CurrentAmmo <= 0) continue;
                if (aa.CrewAssigned <= 0) continue;

                // Skip if the AA object's building is gone (Deleted/Destroyed). Position itself is
                // the cached snapshot value (Transform read happens only in the cache rebuild).
                var buildingEntity = aaData.Building.ToEntity();
                if (m_DeletedLookup.HasComponent(buildingEntity) || m_DestroyedLookup.HasComponent(buildingEntity))
                    continue;
                float3 aaPos = aaData.Position;
                float rangeSq = aaData.RangeSq;

                // Find closest ballistic in range (ballistic count is small, 1-5 per wave)
                // Uses cached entity array + ComponentLookup to avoid CompleteDependencyBeforeRO
                // sync point on ThreatPosition (written by TMS's async BallisticMovementJobEntity).
                Entity closestBallistic = Entity.Null;
                float closestDistSq = float.MaxValue;
                float3 closestPos = float3.zero;

                for (int i = 0; i < ballisticCount; i++)
                {
                    var snapshot = ballisticSnapshots[i];
                    // C-5: stale/unstamped generation — skip before it can become the
                    // intercept target (TryInterceptBallistic spends ammo). Counted
                    // once per frame in the pre-pass above, not here.
                    if (snapshot.ThreatGeneration == ThreatGenerationClock.Unstamped
                        || snapshot.ThreatGeneration != m_threatGenerationClock.Current)
                        continue;
                    if (snapshot.IsIntercepted)
                        continue;
                    // S16a-3 FIX: Skip already-arrived ballistics
                    if (snapshot.IsArrived) continue;

                    var ballisticEntity = snapshot.Entity;
                    if (m_DeletedLookup.HasComponent(ballisticEntity)
                        || m_DestroyedLookup.HasComponent(ballisticEntity)
                        || (m_PendingDestructionLookup.HasComponent(ballisticEntity) && m_PendingDestructionLookup.IsComponentEnabled(ballisticEntity)))
                        continue;
                    if (!m_InterceptStateLookup.TryGetComponent(ballisticEntity, out var liveInterceptState)
                        || liveInterceptState.IsIntercepted
                        || liveInterceptState.IsLeaked)
                        continue;

                    // FIX P0-5: Check same-frame tracking (Donor Patriot may have intercepted)
                    if (m_InterceptedThisFrame.Contains(ballisticEntity)) continue;

                    float3 ballisticPos = snapshot.Position;
                    float distSq = math.distancesq(aaPos, ballisticPos);

                    if (distSq <= rangeSq)
                    {
                        // Leak-attribution funnel: a ready Patriot has this missile in range —
                        // if it still lands unengaged, that is a throughput loss, not a coverage gap.
                        if (!liveInterceptState.WasCandidate)
                        {
                            liveInterceptState.WasCandidate = true;
                            m_InterceptStateLookup[ballisticEntity] = liveInterceptState;
                        }

                        if (distSq < closestDistSq)
                        {
                            closestDistSq = distSq;
                            closestBallistic = ballisticEntity;
                            closestPos = ballisticPos;
                        }
                    }
                }

                if (closestBallistic != Entity.Null)
                {
                    // TryInterceptBallistic mutates ammo/cooldown on the local live structs; write
                    // them back through the RW lookups (single-writer for these two on this entity).
                    TryInterceptBallistic(now, ref aa, ref cd, aaPos, aaData.Rotation, aaEntity.Index, closestBallistic, closestPos, spotterPenalty);
                    m_AALookup[aaEntity] = aa;
                    m_CooldownLookup[aaEntity] = cd;
                }
            }
        }

        /// <summary>
        /// Attempt to intercept a ballistic missile.
        /// </summary>
        private void TryInterceptBallistic(double now, ref AirDefenseInstallation aa, ref AirDefenseCooldown cd, float3 aaPos, quaternion aaRotation, int aaEntityIndex, Entity ballisticEntity, float3 ballisticPos, float spotterPenalty)
        {
            if (!m_InterceptStateLookup.TryGetComponent(ballisticEntity, out var currentState)
                || currentState.IsIntercepted
                || currentState.IsLeaked)
            {
                if (Log.IsDebugEnabled) Log.Debug($"Ballistic intercept skipped: missing/already intercepted/leaked state on {ballisticEntity.Index}");
                return;
            }

            // Leak-attribution funnel: a Patriot shot is being resolved against this missile
            // (hit or miss decided below) — engaged either way.
            currentState.WasEngaged = true;
            m_InterceptStateLookup[ballisticEntity] = currentState;

            aa.CurrentAmmo--;
            cd.ReadyAtGameSeconds = now + aa.CooldownDuration;

            IncrementShotsFired();

            // VFX: tracer rounds + interceptor missile from Patriot to ballistic
            EventBus?.SafePublish(new AAFireEvent(aaPos, ballisticPos, aa.Type, aaEntityIndex, ballisticEntity.Index, ballisticEntity.Version, IsBallistic: true, AARotation: aaRotation), "BallisticDefenseSystem");

            float baseChance = aa.InterceptChanceBallistic;
            // Route through the single intercept-chance rule instead of re-deriving base − spotter − low-ammo
            // inline. Evasion is not applicable to ballistics (no missedShots tracking) → missedShotsCount: 0.
            // +1 restores the pre-shot ammo count (CurrentAmmo was decremented above) for the low-ammo gate.
            float chance = AALogic.CalculateInterceptChance(
                baseChance, aa.CurrentAmmo + 1, aa.MaxAmmo,
                spotterPenalty, missedShotsCount: 0, detectionBonus: 0f);

            if (spotterPenalty > 0f)
            {
                if (Log.IsDebugEnabled) Log.Debug($"[{aa.Type}] Spotter penalty: -{spotterPenalty:P0}");
            }

            float roll = m_Random.NextFloat(0f, 1f);

            if (roll < chance)
            {
                m_InterceptedThisFrame.Add(ballisticEntity);
                if (OnBallisticInterceptSuccess(ballisticEntity, ballisticPos)
                    && Log.IsDebugEnabled)
                    Log.Debug($"[{aa.Type}] BALLISTIC INTERCEPT! Ammo: {aa.CurrentAmmo}/{aa.MaxAmmo}");
            }
            else
            {
                if (Log.IsDebugEnabled) Log.Debug($"[{aa.Type}] Ballistic miss! Roll: {roll:F2}, Chance: {chance:F2}");
            }

            // Ammo warnings
            int lowAmmoThreshold = math.max(1, aa.MaxAmmo / 10);
            if (aa.CurrentAmmo <= lowAmmoThreshold && aa.CurrentAmmo > 0)
            {
                Log.Warn($"[{aa.Type}] LOW AMMO: {aa.CurrentAmmo}/{aa.MaxAmmo}");
            }
        }

        /// <summary>
        /// Handle successful ballistic interception.
        /// </summary>
        private bool OnBallisticInterceptSuccess(Entity ballisticEntity, float3 position)
        {
            // Mark as intercepted + create InterceptRequest only when state exists.
            // Orphaned request (no state) would cause InterceptProcessingSystem to process
            // a ballistic that was never marked — guard prevents phantom intercepts.
            if (m_InterceptStateLookup.TryGetComponent(ballisticEntity, out var interceptState))
            {
                m_InterceptFiredThisFrame = true; // H4: mark that ECB commands were produced
                var interceptEcb = m_InterceptBarrier.CreateCommandBuffer();
                interceptState.IsIntercepted = true;
                // Ballistic interception is Patriot-only (InterceptChanceBallistic > 0 gate) → always
                // launches a visible interceptor → always coast until it arrives, then terminalize.
                interceptState.AwaitingInterceptorImpact = true;
#pragma warning disable CIVIC035 // Durable terminal marker must be written immediately; InterceptRequest stays transient/deferred.
                m_InterceptStateLookup[ballisticEntity] = interceptState;
#pragma warning restore CIVIC035

                var requestEntity = interceptEcb.CreateEntity();
                interceptEcb.AddComponent(requestEntity, new InterceptRequest
                {
                    ThreatEntityIndex = ballisticEntity.Index,
                    ThreatEntityVersion = ballisticEntity.Version,
                    Position = position,
                    IsBallistic = true
                });
                // InterceptProcessingSystem publishes ThreatInterceptEvent after the
                // per-wave leak floor accepts this request.
                if (Log.IsDebugEnabled) Log.Debug($"BALLISTIC intercept request queued at {position}");
                return true;
            }
            else
            {
                Log.Warn($"[BallisticInterceptSuccess] BallisticInterceptState missing on {ballisticEntity.Index} — InterceptRequest skipped");
                return false;
            }
        }

        private static void IncrementShotsFired()
        {
            // Single-writer pattern: AirDefenseShotStatsFlushSystem drains the counter and
            // writes the total (AA + ballistic) to DebriefingShotStats. Direct write here
            // caused lost-update races when both producers ran in the same frame.
            AirDefenseShotCounter.AddBallisticShots(1);
        }
    }
}
