using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Diagnostics;
using CivicSurvival.Core.Components.Domain.Mobilization;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Forecast;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.AirDefense;
using CivicSurvival.Core.Interfaces.Domain.Economy;
using CivicSurvival.Core.Interfaces.Domain.Power;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Systems.Scheduling;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// In-game crisis sweep — the C# replacement for <c>Tools/crisis_model.py</c>. Consumes a
    /// <see cref="CrisisSweepRequest"/> on the pause-safe <c>PostSimulation</c> route (Axiom 14),
    /// runs one of three models (invariant / pacing / severity) using ONLY the shared
    /// <c>Core/Logic</c> balance formulas plus the generation-spam axes (saturation / fuel /
    /// surplus / demand-peak), writes <see cref="CrisisSweepResultSingleton"/>, logs a grep-able
    /// <c>[SWEEP]</c> verdict, then destroys the request entity.
    ///
    /// variant D: the sweep and the runtime call the SAME compiled formulas, so the predicted
    /// crisis can never drift from the simulated one. The archetype shape, real↔game time scale,
    /// player policy and per-drone shot count are explicit assumption params on the request
    /// (the labelled MODELING ASSUMPTIONS block of the reference tool), never baked here.
    /// </summary>
    [ActIndependent]
    [HandlesRequestKind(RequestKind.CrisisSweep)]
    [TransientConsumerReconcile(typeof(CrisisSweepRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Transient diagnostic command: the sweep only reads state and writes a transient result singleton, so a pre-consume load loss is reissuable from the panel.")]
    public partial class CrisisSweepSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CrisisSweep");

        // The forecast model (archetype presets, the three Run methods, the modeling consts) lives in
        // the pure non-ECS CrisisSweepRunner. This system is the thin orchestrator: it gathers the live
        // inputs (one-shot snapshots) into a LiveInputs struct, hands the runner its reused per-plant
        // scratch, and writes/logs the verdict the runner returns. The few consts the SYSTEM still owns
        // are below; everything else moved to the runner.

        // Worst-case plant count the per-plant severity scratch is sized to (shared single source with
        // the severity clamp — CrisisSweepRunner.MAX_PLANTS). The scratch is allocated ONCE in OnCreate
        // to MAX_PLANTS — CIVIC050 forbids growing it in OnUpdate. When live plants exceed the cap they
        // are clamped to it (the timeline samples a representative fleet).
        private const int MAX_PLANTS = CrisisSweepRunner.MAX_PLANTS;
        // Kilowatt → megawatt scale for the live power/demand snapshot reads (the snapshots quote kW).
        private const float KW_PER_MW = 1000f;

        // Pure forecast composer — instantiated once (OnCreate). Drives the three models from the
        // gathered LiveInputs + the system's reused scratch; touches no ECS itself.
        private readonly CrisisSweepRunner m_runner = new();

        private EntityQuery m_SweepRequestQuery;
        private EntityQuery m_ScenarioQuery;
        private ModCleanupBarrier m_ModCleanupBarrier = null!;

        // Tier-0 live inputs: per-input live-or-archetype auto-detect. Each reader/singleton degrades
        // to the archetype preset when it returns no data (cold start / city not loaded / not yet
        // hydrated), so the verdict is byte-identical to the archetype model when nothing is live.
        // Mobilization is an ECS singleton (queried in OnCreate, Axiom 8). Power capacity + demand
        // peak feed the live power inputs; settings/climate feed the live wave inputs. Service
        // readers resolve in OnStartRunning with ??= (Axiom 15 / CIVIC403).
        private EntityQuery m_MobilizationQuery;
        private EntityQuery m_DemandPeakQuery;
        private BufferLookup<DemandPeakBucket> m_DemandPeakBucketLookup;
        private IPowerCapacitySnapshotReader m_CapacityReader = null!;
        private ClimateState m_ClimateAdapter = null!;
        private ModSettings m_Settings = null!;
        // Live air-defense fleet reader (Axiom-5-clean bridge to AirDefenseStateSystem's per-type
        // counts). Resolved via the null-object in OnStartRunning (??=); the null-object projects a
        // zero fleet, so AirDefense-closed degrades to the archetype Heritage-grant model.
        private IAirDefenseStatsReader m_FleetReader = null!;

        // Global player "Patriot intercepts drones" toggle (Axiom-5-clean read of the persisted flag
        // AirDefenseStateSystem owns). Null-object false when AirDefense is closed — fail-closed,
        // matching the runtime targeting gate (Patriot reserved for ballistics when unavailable).
        private IPatriotDroneInterceptReader m_PatriotDroneReader = null!;

        // Live shadow-wallet reader (Phase F repair cash-gate). Resolved via the null-object in
        // OnStartRunning; the null-object reports Balance 0, so a closed shadow wallet (pre-Crisis act)
        // yields no shadow-funded repair slots — honest fail-closed. The municipal pot is read straight
        // from CityBudgetService (vanilla City Budget), no reader field needed.
        private IShadowWalletService m_WalletReader = NullShadowWalletService.Instance;

        // Live city-area queries (Phase E). Built once (Axiom 8): the built-footprint sum over
        // district areas and the owned-tile sum over purchased map tiles (vanilla Game.Areas types —
        // Axiom 5 OK, not a Domain). Geometry.m_SurfaceArea is m²; the caller converts to km².
        private EntityQuery m_DistrictAreaQuery;
        private EntityQuery m_OwnedTileQuery;
        private ComponentLookup<Game.Areas.Geometry> m_AreaGeometryLookup;

        // Reused scratch for severity first-collapse days (preallocated; Clear() per run-set).
        private readonly System.Collections.Generic.List<int> m_CollapseDays = new();

        // Per-plant severity scratch (preallocated to MAX_PLANTS in OnCreate; only the first
        // n_plants entries are live per run — handed to ForecastState as the reused buffer and reset
        // by RepairForecast.Reset, never new'd in OnUpdate (CIVIC050)). m_PlantDamage[p] = damage
        // fraction 0..1; m_RepairDone[p] = repair-completion game-hour (RepairForecast.REPAIR_NONE
        // when not under repair).
        private float[] m_PlantDamage = null!;
        private float[] m_RepairDone = null!;
        // Per-plant nameplate (MW), populated from the live PowerCapacitySnapshot.Plants[] sizes when
        // the snapshot is available; only the first n_plants entries are live per run. null is passed
        // to ForecastState in archetype-fallback mode (equal-plant discretisation, no per-plant sizes).
        private float[] m_PlantCapMW = null!;
        // Repair dispatch queue — a fixed-capacity FIFO ring of damaged plant ids plus a per-plant
        // membership flag, both sized to MAX_PLANTS in OnCreate. The ring drains O(1) (head pop, no
        // List.RemoveAt(0) shift) and the flag dedupes enqueues O(1) (no Contains scan); at most NPlants
        // ids are ever live at once so the ring never overflows. Reused across runs, never re-new'd
        // (CIVIC050) — RepairForecast.Reset rewinds head/count and clears the flags.
        private int[] m_RepairQueue = null!;
        private bool[] m_RepairQueued = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Axiom 8: queries built only in OnCreate; .Update via TryGetSingleton in OnUpdateImpl.
            // The sweep models an archetype-shaped city (assumption presets), not the live grid, so
            // it reads ScenarioSingleton (population/war-day) but never PowerGridSingleton.
            m_SweepRequestQuery = GetEntityQuery(ComponentType.ReadWrite<CrisisSweepRequest>());
            m_ScenarioQuery = GetEntityQuery(ComponentType.ReadOnly<ScenarioSingleton>());

            // Tier-0 live-input queries/lookups — built once here (Axiom 8). .Update(this) on the
            // buffer lookup and TryGetSingleton on the queries run in the update path; a missing
            // singleton/buffer simply falls the matching input back to its archetype preset.
            m_MobilizationQuery = GetEntityQuery(ComponentType.ReadOnly<MobilizationStateSingleton>());
            m_DemandPeakQuery = GetEntityQuery(ComponentType.ReadOnly<DemandPeakSingleton>());
            m_DemandPeakBucketLookup = GetBufferLookup<DemandPeakBucket>(true);

            // Live city-area queries (Phase E, Axiom 8). District = built defendable footprint; owned
            // map tiles (MapTile minus Native — vanilla removes Native on purchase, see
            // Game.Areas.MapTileSystem.AddOwner) = total purchased land, always >0 after start. Both
            // archetypes carry Game.Areas.Geometry (m_SurfaceArea m²); the owned-tile area sums via a
            // ComponentLookup over the tile entities.
            m_DistrictAreaQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.District>(),
                ComponentType.ReadOnly<Game.Areas.Geometry>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_OwnedTileQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.MapTile>(),
                ComponentType.ReadOnly<Game.Areas.Geometry>(),
                ComponentType.Exclude<Game.Common.Native>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_AreaGeometryLookup = GetComponentLookup<Game.Areas.Geometry>(true);

            // Dormant until a sweep is requested (zero overhead when none pending).
            RequireForUpdate(m_SweepRequestQuery);

            m_ModCleanupBarrier = World.GetOrCreateSystemManaged<ModCleanupBarrier>();

            // Per-plant severity scratch — allocated once, reused across every run (CIVIC050: no
            // per-OnUpdate allocation). Sized to the worst-case plant count (MAX_PLANTS).
            m_PlantDamage = new float[MAX_PLANTS];
            m_RepairDone = new float[MAX_PLANTS];
            m_PlantCapMW = new float[MAX_PLANTS];
            m_RepairQueue = new int[MAX_PLANTS];
            m_RepairQueued = new bool[MAX_PLANTS];

            // Transient output singleton — re-populated each run, HasResult=false after load.
            // EntityManager only in OnCreate (Axiom 7). Re-ensured on the load path in
            // ValidateAfterLoad, so the update path can write via SystemAPI.SetSingleton.
            CivicSingleton.Ensure(EntityManager, default(CrisisSweepResultSingleton));

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // Resolve the live-input readers with ??= (Axiom 15 / CIVIC403). Require (not TryGet) —
            // IPowerCapacitySnapshotReader is AlwaysOpen ([OwnedByFeatureId] mandates Require,
            // CIVIC463), ClimateState / ModSettings are infrastructure services. Live-vs-archetype is
            // then decided per input by whether the resolved reader RETURNS data
            // (TryGetSnapshot / hydrated singleton), not by reader nullability — same shape WaveScheduler
            // uses. The readers themselves are always present once a city is running.
            m_CapacityReader ??= ServiceRegistry.Instance.Require<IPowerCapacitySnapshotReader>();
            m_ClimateAdapter ??= ServiceRegistry.Instance.Require<ClimateState>();
            m_Settings ??= ServiceRegistry.Instance.Require<ModSettings>();
            // AirDefense fleet reader resolves to the null-object when AirDefense is closed (a
            // zero fleet), so the AA inputs degrade to the archetype Heritage-grant model — fail-closed,
            // never a hard Require that would throw before the feature registers.
            m_FleetReader ??= ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullAirDefenseStatsReader.Instance);
            // Patriot-drone toggle reader — same fail-closed null-object pattern: when AirDefense is
            // closed the null-object reports false (Patriot reserved for ballistics), so the drone-gate
            // and the ballistic-reservation degrade to the runtime's "unavailable ⇒ ballistic-only" path.
            m_PatriotDroneReader ??= ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullPatriotDroneInterceptReader.Instance);
            // Shadow wallet reader (Phase F). Same fail-closed null-object pattern: when ShadowEconomy is
            // closed the null-object reports Balance 0, so shadow-tier repair funding reads as 0.
            m_WalletReader = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullShadowWalletService.Instance);
        }

        protected override void OnUpdateImpl()
        {
            EntityCommandBuffer ecb = default;
            bool ecbCreated = false;

            // Refresh the demand-peak buffer lookup once per update (Axiom 8); the live-input readers
            // sample it lazily inside the sweep when a request is present.
            m_DemandPeakBucketLookup.Update(this);
            // Same for the live-area geometry lookup (sampled by LiveArea when a request is present).
            m_AreaGeometryLookup.Update(this);

            // Reap any request that lost its RequestMeta (crafted / partial-load entity).
            foreach (var (_, entity) in
                SystemAPI.Query<RefRO<CrisisSweepRequest>>()
                    .WithNone<RequestMeta>()
                    .WithEntityAccess())
            {
                Log.Warn("Destroyed CrisisSweepRequest without RequestMeta");
                EnsureEcb(ref ecb, ref ecbCreated);
                ecb.DestroyEntity(entity);
            }

            foreach (var (request, meta, entity) in
                SystemAPI.Query<RefRO<CrisisSweepRequest>, RefRO<RequestMeta>>()
                    .WithEntityAccess())
            {
                var req = request.ValueRO;

                // Scenario + clock are ECS-side; read them here and pass them to the pure runner.
                int populationPeak = 1000;
                int warDay = -1;
                if (m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out var scenario))
                {
                    populationPeak = math.max(scenario.PopulationPeak, 1);
                    warDay = scenario.WarDay;
                }

                double computedAt = 0.0;
                if (GameTimeSystem.TryGetGameHours(out float gameHours))
                    computedAt = gameHours;

                // Gather every live-or-archetype input ONCE (each reader is a one-shot snapshot, so the
                // pre-gathered value equals what the runner's old inline call would have returned), then
                // compose the verdict purely. TryGetLivePower (inside GatherLiveInputs) fills m_PlantCapMW.
                var live = GatherLiveInputs();
                var scratch = new CrisisSweepScratch(
                    m_PlantDamage, m_RepairDone, m_PlantCapMW, m_RepairQueue, m_RepairQueued, m_CollapseDays);
                var result = m_runner.RunSweep(req, live, scratch, populationPeak, warDay, computedAt);
                if (!result.HasResult && req.Mode != CrisisSweepMode.Invariant
                    && req.Mode != CrisisSweepMode.Pacing && req.Mode != CrisisSweepMode.Severity)
                    Log.Warn($"Unknown sweep mode {(byte)req.Mode} — no verdict computed");
                WriteResultSingleton(result);
                LogVerdict(result);

                EnsureEcb(ref ecb, ref ecbCreated);
                RequestResultEmitter.EmitSuccess(ecb, meta.ValueRO, RequestKind.CrisisSweep, SystemAPI.Time.ElapsedTime);
                // Destroy is mandatory — an undestroyed request re-fires every PostSimulation tick.
                ecb.DestroyEntity(entity);
            }

            if (ecbCreated)
                m_ModCleanupBarrier.AddJobHandleForProducer(Dependency);
        }

