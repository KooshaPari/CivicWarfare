using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Components.Domain.AirDefense;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Placement;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.AirDefense;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Domains.AirDefense.Logic;

namespace CivicSurvival.Domains.AirDefense.Systems
{
    /// <summary>
    /// Single durable owner of AirDefenseCreditsSingleton (credits) — P2 root.
    ///
    /// SYNCHRONOUS single-truth model (no async writer, no runtime cache):
    /// - Durable credit truth is persisted by THIS system's codec (.Serialization),
    ///   sourced from m_CreditsLatest. AirDefenseCreditsSingleton is a plain
    ///   IComponentData RUNTIME projection (the engine never round-trips it), recreated
    ///   + hydrated from the codec in OnLoadRestore. Canon C1 (ScenarioStateMachine):
    ///   "system owns persistence, singleton is runtime read-model".
    /// - This system is the ONLY writer; every write is a synchronous main-thread
    ///   ComponentLookup RW in OnUpdateImpl/ValidateAfterLoad. There is no
    ///   WriteCreditsJob, no m_Current* cache, no lagging mirror.
    /// - Perf-neutral by construction: a dependency audit confirmed this is the only
    ///   accessor of the component type that ever needed a job, so the synchronous
    ///   write has no scheduled job to sync against.
    ///
    /// Credit lifecycle (symmetric with the audited-clean budget dimension):
    /// - CONSUME: the AA handler does a READ-ONLY availability check
    ///   (IsHeritageCreditAvailable / IsDonorPatriotCreditAvailable) and stamps a
    ///   BuildingPlacementIntent with ReservedCreditKind set and CreditResolved=false.
    ///   BuildingPlacementPaymentSystem calls this owner (via the AA handler) through
    ///   ResolvePlacementCredit so
    ///   the singleton still has one writer while placement can close in ModificationEnd.
    /// - GRANT: Grant* request entities are applied directly to the singleton
    ///   synchronously (ProcessHeritageRequests), then destroyed. Placement refunds
    ///   use RefundPlacementCredit directly on this owner.
    ///
    /// Policy A: the singleton's CurrentPolicy is a written PROJECTION (not persisted
    /// by the credits codec — only the 4 credit ints are). AirDefensePolicySystem
    /// persists policy through AirDefensePolicyCodec; this system projects its value
    /// into the singleton + snapshot synchronously each frame. No DeserializeSucceeded reconcile.
    ///
    /// Readers (UI, DonorConference via IAirDefenseCreditsReader) read the
    /// synchronously-refreshed m_CreditsLatest — never a stale async value.
    /// </summary>
    [ActIndependent]
    [SingletonOwner(typeof(AirDefenseCreditsSingleton))]
    [OwnedSingletonLifecycle(
        Persisted = true,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.OnStartRunning | SingletonLifecyclePhase.OnLoadRestore,
        DisposePhase = SingletonLifecyclePhase.OnDestroy)]
    public partial class AirDefenseStateSystem : CivicSystemBase, IPostLoadValidation, IResettable, IAirDefenseCreditsReader, IAirDefenseStatsReader, IPatriotDroneInterceptReader, IPatriotDroneToggleCommandService, IAutoResupplyReader, IAutoResupplyToggleCommandService, ICivicSingletonOwner<AirDefenseCreditsSingleton>
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("AirDefenseStateSystem");

        private EntityQuery m_SingletonQuery;
        private EntityQuery m_GrantDonorPatriotQuery;
        private EntityQuery m_AAClearQuery;
        private EntityQuery m_UiStatsAAQuery;
        private EntityQuery m_PlacementIntentQuery;
        private GameSimulationEndBarrier m_GameSimulationEndBarrier = null!;
#pragma warning disable CIVIC229 // System reference — not state, no reset needed
        private AirDefensePolicySystem m_PolicySystem = null!;
#pragma warning restore CIVIC229
        [System.NonSerialized] private CivicDependencyWire m_DependencyWire = null!;

        // Synchronous owner write to the persisted singleton. CIVIC269 ("write via
        // IJob, not direct indexer") is INTENTIONALLY suppressed here: the IJob async
        // path was the P2 ownership defect; the direct synchronous indexer write IS
        // the root fix, and a dependency audit proved it is sync-point-free (no other
        // scheduled job touches this component type). Not a perf false-positive — a
        // deliberate architecture (Axiom 10).
#pragma warning disable CIVIC269
        private ComponentLookup<AirDefenseCreditsSingleton> m_CreditsWriteLookup;
#pragma warning restore CIVIC269
        // Explicit refresh boundary for the synchronous Patriot drone-toggle command
        // (IPatriotDroneToggleCommandService): the UI thread enters here while paused, so
        // the credit lookup must be refreshed at the service boundary before the write.
        // Infrastructure (holds lookups, recreated in OnCreate) — not persisted state.
        [System.NonSerialized] private CivicServiceLookups m_CommandLookups = null!;
        private EntityStorageInfoLookup m_UiStatsStorageInfoLookup;
        private ComponentLookup<AirDefenseInstallation> m_UiStatsAALookup;
        private ComponentLookup<Deleted> m_UiStatsDeletedLookup;
        private ComponentLookup<Destroyed> m_UiStatsDestroyedLookup;
        private AirDefenseUiStatsSnapshot m_UiStatsModel;
        private int m_UiStatsLastCount = -1;
        [EntityQueryOrderCursor("Invalidates the UI stats read model when the live AA archetype set changes structurally.")]
        private int m_UiStatsLastOrderVersion;