#pragma warning disable CIVIC145 // Lazy helper: every call site writes immediately after EnsureEcb returns.
        private void EnsureEcb(ref EntityCommandBuffer ecb, ref bool ecbCreated)
        {
            // ECB allocated lazily, inside the created-gate, so it is created only after a
            // request entity is actually present (CIVIC486 lazy-gate).
            if (!ecbCreated)
            {
                ecb = m_ModCleanupBarrier.CreateCommandBuffer();
                ecbCreated = true;
            }
        }
#pragma warning restore CIVIC145

        // ════════════════════════════════════════════════════════════════════
        // Output: result singleton + grep-able [SWEEP] verdict (Axiom 1)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Post-load re-ensure: the result singleton is transient output (HasResult=false until a
        /// sweep runs), so it is re-seeded after load before the first OnUpdateImpl. This keeps the
        /// SystemAPI.SetSingleton in WriteResultSingleton safe even if the singleton entity did not
        /// survive a load (CIVIC414/CIVIC017 — ensure on the load path, not only OnCreate).
        /// </summary>
        public void ValidateAfterLoad()
        {
            CivicSingleton.Ensure(EntityManager, default(CrisisSweepResultSingleton));
        }

        private void WriteResultSingleton(in CrisisSweepResultSingleton result)
        {
            // Seeded in OnCreate and re-ensured after load (ValidateAfterLoad), so the singleton is
            // always present here — write it in place via SetSingleton (no recreate in update path).
#pragma warning disable CIVIC017 // False positive: singleton guaranteed by OnCreate + ValidateAfterLoad (CIVIC414), SetSingleton cannot throw here.
            SystemAPI.SetSingleton(result);
#pragma warning restore CIVIC017
        }

        private void LogVerdict(in CrisisSweepResultSingleton r)
        {
            switch ((CrisisSweepMode)r.Mode)
            {
                case CrisisSweepMode.Invariant:
                    Log.Info($"[SWEEP] mode=invariant archetype={r.ArchetypeId} pop={r.PopulationPeak} warDay={r.WarDay} " +
                             $"recoverableA={(r.IsRecoverableBallisticOnly ? "true" : "false")} recoveryA={r.WorstCaseRecoveryBallisticOnly:F1} " +
                             $"recoverableB={(r.IsRecoverableMixed ? "true" : "false")} recoveryB={r.WorstCaseRecoveryMixed:F1} " +
                             $"grace={r.GraceWindowHours:F1}h freeGrant={r.FreeHeritageGrant} opAA={r.OperationalAaAtVerdict} " +
                             $"droneA={r.DroneInterceptBallisticOnly:F1} droneB={r.DroneInterceptMixed:F1} " +
                             $"ballisticA={r.BallisticInterceptBallisticOnly:F1} ballisticB={r.BallisticInterceptMixed:F1} " +
                             $"ballisticTargets={r.BallisticTargets} missilesOnDrones={r.MissilesSpentOnDrones} patriotDrones={(r.PatriotInterceptsDrones ? "true" : "false")}");
                    break;
                case CrisisSweepMode.Pacing:
                    Log.Info($"[SWEEP] mode=pacing archetype={r.ArchetypeId} pop={r.PopulationPeak} warDay={r.WarDay} " +
                             $"calmHours={r.CalmHours:F1} wavePressurePeak={r.WavePressureAtPeak:F2}");
                    break;
                case CrisisSweepMode.Severity:
                    // No warDay here: severity iterates war-day along the timeline, so a single value would mislead.
                    Log.Info($"[SWEEP] mode=severity archetype={r.ArchetypeId} pop={r.PopulationPeak} " +
                             $"samples={r.SampleCount} blackoutP={r.BlackoutProbabilityPct:F1} medianCollapseDay={r.MedianCollapseDay} unsheddableFloorMW={r.UnsheddableFloorMW} " +
                             $"repairSlots={r.RepairSlots} repairTier={r.RepairTier} repairCash={r.RepairFundingCash:N0} repairLive={(r.RepairBudgetLive ? "true" : "false")} " +
                             $"recoverableA={(r.IsRecoverableBallisticOnly ? "true" : "false")} recoveryA={r.WorstCaseRecoveryBallisticOnly:F1} " +
                             $"recoverableB={(r.IsRecoverableMixed ? "true" : "false")} recoveryB={r.WorstCaseRecoveryMixed:F1} " +
                             $"droneA={r.DroneInterceptBallisticOnly:F1} droneB={r.DroneInterceptMixed:F1} " +
                             $"ballisticA={r.BallisticInterceptBallisticOnly:F1} ballisticB={r.BallisticInterceptMixed:F1} " +
                             $"ballisticTargets={r.BallisticTargets} missilesOnDrones={r.MissilesSpentOnDrones} patriotDrones={(r.PatriotInterceptsDrones ? "true" : "false")}");
                    break;
                default:
                    Log.Warn($"[SWEEP] mode=unknown({r.Mode}) — no verdict");
                    break;
            }
        }
        // ════════════════════════════════════════════════════════════════════
        // Tier-0 live-input readers — per-input live-or-archetype auto-detect
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Live mobilization snapshot at sweep time. Available only once MobilizationSystem
        /// has hydrated the singleton (city loaded, war started). The fields already net out crew /
        /// casualties — <c>AvailableManpower = Total − Used − Casualties</c> — so callers must NOT
        /// re-subtract.</summary>
        private readonly struct LiveManpower
        {
            public readonly int Total;
            public readonly int Used;
            public readonly int Casualties;
            public readonly int Available;
            /// <summary>Live war-day the pool was snapshotted at (mobilization nets Available against
            /// this day's fatigue). -1 before the war starts. The invariant reports THIS as result.WarDay
            /// in live mode so the displayed war-day matches the manpower source.</summary>
            public readonly int WarDay;

            public LiveManpower(int total, int used, int casualties, int available, int warDay)
            {
                Total = total;
                Used = used;
                Casualties = casualties;
                Available = available;
                WarDay = warDay;
            }
        }

        /// <summary>Read the live mobilization snapshot; false when the singleton is absent (cold
        /// start / pre-war), which keeps the manpower inputs on their archetype-derived pool.</summary>
        private bool TryGetLiveManpower(out LiveManpower live)
        {
            if (m_MobilizationQuery.TryGetSingleton<MobilizationStateSingleton>(out var mob))
            {
                live = new LiveManpower(mob.TotalManpower, mob.UsedManpower, mob.Casualties, mob.AvailableManpower, mob.WarDay);
                return true;
            }
            live = default;
            return false;
        }

        /// <summary>Live power-capacity snapshot at sweep time: production (own-city dispatchable),
        /// built nameplate (MW), N+1 buffer base (largest plant MW), intermittent-type count, and the
        /// real per-plant sizes (MW). Available only when the Engineering capacity reader is resolved
        /// AND returns a snapshot; otherwise every power input stays on its archetype preset.</summary>
        private readonly struct LivePower
        {
            public readonly float ProductionMW;
            public readonly float NameplateMW;
            public readonly float LargestPlantMW;
            public readonly int IntermittentTypes;
            /// <summary>Live plant count (clamped to MAX_PLANTS); 0 ⇒ no per-plant sizes, use equal-plant.</summary>
            public readonly int NPlants;

            public LivePower(float productionMW, float nameplateMW, float largestPlantMW, int intermittentTypes, int nPlants)
            {
                ProductionMW = productionMW;
                NameplateMW = nameplateMW;
                LargestPlantMW = largestPlantMW;
                IntermittentTypes = intermittentTypes;
                NPlants = nPlants;
            }
        }

        /// <summary>True when this plant snapshot is part of the channel-correct built nameplate
        /// (<see cref="PowerCapacitySnapshot.NameplateKW"/>) — i.e. exactly the subset whose
        /// <c>OriginalCapacityKW</c> the resolver sums into nameplate. The resolver counts a plant
        /// iff <c>Channel == GridProducer &amp;&amp; !IsUnderConstruction &amp;&amp; !IsKnockedOut</c>
        /// (PowerCapacityResolverSystem.ResolveJob.cs:191-198). The snapshot now carries the resolver's
        /// own <see cref="PowerCapacityPlantSnapshot.Channel"/> verbatim, so this filter matches that
        /// inclusion field-for-field — no kind-reconstruction. The channel filter is exact where the old
        /// kind gate was an approximation: it correctly INCLUDES a pure batteryless emergency generator
        /// (genuine GridProducer, dropped before by the <c>Kind == EmergencyGenerator</c> exclusion) and
        /// correctly EXCLUDES an upgraded battery whose PPD/Wind/Solar upgrade gave it <c>mw &gt; 0</c>
        /// (EmergencyBattery channel, which the legacy <c>mw &gt; 0</c> gate let through). Knockout mirrors
        /// <c>PowerCapacityMath.IsKnockedOut</c>: collapse, an active repair window, or damage that already
        /// zeroes the multiplier (operational + disaster + explosion ≥ 1). Keeping per-plant sizes in
        /// lockstep with nameplate is what makes <c>Σ m_PlantCapMW ≈ NameplateMW</c> and <c>nPlants</c> the
        /// real fighting fleet — counting ruins / construction / import / batteries inflates both and makes
        /// the severity verdict systematically pessimistic on any city with a strike, a build or an import.
        /// NOTE: this mirrors the PUBLISHED (full-nameplate) aggregate only. The resolver-internal
        /// saturation-target aggregate uses the damage-RESIDUAL nameplate by design — do not "align" this
        /// filter to it (see the NameplateKW docstring on the intentional full/residual split).</summary>
        private static bool CountsTowardNameplate(in PowerCapacityPlantSnapshot p)
        {
            // Exactly the resolver's NameplateKW inclusion (ResolveJob.cs:191-193):
            // GridProducer channel, built (not under construction-delay), not a standing ruin
            // (snapshot-side IsKnockedOut mirrors PowerCapacityMath.IsKnockedOut).
            return p.Channel == CapacityChannel.GridProducer
                && !p.IsUnderConstruction
                && !p.IsKnockedOut;
        }

        /// <summary>Read the live power-capacity snapshot and fill the per-plant MW scratch from the
        /// real <c>OriginalCapacityKW</c> sizes (clamped to MAX_PLANTS). false when the reader is
        /// unresolved or has no snapshot (cold start / Engineering closed), keeping power on the
        /// archetype preset. nameplate must be &gt; 0 to count as live (an empty grid is not a model).</summary>
        private bool TryGetLivePower(out LivePower live)
        {
            live = default;
            if (!m_CapacityReader.TryGetSnapshot(out var cap))
                return false;

            float nameplateMW = cap.NameplateKW / KW_PER_MW;
            if (nameplateMW <= 0f)
                return false;

            // Per-plant sizes from the real fleet — counting EXACTLY the subset that sums to
            // NameplateKW (GridProducer, not under construction, not knocked out — see
            // CountsTowardNameplate). Skipping import / construction / ruins keeps Σ m_PlantCapMW in
            // lockstep with nameplate and nPlants as the real fighting fleet. Clamp the count to the
            // scratch cap — a fleet larger than MAX_PLANTS samples a representative MAX_PLANTS set.
            int nPlants = 0;
            float filledSumMW = 0f;
            var plants = cap.Plants;
            for (int i = 0; i < plants.Count && nPlants < MAX_PLANTS; i++)
            {
                var plant = plants[i];
                float mw = plant.OriginalCapacityKW / KW_PER_MW;
                if (mw <= 0f)
                    continue;
                if (!CountsTowardNameplate(plant))
                    continue;
                m_PlantCapMW[nPlants] = mw;
                filledSumMW += mw;
                nPlants++;
            }

            // Keep Σ m_PlantCapMW[0..nPlants) == nameplate exactly. They can drift apart when the real
            // producing fleet exceeds the MAX_PLANTS scratch (the tail is clamped off but nameplate is
            // the full sum) or when the kind-reconstructed CountsTowardNameplate set diverges slightly
            // from the resolver's channel-correct nameplate set. Scale the filled prefix by
            // nameplate / Σ(filled) so the timeline's damage→MW conversion (RepairForecast.PlantCap reads
            // these per-plant sizes) and the severity production normalisation (off the full nameplate)
            // stay in lockstep — the nPlants representative plants then carry the whole nameplate.
            if (nPlants > 0 && filledSumMW > 0f)
            {
                float scale = nameplateMW / filledSumMW;
                for (int i = 0; i < nPlants; i++)
                    m_PlantCapMW[i] *= scale;
            }

            // Clear the stale tail past the live prefix: the scratch is reused across calls and runs,
            // and only [0..nPlants) is filled above. Zeroing [nPlants..MAX_PLANTS) makes the buffer
            // self-consistent (a future read past nPlants reads 0, not a prior fleet's MW) instead of
            // relying on the implicit "valid only up to NPlants" contract. Cheap — one bounded loop,
            // no allocation.
            for (int i = nPlants; i < MAX_PLANTS; i++)
                m_PlantCapMW[i] = 0f;

            live = new LivePower(
                cap.CityDispatchableMW,
                nameplateMW,
                cap.LargestPlantKW / KW_PER_MW,
                cap.IntermittentTypeCount,
                nPlants);
            return true;
        }

        /// <summary>24h rolling demand peak (MW) from the DemandPeakSingleton ring; 0 when the ring is
        /// absent or cold (first 24h), which lets the caller fall back to the instantaneous demand.</summary>
        private float LiveDemandPeakMW()
        {
            // m_DemandPeakBucketLookup is refreshed once per update in OnUpdateImpl (Axiom 8).
            int peakKW = 0;
            if (m_DemandPeakQuery.TryGetSingletonEntity<DemandPeakSingleton>(out var demandPeakEntity)
                && m_DemandPeakBucketLookup.TryGetBuffer(demandPeakEntity, out var ring)
                && ring.Length == DemandPeakSingleton.BUCKETS)
            {
                for (int b = 0; b < ring.Length; b++)
                    peakKW = math.max(peakKW, ring[b].PeakKW);
            }
            return peakKW / KW_PER_MW;
        }

        /// <summary>Instantaneous demand (MW) from PowerGridSingleton; the demand-peak fallback when
        /// the 24h ring is still cold. 0 when the grid singleton is absent.</summary>
        private float LiveInstantDemandMW()
            => SystemAPI.TryGetSingleton<PowerGridSingleton>(out var grid)
                ? grid.Demand / KW_PER_MW
                : 0f;

        /// <summary>Live demand (MW) for the sweep: the 24h rolling peak, falling back to the
        /// instantaneous grid demand when the ring is still cold. Returns 0 when neither is available,
        /// letting the caller fall back to the archetype population-derived demand. Single source so
        /// invariant / pacing / severity all read demand the same way (same base as live production).</summary>
        private float LiveDemandMW()
        {
            float peakMW = LiveDemandPeakMW();
            return peakMW > 0f ? peakMW : LiveInstantDemandMW();
        }

        /// <summary>Live season frequency modifier from vanilla climate (winter/summer differ; spring/
        /// fall == DefaultFrequencyMod). The climate facade is always resolved (Require), and reads
        /// ClimateSnapshot.Default (neutral spring) when no world is bound — so this naturally returns
        /// the neutral modifier before a city is loaded, the archetype-fallback value.</summary>
        private float LiveSeasonMod()
            => WaveScalingService.GetSeasonModifier(m_ClimateAdapter.Current.Season);

        /// <summary>Live wave-frequency modifier from the player's AirAttacks difficulty preset (live
        /// mode). The archetype fallback deliberately keeps cfg.Waves.DefaultFrequencyMod (the
        /// fixed-scenario assumption); callers choose between this and the default by mode.</summary>
        private float LiveFrequencyMod(WavesConfig waves)
            => WaveForecast.LiveFrequencyMod(waves, m_Settings.AirAttacks);

        /// <summary>City-loaded readiness for the WAVE axis — true once the scenario singleton exists.
        /// The wave live inputs (player AirAttacks preset, vanilla-climate season) are profile-/world-
        /// level, NOT in the power snapshot, so they must gate on their OWN readiness rather than on the
        /// power snapshot: AirAttacks carries the player's preset even before a city is loaded, so reading
        /// it without a city-loaded gate would diverge from the archetype fallback. The scenario singleton
        /// is present iff a city is loaded (Scenario domain owns it), giving an honest, axis-local gate.
        /// When false, the wave inputs stay on DefaultFrequencyMod / neutral season — byte-identical to
        /// the archetype model.</summary>
        private bool IsCityLoaded()
            => m_ScenarioQuery.TryGetSingleton<ScenarioSingleton>(out _);

        /// <summary>Live placed fleet (per-type counts) from the AirDefense owner; false when the
        /// fleet is empty (no city loaded / no AA built / AirDefense closed ⇒ null-object zero fleet),
        /// which keeps the AA inputs on the FREE Heritage-grant archetype model.</summary>
        private bool TryGetLiveFleet(out AirDefenseForecast.FleetComposition fleet)
        {
            var view = m_FleetReader.Fleet;
            if (view.TotalAa <= 0)
            {
                fleet = default;
                return false;
            }
            fleet = new AirDefenseForecast.FleetComposition(
                view.HeritageCount, view.BoforsCount, view.GepardCount, view.PatriotCount, view.HawkCount);
            return true;
        }

        /// <summary>Live defendable city area (km²). Prefers the built district footprint (the land
        /// the player has actually zoned into districts — the area the AA grid defends); falls back to
        /// the owned-tile footprint (all purchased land, always &gt;0 once a city starts) when no
        /// district is drawn; returns 0 when neither query matches (no city loaded), which lets the
        /// caller fall back to the archetype population-derived area. m_AreaGeometryLookup is refreshed
        /// once per update in OnUpdateImpl (Axiom 8).</summary>
        private float LiveAreaKm2()
        {
            const float SQM_PER_SQKM = 1_000_000f; // m² → km²

            // The two area materializations are a diagnostic one-shot survey — the whole system is
            // gated by RequireForUpdate(m_SweepRequestQuery), so LiveAreaKm2 runs only on a manual
            // panel sweep (not per frame). Same acceptable-sync class as EffectCacheSystem's diagnostic
            // survey: the per-frame sync point CIVIC218 guards against does not apply on a click-driven
            // diagnostic. Both arrays are Allocator.Temp + using-disposed.
#pragma warning disable CIVIC218 // Diagnostic one-shot survey gated on a pending sweep request — not a per-frame hot path.
            // Owned-tile sum (purchased land) — the physical upper bound on defendable area: nothing can
            // be defended past the land the player actually owns. Iterate the tile entities and read
            // Geometry through the cached lookup (prompt-specified shape).
            float tileSqm = 0f;
            if (!m_OwnedTileQuery.IsEmptyIgnoreFilter)
            {
                using var tiles = m_OwnedTileQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < tiles.Length; i++)
                    if (m_AreaGeometryLookup.TryGetComponent(tiles[i], out var geo))
                        tileSqm += geo.m_SurfaceArea;
            }

            // District sum (built footprint). Geometry rides the same archetype as District, so the
            // component-data array is the channel-correct sum without a lookup. Painted districts can
            // overlap or include unowned land, so clamp the district footprint to the owned-tile area —
            // a district sum exceeding owned land would inflate area and under-report coverage.
            float districtSqm = 0f;
            if (!m_DistrictAreaQuery.IsEmptyIgnoreFilter)
            {
                using var geos = m_DistrictAreaQuery.ToComponentDataArray<Game.Areas.Geometry>(Allocator.Temp);
                for (int i = 0; i < geos.Length; i++)
                    districtSqm += geos[i].m_SurfaceArea;
            }
            if (districtSqm > 0f)
            {
                float clampedSqm = tileSqm > 0f ? math.min(districtSqm, tileSqm) : districtSqm;
                return clampedSqm / SQM_PER_SQKM;
            }

            // No district drawn — fall back to the owned-tile footprint (all purchased land, always >0
            // once a city starts); 0 when neither query matches (no city loaded).
            return tileSqm > 0f ? tileSqm / SQM_PER_SQKM : 0f;