        // Synchronously-refreshed reader mirror of the singleton (credits) — readers
        // (UI, DonorConference) read this via GetCreditsSnapshot; never an async value.
        private AirDefenseCreditsSingleton m_CreditsLatest;
        [System.NonSerialized] private bool m_HasPendingHeritageGrantEvent;
        [System.NonSerialized] private int m_PendingHeritageGrantCredits;
        [System.NonSerialized] private int m_PendingHeritageGrantProductionMW;

        private const int MAX_DONOR_PATRIOT_CREDITS = AirDefenseCreditsSingleton.MaxDonorPatriotCredits;
        private const int MAX_HERITAGE_CREDITS = 100; // Overflow guard (HeritageGrantSystem caps actual grant at HeritageMaxCount=10)

        /// <summary>
        /// Single source for clearing the in-process heritage-grant event buffer, called from
        /// every reset leg (ResetState, ResetToBootDefaults, Deserialize) so the legs cannot
        /// drift: a grant published by HeritageGrantSystem but not yet consumed must never
        /// carry across a load/boot-reset into a different city's credit pool.
        /// </summary>
        private void ClearTransientState()
        {
            m_HasPendingHeritageGrantEvent = false;
            m_PendingHeritageGrantCredits = 0;
            m_PendingHeritageGrantProductionMW = 0;
        }

        public void ResetState()
        {
            // No runtime cache to zero — the system codec is the durable truth.
            m_CreditsLatest = AirDefenseCreditsSingleton.Default;
            ClearTransientState();

            // Transient codec buffer — never a parallel persisted truth.
            m_HasRestoredCredits = false;
            m_RestoredCredits = default;
#pragma warning disable CIVIC458 // AirDefense UI stats uses a plain owner-side read model, not VersionedView publication.
            m_UiStatsModel = AirDefenseUiStatsSnapshot.Empty;
#pragma warning restore CIVIC458
            m_UiStatsLastCount = -1;
            m_UiStatsLastOrderVersion = 0;

            // NEW-GAME path only (SetDefaults → ResetState). NOT called from the load
            // path (Deserialize buffers into m_RestoredCredits; OnLoadRestore recreates
            // + hydrates the runtime entity — G1: no structural rebuild mid-load-pass).
            // EnsureExists creates the single runtime projection if none exists.
            AirDefenseCreditsSingleton.EnsureExists(EntityManager);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Do NOT EnsureExists here (SAVE_LOAD_LIFECYCLE_DOCTRINE Invariant 2):
            // OnCreate runs before the game's entity deserialize on a fresh-process
            // load; a default created here would collide with the engine-restored
            // ISerializable entity. Creation belongs to post-deserialize hooks only —
            // ResetState (new game / world reload) and ICivicSingletonOwner.OnLoadRestore
            // (safety EnsureExists). Same reasoning is documented on the sibling
            // AirDefensePolicySystem.OnCreate.

            m_SingletonQuery = GetEntityQuery(ComponentType.ReadWrite<AirDefenseCreditsSingleton>());
            m_GrantDonorPatriotQuery = GetEntityQuery(ComponentType.ReadOnly<GrantDonorPatriotCreditsRequest>());
            m_AAClearQuery = GetEntityQuery(ComponentType.ReadOnly<AirDefenseInstallation>());
            m_UiStatsAAQuery = GetEntityQuery(
                ComponentType.ReadOnly<AirDefenseInstallation>(),
                ComponentType.ReadOnly<Simulate>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Destroyed>());
            // CreditAvailable runs from the AA placement handler's resolve context (cross-system credit
            // eligibility check) — a cached query is context-free, SystemAPI.Query would bind wrong.
            m_PlacementIntentQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingPlacementIntent>());
            m_GameSimulationEndBarrier = World.GetOrCreateSystemManaged<GameSimulationEndBarrier>();
            m_DependencyWire = new CivicDependencyWire(nameof(AirDefenseStateSystem));

            m_CreditsWriteLookup = GetComponentLookup<AirDefenseCreditsSingleton>(false);
            // CIVIC081: every lookup the bundle refreshes appears as a literal
            // {field}.Update(this) inside the constructor lambda so the analyzer can pair
            // RefreshIfStale() callers with the right field set.
            m_CommandLookups = new CivicServiceLookups(() =>
            {
                m_CreditsWriteLookup.Update(this);
            });
            m_UiStatsStorageInfoLookup = GetEntityStorageInfoLookup();
            m_UiStatsAALookup = GetComponentLookup<AirDefenseInstallation>(true);
            m_UiStatsDeletedLookup = GetComponentLookup<Deleted>(true);
            m_UiStatsDestroyedLookup = GetComponentLookup<Destroyed>(true);
            // Seed the reader mirror with a sane default so UI snapshots taken before
            // the first OnUpdateImpl see a valid policy (HumanitarianShield) rather
            // than the zero-init DefensePolicy.Unavailable from `default(struct)`.
            m_CreditsLatest = AirDefenseCreditsSingleton.Default;
            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Register<IAirDefenseCreditsReader>(this);
                // Single owner of the per-type UI stats model — surface the live fleet (total +
                // per-type counts) to the Core crisis-sweep forecast without it importing
                // AirDefense.Systems (Axiom 5). Fail-closed: the null-object projects a zero fleet.
                ServiceRegistry.Instance.Register<IAirDefenseStatsReader>(this);
                // Single owner of the persisted Patriot drone-intercept flag — expose it
                // cross-domain (the targeting orchestrator) as a fail-closed reader.
                ServiceRegistry.Instance.Register<IPatriotDroneInterceptReader>(this);
                // Pause-safe command surface for the UI toggle: the owner applies the flag
                // synchronously on the UI thread (mirrors IBuildingPlacementService), so the
                // button never waits on a GameSimulation update that is stalled while paused.
                ServiceRegistry.Instance.Register<IPatriotDroneToggleCommandService>(this);
                // Per-save AA auto-resupply rule — same owner. Reader lets AAAmmoSystem gate the
                // calm trickle without importing this system; command service applies the toggle
                // synchronously on the UI thread (pause-safe), mirroring the Patriot toggle.
                ServiceRegistry.Instance.Register<IAutoResupplyReader>(this);
                ServiceRegistry.Instance.Register<IAutoResupplyToggleCommandService>(this);
            }

            SubscribeRequired<HeritageGrantedEvent>(OnHeritageGranted);

            Log.Info("Created (synchronous single-owner credits)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_PolicySystem ??= m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<AirDefensePolicySystem>());
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<HeritageGrantedEvent>(OnHeritageGranted);

            if (ServiceRegistry.IsInitialized)
            {
                ServiceRegistry.Instance.Unregister<IAirDefenseCreditsReader>(this);
                ServiceRegistry.Instance.Unregister<IAirDefenseStatsReader>(this);
                ServiceRegistry.Instance.Unregister<IPatriotDroneInterceptReader>(this);
                ServiceRegistry.Instance.Unregister<IPatriotDroneToggleCommandService>(this);
                ServiceRegistry.Instance.Unregister<IAutoResupplyReader>(this);
                ServiceRegistry.Instance.Unregister<IAutoResupplyToggleCommandService>(this);
            }

            base.OnDestroy();
        }

        /// <summary>
        /// AA world-state boundary. CS2 loads a save into the LIVE session without
        /// destroying mod entities; without this, pre-load AirDefenseInstallation
        /// entities linger with stale building refs post-load (AACrewReleaseSystem
        /// then demolishes them, leaking manpower — the same-session "duplicate" AA).
        /// The durable AA-domain owner closes the old world's runtime AA set HERE,
        /// before deserialize recreates the authoritative set (boundary lifecycle,
        /// mirrors vanilla ClearSystem/PreDeserialize — not a post-hoc cleanup).
        /// Guarded to load/new-game: must NOT run on SaveGame (would wipe AA right
        /// before serialize) nor on map/cleanup purposes.
        /// </summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (purpose != Purpose.LoadGame && purpose != Purpose.NewGame)
                return;

            int n = m_AAClearQuery.CalculateEntityCount();
            if (n > 0)
            {
                EntityManager.DestroyEntity(m_AAClearQuery);
                Log.Info($"OnGamePreload({purpose}): cleared {n} pre-load AirDefenseInstallation at load-boundary");
            }

#pragma warning disable CIVIC458 // AirDefense UI stats uses a plain owner-side read model, not VersionedView publication.
            m_UiStatsModel = AirDefenseUiStatsSnapshot.Empty;