#pragma warning restore CIVIC218
        }
        // ════════════════════════════════════════════════════════════════════
        // Live-input gather — one-shot snapshot handed to the pure runner
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sample EVERY live-or-archetype input ONCE per sweep and bundle it into a <see cref="LiveInputs"/>
        /// the pure <see cref="CrisisSweepRunner"/> reads instead of calling the readers inline. Each reader
        /// is a one-shot snapshot (no side effect that changes a second call's result), so pre-gathering the
        /// value here is equivalent to the runner calling the reader at the same point — the verdict stays
        /// byte-identical to the pre-split inline-reader model. <see cref="TryGetLivePower"/> additionally
        /// fills the reused <c>m_PlantCapMW</c> scratch (handed to the runner via <see cref="CrisisSweepScratch"/>),
        /// which is why it is gathered here, before the runner runs.
        ///
        /// The WAVE axis (season / frequency modifier) is gathered ONLY when a city is loaded, mirroring the
        /// old per-axis gate: when no city is loaded the runner uses <c>DefaultFrequencyMod</c> /
        /// <c>GetSeasonModifier(0)</c>, so the gathered values are left at their defaults (the runner ignores
        /// them via the <see cref="LiveInputs.CityLoaded"/> flag). Demand and area carry their raw value
        /// unconditionally; the runner gates each on whether it is &gt; 0 exactly as the inline reads did.
        /// </summary>
        private LiveInputs GatherLiveInputs()
        {
            // SINGLE-GATHER CONTRACT: area / fleet / manpower / demand / wave are gathered
            // UNCONDITIONALLY once per sweep, even for modes that never read them (e.g. the
            // pacing verdict never reads area). The values are byte-identical to the old lazy
            // inline reads the composer used to do per-mode; the only behavioural difference is
            // that the sync-materialisation (LiveAreaKm2's Temp ToEntityArray surveys) now runs
            // on every gather instead of on demand. That is intentional and harmless: gathering
            // is gated by RequireForUpdate(m_SweepRequestQuery) — it fires ONLY on a manual
            // diagnostic click, never per frame — so the extra surveys cost nothing in normal play.
            var cfg = BalanceConfig.Current;

            // MANPOWER (one-shot singleton read).
            bool manpowerLive = TryGetLiveManpower(out var liveManpower);

            // POWER (one-shot snapshot read; fills m_PlantCapMW as its scratch side effect).
            bool powerLive = TryGetLivePower(out var livePower);

            // DEMAND (24h peak → instantaneous; one-shot).
            float demandMW = LiveDemandMW();

            // WAVE — gathered only when a city is loaded (axis-local gate). When not loaded the runner
            // falls back to DefaultFrequencyMod / GetSeasonModifier(0) and never reads these fields.
            bool cityLoaded = IsCityLoaded();
            float seasonMod = cityLoaded ? LiveSeasonMod() : 0f;
            float frequencyMod = cityLoaded ? LiveFrequencyMod(cfg.Waves) : 0f;

            // FLEET (one-shot per-type counts).
            bool fleetLive = TryGetLiveFleet(out var fleet);

            // PATRIOT-DRONE TOGGLE (global player choice; null-object false when AirDefense closed).
            // Read unconditionally — it is a plain flag, not a presence-gated value, and false is the
            // correct fallback (runtime reserves Patriot for ballistics when the reader is unavailable).
            bool patriotInterceptsDrones = m_PatriotDroneReader.PatriotInterceptsDrones;

            // AREA (one-shot diagnostic survey; 0 ⇒ runner uses archetype area).
            float areaKm2 = LiveAreaKm2();

            // REPAIR BUDGET (Phase F) — snap both repair-funding pots once a city is loaded. Municipal =
            // vanilla City Budget via the sync-free cached read (CitySystem.moneyAmount, no PlayerMoney
            // sync); shadow = the shadow wallet (Balance 0 when closed pre-Crisis). The runner picks the
            // pot the chosen RepairTier draws on and derives the concurrent-repair cash gate. When no city
            // is loaded the runner keeps the request's manual MaxConcurrentRepairs stand-in (archetype).
            bool repairBudgetLive = cityLoaded;
            long municipalCash = 0;
            long shadowCash = 0;
            if (repairBudgetLive)
            {
                if (!CityBudgetService.TryGetCachedBalance(World, out municipalCash))
                    municipalCash = 0;
                shadowCash = m_WalletReader.Balance;
            }

            return new LiveInputs(
                manpowerLive,
                manpowerLive ? liveManpower.Total : 0,
                manpowerLive ? liveManpower.Used : 0,
                manpowerLive ? liveManpower.Casualties : 0,
                manpowerLive ? liveManpower.Available : 0,
                manpowerLive ? liveManpower.WarDay : -1,
                powerLive,
                powerLive ? livePower.ProductionMW : 0f,
                powerLive ? livePower.NameplateMW : 0f,
                powerLive ? livePower.LargestPlantMW : 0f,
                powerLive ? livePower.IntermittentTypes : 0,
                powerLive ? livePower.NPlants : 0,
                demandMW,
                cityLoaded, seasonMod, frequencyMod,
                fleetLive, fleet,
                patriotInterceptsDrones,
                areaKm2,
                repairBudgetLive, municipalCash, shadowCash);
        }
    }
}