#pragma warning restore CIVIC458
            m_UiStatsLastCount = -1;
            m_UiStatsLastOrderVersion = 0;
        }

        protected override void OnUpdateImpl()
        {
            RefreshUiStatsSnapshotIfStructureChanged();

            bool hasSingleton;
            using (PerformanceProfiler.Measure("AirDefState.IsEmpty"))
            {
                hasSingleton = !m_SingletonQuery.IsEmpty;
            }
            if (!hasSingleton) return;

            if (!TryGetCreditsSingleton(out var singletonEntity, out var s))
                return;

            bool changed = false;

            // Policy A: project the persisted policy owner's value into the singleton
            // (non-persisted projection; cross-domain reads use IDefensePolicyReader).
            var policy = m_PolicySystem.CurrentPolicy;
            if (s.CurrentPolicy != policy)
            {
                s.CurrentPolicy = policy;
                changed = true;
            }

            using (PerformanceProfiler.Measure("AirDefState.HeritageRequests"))
            {
                changed |= ProcessHeritageRequests(ref s);
            }

            if (changed)
                WriteCreditsSingleton(singletonEntity, in s);
            else
                m_CreditsLatest = s; // keep reader mirror fresh even on a no-op frame
        }

        /// <summary>
        /// Read the persisted credit singleton synchronously (main thread, no job).
        /// </summary>
        private bool TryGetCreditsSingleton(out Entity entity, out AirDefenseCreditsSingleton s)
        {
            m_CreditsWriteLookup.Update(this);
            if (m_SingletonQuery.TryGetSingletonEntity<AirDefenseCreditsSingleton>(out entity)
                && m_CreditsWriteLookup.TryGetComponent(entity, out s))
                return true;

            entity = Entity.Null;
            s = AirDefenseCreditsSingleton.Default;
            return false;
        }

        /// <summary>
        /// Durable synchronous write of the credit singleton + reader-mirror refresh.
        /// This is the only write path (no async WriteCreditsJob).
        /// </summary>
        private void WriteCreditsSingleton(Entity entity, in AirDefenseCreditsSingleton s)
        {
            m_CreditsWriteLookup.Update(this);
            if (m_CreditsWriteLookup.HasComponent(entity))
                m_CreditsWriteLookup[entity] = s;
            m_CreditsLatest = s;
        }

        /// <summary>
        /// Apply heritage grant events and donor request entities directly to the singleton
        /// (synchronous). Same caps/peak arithmetic as before; just on the singleton,
        /// not a cache.
        /// Returns true if the singleton changed.
        /// </summary>
        private bool ProcessHeritageRequests(ref AirDefenseCreditsSingleton s)
        {
            bool hasDonorRequests = !m_GrantDonorPatriotQuery.IsEmpty;
            bool hasRequests = m_HasPendingHeritageGrantEvent || hasDonorRequests;
            if (!hasRequests) return false;

            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;
            EntityCommandBuffer EnsureEcb()
            {
                if (!ecbCreated)
                {
                    ecb = m_GameSimulationEndBarrier.CreateCommandBuffer();
                    ecbCreated = true;
                }
                return ecb;
            }

            int heritage = s.HeritageCredits;
            int heritageMax = s.HeritageCreditsMax;
            int donor = s.DonorPatriotCredits;
            int donorMax = s.DonorPatriotCreditsMax;
            bool changed = false;

            if (m_HasPendingHeritageGrantEvent)
            {
                int credits = m_PendingHeritageGrantCredits;
                int productionMW = m_PendingHeritageGrantProductionMW;

                changed |= ApplyHeritageGrant(credits, productionMW, ref heritage, ref heritageMax);

                m_HasPendingHeritageGrantEvent = false;
                m_PendingHeritageGrantCredits = 0;
                m_PendingHeritageGrantProductionMW = 0;
            }

            if (hasDonorRequests)
            {
                foreach (var (requestRef, entity) in
                    SystemAPI.Query<RefRO<GrantDonorPatriotCreditsRequest>>()
                    .WithEntityAccess())
                {
                    var request = requestRef.ValueRO;
                    if (request.Credits <= 0)
                    {
                        Log.Warn($"GrantDonorPatriotCreditsRequest with Credits={request.Credits} — ignoring");
                        EnsureEcb().DestroyEntity(entity);
                        continue;
                    }
                    donor = Math.Min(donor + request.Credits, MAX_DONOR_PATRIOT_CREDITS);
                    donorMax = Math.Max(donorMax, donor); // H1: track peak, not cumulative sum
                    Log.Info($"Donor Patriot credits granted: +{request.Credits} (total: {donor}/{MAX_DONOR_PATRIOT_CREDITS})");
                    changed = true;
                    EnsureEcb().DestroyEntity(entity);
                }
            }

            if (changed)
            {
                s.HeritageCredits = heritage;
                s.HeritageCreditsMax = heritageMax;
                s.DonorPatriotCredits = donor;
                s.DonorPatriotCreditsMax = donorMax;
            }

            return changed;
        }

        /// <summary>
        /// IPatriotDroneToggleCommandService — pause-safe synchronous SET of the persisted
        /// "Patriot intercepts drones" flag (idempotent: the caller passes the target state).
        /// Called on the UI thread from AirDefenseUISystem's trigger callback, so the toggle
        /// applies while the simulation is paused instead of waiting on a GameSimulation
        /// update that never runs in pause (the former request-entity route left the button
        /// stuck "Processing…"). This owner is the single writer of the flag, so the
        /// synchronous main-thread write here is identical in safety to the credit writes —
        /// it also refreshes m_CreditsLatest so IPatriotDroneInterceptReader sees the new
        /// value immediately.
        /// </summary>
        public void SetPatriotDroneInterceptImmediate(bool enabled)
        {
            m_CommandLookups.RefreshIfStale();

            if (!TryGetCreditsSingleton(out var entity, out var s))
            {
                Log.Warn($"Patriot drone toggle dropped (enabled={enabled}): AirDefenseCreditsSingleton is missing");
                return;
            }

            if (s.PatriotInterceptsDrones == enabled)
            {
                m_CreditsLatest = s; // already at target — keep reader mirror fresh
                return;
            }

            s.PatriotInterceptsDrones = enabled;
            WriteCreditsSingleton(entity, in s);
            Log.Info($"Patriot drone interception toggled: {(enabled ? "ON" : "OFF")}");
        }

        /// <summary>
        /// IAutoResupplyToggleCommandService — pause-safe synchronous SET of the persisted
        /// per-save "AA auto-resupply" rule (idempotent: the caller passes the target state).
        /// Called on the UI thread from AirDefenseUISystem's trigger callback, so the rule
        /// applies while the simulation is paused. This owner is the single writer of the flag,
        /// so the synchronous main-thread write here is identical in safety to the credit writes;
        /// it also refreshes m_CreditsLatest so IAutoResupplyReader sees the new value immediately.
        /// </summary>
        public void SetAutoResupplyImmediate(bool enabled)
        {
            m_CommandLookups.RefreshIfStale();

            if (!TryGetCreditsSingleton(out var entity, out var s))
            {
                Log.Warn($"AA auto-resupply toggle dropped (enabled={enabled}): AirDefenseCreditsSingleton is missing");
                return;
            }

            if (s.AutoResupplyEnabled == enabled)
            {
                m_CreditsLatest = s; // already at target — keep reader mirror fresh
                return;
            }

            s.AutoResupplyEnabled = enabled;
            WriteCreditsSingleton(entity, in s);
            Log.Info($"AA auto-resupply toggled: {(enabled ? "ON" : "OFF")}");
        }

        /// <summary>
        /// Record a successful emergency resupply for <paramref name="type"/>, persisted so the
        /// per-type resupply cooldown survives save/load. Sole writer of the per-type last-resupply
        /// stamps (same owner as the credit mirror). Called by AirDefenseActionRequestSystem when a
        /// resupply batch is accepted. The game-hour drives the (now zero, vestigial-for-Patriot)
        /// hour cooldown; <paramref name="waveNumber"/> drives the Patriot one-resupply-per-wave gate
        /// — only Patriot carries a wave cooldown, so other types ignore it.
        /// </summary>
        public void RecordResupply(AAType type, float gameHour, int waveNumber)
        {
            m_CommandLookups.RefreshIfStale();

            if (!TryGetCreditsSingleton(out var entity, out var s))
            {
                Log.Warn($"Resupply timestamp dropped (type={type}): AirDefenseCreditsSingleton is missing");
                return;
            }

            s.SetLastResupplyHour(type, gameHour);
            // Record only a REAL wave (>= 1). WaveNumber 0 (no active wave) and the singleton-absent
            // fallback both surface as 0; stamping it would let the gate's currentWave-lastWave
            // arithmetic treat a non-wave as a wave. The field domain stays {NoResupplyWave} ∪ {>= 1}.
            if (type.FiresInterceptorMissile() && waveNumber >= 1)
                s.SetLastResupplyWave(type, waveNumber);
            WriteCreditsSingleton(entity, in s);
        }

        private void OnHeritageGranted(HeritageGrantedEvent evt)
        {
            if (evt.Count <= 0)
            {
                Log.Warn($"HeritageGrantedEvent with Count={evt.Count} — ignoring");
                return;
            }

            m_PendingHeritageGrantCredits += evt.Count;
            m_PendingHeritageGrantProductionMW = Math.Max(m_PendingHeritageGrantProductionMW, evt.ProductionMW);
            m_HasPendingHeritageGrantEvent = true;
        }

        private bool ApplyHeritageGrant(int credits, int productionMW, ref int heritage, ref int heritageMax)
        {
            // ROOT FIX (re-grant inflation): the heritage grant is one-shot per
            // game. heritageMax>0 means the initial grant already applied this
            // batch, an earlier frame, or the loaded game.
            if (heritageMax > 0)
            {
                if (Log.IsDebugEnabled) Log.Debug($"Duplicate/stale heritage grant (Credits={credits}) ignored — heritage already granted this game");
                return false;
            }

            heritage = Math.Min(heritage + credits, MAX_HERITAGE_CREDITS);
            heritageMax = Math.Max(heritageMax, heritage);
            Log.Info(productionMW > 0
                ? $"Heritage credits granted: +{credits} ({productionMW} MW city)"
                : $"Heritage credits granted: +{credits}");
            return true;
        }

        /// <summary>
        /// Owner-side placement credit resolution. Called by the ModificationEnd
        /// payment system so placement can commit while paused without giving another
        /// system write access to AirDefenseCreditsSingleton.
        /// </summary>
        public void ResolvePlacementCredit(AAPlacementCreditKind kind, int placementId, out bool creditSucceeded)
        {
            creditSucceeded = false;
            if (kind == AAPlacementCreditKind.None)
                return;

            if (!TryGetCreditsSingleton(out var entity, out var s))
            {
                Log.Warn($"Credit claim could not be resolved because AirDefenseCreditsSingleton is missing (plId={placementId})");
                return;
            }

            bool changed = false;
            switch (kind)
            {
                case AAPlacementCreditKind.Heritage:
                    if (s.HeritageCredits > 0)
                    {
                        s.HeritageCredits--;
                        creditSucceeded = true;
                        changed = true;
                        if (Log.IsDebugEnabled) Log.Debug($"Heritage credit resolved: {s.HeritageCredits} remaining (plId={placementId})");
                    }
                    else
                    {
                        Log.Warn($"Heritage credit claim could not be honored (plId={placementId}) — rejecting placement, no refund");
                    }
                    break;

                case AAPlacementCreditKind.DonorPatriot:
                    if (s.DonorPatriotCredits > 0)
                    {
                        s.DonorPatriotCredits--;
                        creditSucceeded = true;
                        changed = true;
                        if (s.DonorPatriotCredits == 0 && s.DonorPatriotCreditsMax > 0)
                            PublishDonorPatriotExpired();
                        if (Log.IsDebugEnabled) Log.Debug($"Donor Patriot credit resolved: {s.DonorPatriotCredits} remaining (plId={placementId})");
                    }
                    else
                    {
                        Log.Warn($"Donor Patriot credit claim could not be honored (plId={placementId}) — rejecting placement, no refund");
                    }
                    break;

                default:
                    Log.Error($"Unexpected ReservedCreditKind={(int)kind} (plId={placementId}) — rejecting placement");
                    break;
            }

            if (changed)
                WriteCreditsSingleton(entity, in s);
            else
                m_CreditsLatest = s;
        }

        public bool RefundPlacementCredit(AAPlacementCreditKind kind, int placementId)
        {
            if (kind == AAPlacementCreditKind.None)
                return false;

            if (!TryGetCreditsSingleton(out var entity, out var s))
            {
                Log.Warn($"Credit refund dropped because AirDefenseCreditsSingleton is missing (kind={kind}, plId={placementId})");
                return false;
            }

            switch (kind)
            {
                case AAPlacementCreditKind.Heritage:
                    s.HeritageCredits = Math.Min(s.HeritageCredits + 1, MAX_HERITAGE_CREDITS);
                    s.HeritageCreditsMax = Math.Max(s.HeritageCreditsMax, s.HeritageCredits);
                    WriteCreditsSingleton(entity, in s);
                    Log.Info($"Heritage credit returned (plId={placementId})");
                    return true;

                case AAPlacementCreditKind.DonorPatriot:
                    s.DonorPatriotCredits = Math.Min(s.DonorPatriotCredits + 1, MAX_DONOR_PATRIOT_CREDITS);
                    s.DonorPatriotCreditsMax = Math.Max(s.DonorPatriotCreditsMax, s.DonorPatriotCredits);
                    WriteCreditsSingleton(entity, in s);
                    Log.Info($"Donor Patriot credit returned (plId={placementId})");
                    return true;

                default:
                    Log.Error($"Unexpected ReservedCreditKind={(int)kind} during refund (plId={placementId})");
                    return false;
            }
        }

        /// <summary>
        /// Read-only credit availability for the detector (variant B). Available =
        /// persisted credits MINUS outstanding unresolved claims of this kind (intents
        /// the detector already created but the owner has not yet decremented for).
        /// No mutation — the owner does the decrement when it resolves the claim.
        /// </summary>
        public bool IsHeritageCreditAvailable() => CreditAvailable(AAPlacementCreditKind.Heritage);

        public bool IsDonorPatriotCreditAvailable() => CreditAvailable(AAPlacementCreditKind.DonorPatriot);

        private bool CreditAvailable(AAPlacementCreditKind kind)
        {
            if (!TryGetCreditsSingleton(out _, out var s))
                return false;

            int available = kind == AAPlacementCreditKind.Heritage ? s.HeritageCredits : s.DonorPatriotCredits;

            int outstandingClaims = 0;
            using var intents = m_PlacementIntentQuery.ToComponentDataArray<BuildingPlacementIntent>(Allocator.Temp);
            for (int i = 0; i < intents.Length; i++)
            {
                var intent = intents[i];
                if (intent.Kind == BuildingPlacementKind.AirDefense
                    && intent.ReservedCreditKind == (byte)kind
                    && !intent.CreditResolved)
                    outstandingClaims++;
            }

            return (available - outstandingClaims) > 0;
        }

        private static void PublishDonorPatriotExpired()
        {
            var eventBus = ServiceRegistry.Instance.Require<IEventBus>();

            eventBus.SafePublish(new DonorEvent(DonorEventType.PatriotExpired), "AirDefenseStateSystem");
        }

        public AirDefenseCreditsSingleton GetCreditsSnapshot()
        {
            var snapshot = m_CreditsLatest;
            snapshot.CurrentPolicy = m_PolicySystem != null ? m_PolicySystem.CurrentPolicy : snapshot.CurrentPolicy;
            return snapshot;
        }

        internal AirDefenseUiStatsSnapshot GetUiStatsSnapshot()
            => m_UiStatsModel;

        internal void RecordUiStatsInstallationAdded(AAType type, int currentAmmo, int maxAmmo)
        {
            ApplyUiStatsDelta(
                stationDelta: 1,
                ammoDelta: currentAmmo,
                maxAmmoDelta: maxAmmo,
                type,
                typeDelta: 1);

            // The actual entity is created through a barrier ECB; force the next owner
            // update to re-baseline against ECS after playback without making UI scan.
            m_UiStatsLastCount = -1;
        }

        internal void RecordUiStatsInstallationRemoved(in AirDefenseInstallation aa)
        {
            ApplyUiStatsDelta(
                stationDelta: -1,
                ammoDelta: -aa.CurrentAmmo,
                maxAmmoDelta: -aa.MaxAmmo,
                aa.Type,
                typeDelta: -1);

            m_UiStatsLastCount = -1;
        }

        internal void RecordUiStatsAmmoChanged(in AirDefenseInstallation aa, int newAmmo)
        {
            int delta = newAmmo - aa.CurrentAmmo;
            if (delta == 0)
                return;

            ApplyUiStatsDelta(
                stationDelta: 0,
                ammoDelta: delta,
                maxAmmoDelta: 0,
                aa.Type,
                typeDelta: 0);
        }

        // Per-frame intercept fire spends one ammo round per shot of the firing AA's own type.
        // FireControlExecutor and BallisticDefenseSystem write CurrentAmmo-- directly into
        // ECS without touching the cache, and a value-only decrement never moves the query
        // count or order version — so the structural rebaseline cannot see it. The shot
        // counter drains per-AAType, so the cached AaAmmo total AND each per-type ammo total
        // fall in step with the live installations during a wave without an ECS scan.
        internal void RecordUiStatsAmmoSpent(in AirDefenseShotsByType shotsByType)
        {
            int total = shotsByType.Total;
            if (total <= 0)
                return;

            ApplyUiStatsDelta(
                stationDelta: 0,
                ammoDelta: -total,
                maxAmmoDelta: 0,
                AAType.HeritageBofors,
                typeDelta: 0,
                ammoSpentByType: shotsByType);
        }

        [CompletesDependency("RefreshUiStatsSnapshotIfStructureChanged: owner-side structural rebaseline for the UI stats cache; the UI getter is a pure cached read. Count/order-version gates the full ToEntityArray rebuild.")]
        private void RefreshUiStatsSnapshotIfStructureChanged()
        {
            int currentCount = m_UiStatsAAQuery.CalculateEntityCount();
            int currentOrderVersion = m_UiStatsAAQuery.GetCombinedComponentOrderVersion(includeEntityType: true);
            if (currentCount == m_UiStatsLastCount && currentOrderVersion == m_UiStatsLastOrderVersion)
                return;

            RebuildUiStatsSnapshot(currentCount, currentOrderVersion);
        }

        [CompletesDependency("RebuildUiStatsSnapshot: bounded owner-side cache rebuild on load or structural AA-set change; not called from UI. Writer paths maintain pause-time deltas without an ECS scan.")]
        private void RebuildUiStatsSnapshot(int currentCount, int currentOrderVersion)
        {
            m_UiStatsStorageInfoLookup.Update(this);
            m_UiStatsAALookup.Update(this);
            m_UiStatsDeletedLookup.Update(this);
            m_UiStatsDestroyedLookup.Update(this);

            int aaStations = 0;
            int aaAmmo = 0;
            int aaMaxAmmo = 0;
            var ammoByType = new int[AirDefenseUiStatsSnapshot.TypeCount];
            var maxAmmoByType = new int[AirDefenseUiStatsSnapshot.TypeCount];
            var countByType = new int[AirDefenseUiStatsSnapshot.TypeCount];

            using var entities = m_UiStatsAAQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_UiStatsAALookup.TryGetComponent(entities[i], out var aa))
                    continue;

                if (!TargetLiveness.IsLiveTarget(
                        aa.GetBuildingEntity(),
                        m_UiStatsStorageInfoLookup,
                        m_UiStatsDeletedLookup,
                        m_UiStatsDestroyedLookup))
                {
                    continue;
                }

                aaStations++;
                aaAmmo += aa.CurrentAmmo;
                aaMaxAmmo += aa.MaxAmmo;
                ammoByType[(int)aa.Type] += aa.CurrentAmmo;
                maxAmmoByType[(int)aa.Type] += aa.MaxAmmo;
                countByType[(int)aa.Type]++;
            }

#pragma warning disable CIVIC458 // AirDefense UI stats uses a plain owner-side read model, not VersionedView publication.
            m_UiStatsModel = new AirDefenseUiStatsSnapshot(
                aaStations,
                aaAmmo,
                aaMaxAmmo,
                ammoByType,
                maxAmmoByType,
                countByType);
#pragma warning restore CIVIC458
            m_UiStatsLastCount = currentCount;
            m_UiStatsLastOrderVersion = currentOrderVersion;
        }

        private void ApplyUiStatsDelta(
            int stationDelta,
            int ammoDelta,
            int maxAmmoDelta,
            AAType type,
            int typeDelta,
            AirDefenseShotsByType ammoSpentByType = default)
        {
            // Carry the per-type ammo arrays forward. A single-type change (install add/remove,
            // ammo-changed) applies ammoDelta/maxAmmoDelta to its own type; intercept fire
            // applies the per-type spent breakdown instead. The two paths never overlap, so the
            // per-type totals stay summed to the aggregate AaAmmo/AaMaxAmmo.
            bool spentPath = ammoSpentByType.Total > 0;
            var ammoByType = new int[AirDefenseUiStatsSnapshot.TypeCount];
            var maxAmmoByType = new int[AirDefenseUiStatsSnapshot.TypeCount];
            var countByType = new int[AirDefenseUiStatsSnapshot.TypeCount];
            for (int i = 0; i < AirDefenseUiStatsSnapshot.TypeCount; i++)
            {
                var t = (AAType)i;
                int ammo = m_UiStatsModel.GetAmmo(t);
                int maxAmmo = m_UiStatsModel.GetMaxAmmo(t);
                if (spentPath)
                {
                    ammo -= ammoSpentByType.Get(t);
                }
                else if (i == (int)type)
                {
                    ammo += ammoDelta;
                    maxAmmo += maxAmmoDelta;
                }
                ammoByType[i] = Math.Max(0, ammo);
                maxAmmoByType[i] = Math.Max(0, maxAmmo);
                countByType[i] = Math.Max(0, m_UiStatsModel.GetCount(t) + (i == (int)type ? typeDelta : 0));
            }

#pragma warning disable CIVIC458 // AirDefense UI stats uses a plain owner-side read model, not VersionedView publication.
            m_UiStatsModel = new AirDefenseUiStatsSnapshot(
                Math.Max(0, m_UiStatsModel.AaStations + stationDelta),
                Math.Max(0, m_UiStatsModel.AaAmmo + ammoDelta),
                Math.Max(0, m_UiStatsModel.AaMaxAmmo + maxAmmoDelta),
                ammoByType,
                maxAmmoByType,
                countByType);
#pragma warning restore CIVIC458
        }

        public bool IsDonorPatriotCreditCapReached => m_CreditsLatest.DonorPatriotCredits >= MAX_DONOR_PATRIOT_CREDITS;
        public bool IsAvailable => true;

        // IPatriotDroneInterceptReader — cross-domain read of the persisted player toggle.
        // Reads the synchronously-refreshed mirror (same source as the credit readers),
        // never an async/stale value. Default OFF: Patriot reserved for ballistics.
        public bool PatriotInterceptsDrones => m_CreditsLatest.PatriotInterceptsDrones;

        // IAutoResupplyReader — read of the persisted per-save auto-resupply rule from the
        // synchronously-refreshed mirror (same source as the credit readers). Default ON so a
        // fresh game / legacy save auto-restocks AA during calm unless the player opts out.
        public bool AutoResupplyEnabled => m_CreditsLatest.AutoResupplyEnabled;

        // IAirDefenseStatsReader — Core projection of the live fleet for the crisis-sweep forecast.
        // Reads the owner-side per-type UI stats model (m_UiStatsModel), which is structurally
        // rebaselined each OnUpdateImpl and delta-maintained by the install/remove writer paths, so
        // it is the same live count the panel shows — no extra ECS scan. An empty fleet (no city
        // loaded / no AA placed) yields a zero-count view, which the forecast reads as archetype
        // fallback (byte-identical to the FREE-Heritage-grant model).
        public AirDefenseFleetView Fleet => new(
            m_UiStatsModel.GetCount(AAType.HeritageBofors),
            m_UiStatsModel.GetCount(AAType.Bofors40mm),
            m_UiStatsModel.GetCount(AAType.Gepard),
            m_UiStatsModel.GetCount(AAType.PatriotSAM),
            m_UiStatsModel.GetCount(AAType.HawkSAM));

        /// <summary>
        /// ICivicSingletonOwner&lt;AirDefenseCreditsSingleton&gt; — PostLoadValidationSystem
        /// .RestoreSingletonOwners() calls this in the post-load pass, after entity deserialize.
        ///
        /// Canon C1 (ScenarioStateMachine): the entity is a runtime projection — the
        /// engine does NOT restore it (plain IComponentData). The durable credit values
        /// were read by the system codec into m_RestoredCredits. Here: ensure the single
        /// runtime entity exists, then write the restored values into it (also refreshes
        /// m_CreditsLatest). Policy is re-projected by ValidateAfterLoad, which PLVS
        /// runs immediately after this.
        /// </summary>
        public void OnLoadRestore(EntityManager entityManager)
        {
            AirDefenseCreditsSingleton.EnsureExists(entityManager);

            if (m_HasRestoredCredits && TryGetCreditsSingleton(out var e, out var s))
            {
                // Hydrate ALL persisted credit fields in one assignment. Per-field copying here
                // silently dropped newer persisted fields (AutoResupplyEnabled, the LastResupply*
                // cooldowns) → they reset to Default on every load. CurrentPolicy is a non-persisted
                // projection (Policy A) re-applied by ValidateAfterLoad / GetCreditsSnapshot, so a
                // whole-struct copy is safe and prevents this recurring "forgot a field" class.
                s = m_RestoredCredits;
                WriteCreditsSingleton(e, in s); // also refreshes m_CreditsLatest
            }
            m_HasRestoredCredits = false;
            RefreshUiStatsSnapshotIfStructureChanged();
        }

#pragma warning disable CIVIC231 // ValidateAfterLoad runs once post-load; AA installations only exist in active domain
        public void ValidateAfterLoad()
        {
#pragma warning restore CIVIC231
            RefreshUiStatsSnapshotIfStructureChanged();

            ReResolveRuntimeRefsForPostLoad();
            TryProjectPolicyAfterLoad();
            RetagZeroCrewAfterLoad();
        }

        private void ReResolveRuntimeRefsForPostLoad()
        {
            if (m_PolicySystem != null)
                return;

            try
            {
                m_PolicySystem = m_DependencyWire.RequireWired(() => FeatureRegistry.Instance.Require<AirDefensePolicySystem>());
            }
            catch (Exception ex)
            {
                Log.Warn($"ValidateAfterLoad: policy system unavailable; continuing with AA crew retag ({ex.GetType().Name})");
            }
        }

        private void TryProjectPolicyAfterLoad()
        {
            if (m_PolicySystem == null)
            {
                Log.Warn("ValidateAfterLoad: policy projection skipped because AirDefensePolicySystem is unavailable");
                return;
            }

            try
            {
            if (TryGetCreditsSingleton(out var singletonEntity, out var s))
            {
                // Policy A: project the persisted policy owner into the singleton
                // (the singleton no longer persists policy; it is a derived projection).
                s.CurrentPolicy = m_PolicySystem.CurrentPolicy;
                WriteCreditsSingleton(singletonEntity, in s);
            }
            }
            catch (Exception ex)
            {
                Log.Warn($"ValidateAfterLoad: policy projection failed; continuing with AA crew retag ({ex.GetType().Name})");
            }
        }

        private void RetagZeroCrewAfterLoad()
        {
            // Re-add RequestCrewTag for zero-crew AA installations (tag is not serialized).
            // Without this, AA with CrewAssigned=0 saved mid-assign never gets crew post-load.
#pragma warning disable CIVIC006 // One-time post-load recovery, not recurring OnUpdate
            var balance = BalanceConfig.Current;
            if (balance == null)
            {
                Log.Warn("ValidateAfterLoad: BalanceConfig.Current unavailable — AA crew retag skipped");
                return;
            }

            foreach (var (aa, entity) in
                SystemAPI.Query<RefRO<AirDefenseInstallation>>()
                .WithNone<RequestCrewTag>()
                .WithEntityAccess())
            {
                if (aa.ValueRO.CrewAssigned <= 0)
                {
                    // Authoritative source is the persisted required-crew on the installation
                    // (set at placement, survives load). The AAType→config switch is only a
                    // fallback for legacy saves written before AirDefenseInstallation.CrewRequired
                    // existed (persisted value 0), so the retag never silently diverges from the
                    // amount of manpower the installation was originally placed with.
                    int crewRequired = aa.ValueRO.CrewRequired > 0
                        ? aa.ValueRO.CrewRequired
                        : AAParams.ForType(balance, aa.ValueRO.Type).CrewRequired;
                    EntityManager.AddComponentData(entity, new RequestCrewTag { CrewRequired = crewRequired });
                }
            }
#pragma warning restore CIVIC006
        }

    }
}
