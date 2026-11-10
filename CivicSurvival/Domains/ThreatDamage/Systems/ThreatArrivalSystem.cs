using Game;
using Game.Common;
using CivicSurvival.Core.Features.CrossDomain.ThreatsAirDefense;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Components.Lifecycle;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Interfaces.Threats;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Types;
using System.Threading;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Constants;
using CivicSurvival.Core.Systems.Scheduling;

namespace CivicSurvival.Domains.ThreatDamage.Systems
{
    /// <summary>
    /// Detects threat arrivals from pre-filtered NativeList (IThreatArrivalSource).
    /// ZERO SystemAPI.Query&lt;&gt; calls — ZERO job sync points.
    /// Singleton accessors (TryGetSingleton[RW]) used for stats do not trigger CompleteDependency.
    ///
    /// TMS fills ArrivedThreats during Apply loop (Shahed) and after ballistic Complete.
    /// TAS reads the list, processes impacts/debris, and issues ECB commands.
    ///
    /// Runs every 16 ticks, offset 12 = 2 ticks after TMS (offset 10).
    ///
    /// Producers append to a producer-owned impact buffer. ThreatDamageSystem
    /// explicitly transfers that buffer into consumer ownership only when it can
    /// process impacts, then completes the transfer to clear the consumer buffer.
    /// </summary>
    [ActIndependent]
    public partial class ThreatArrivalSystem : CivicSystemBase, IResettable, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("ThreatArrivalSystem");

        // ECB command counter (encapsulated to avoid CA2211)
        private static int s_EcbCommandCount;
        public static int EcbCommandCount => Volatile.Read(ref s_EcbCommandCount);
        public static void ResetCounters() => Interlocked.Exchange(ref s_EcbCommandCount, 0);

        // Match TMS cadence: positions change every 16 ticks, no point checking more often.
        // Offset 12 = 2 ticks after TMS (offset 10) — arrival list already filled.
        private const int UPDATE_INTERVAL = 16;
        private const int UPDATE_OFFSET = 12;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => UPDATE_INTERVAL;
        public override int GetUpdateOffset(SystemUpdatePhase phase) => UPDATE_OFFSET;

        private IThreatArrivalSource m_ArrivalSource = null!;
        [System.NonSerialized] private CivicDependencyWire m_DependencyWire = null!;
        private EntityStorageInfoLookup m_StorageInfoLookup;
        private ComponentLookup<ActiveThreat> m_ActiveThreatLookup;
        private ComponentLookup<Deleted> m_DeletedLookup;
        private ComponentLookup<PendingDestruction> m_PendingDestructionLookup;
        private ComponentLookup<Shahed> m_ShahedLookup;
        private ComponentLookup<ShahedCombatState> m_ShahedCombatStateLookup;
        private ComponentLookup<Ballistic> m_BallisticLookup;
        private ComponentLookup<BallisticInterceptState> m_BallisticInterceptLookup;
        // Faction gate: an outbound player counter-strike (enabled PlayerOutboundThreat) reaching
        // its terminal point must NOT route into city terminalization / ThreatImpactData. Its
        // axis-effect resolution belongs to the outbound arrival channel (a later phase). Inbound
        // waves (bit absent/disabled) pass exactly as before.
        private ComponentLookup<PlayerOutboundThreat> m_PlayerOutboundLookup;
        // Outbound axis payload carried by a player counter-strike; read at the frontier to emit
        // the deferred enemy-axis signal (OutboundArrivalSignal) before terminalizing the render
        // entity (DebugDeleteOnly — no city impact, render-safe).
        private ComponentLookup<OutboundStrikePayload> m_OutboundPayloadLookup;
        private EntityQuery m_ArrivalSignalQuery;
        [System.NonSerialized] private IThreatTerminalizationSink m_TerminalizationQueue = null!;

        private NativeList<ThreatImpactData> m_ProducerImpacts;
        private NativeList<ThreatImpactData> m_ConsumerImpacts;
        private bool m_ConsumerOwnsImpacts;

        /// <summary>
        /// True when producer-owned impacts are waiting for ThreatDamageSystem to
        /// transfer them. Consumer-owned impacts are intentionally excluded:
        /// they are already checked out and must be completed by the consumer.
        /// </summary>
        public bool HasPendingImpacts => m_ProducerImpacts.IsCreated && m_ProducerImpacts.Length > 0;

        public void ResetState()
        {
            ResetImpactBuffers();
            ResetCounters();
        }

        public void ValidateAfterLoad()
        {
            ResetImpactBuffers();
        }

        private void ResetImpactBuffers()
        {
            if (m_ProducerImpacts.IsCreated) m_ProducerImpacts.Clear();
            if (m_ConsumerImpacts.IsCreated) m_ConsumerImpacts.Clear();
            m_ConsumerOwnsImpacts = false;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_DependencyWire = new CivicDependencyWire(nameof(ThreatArrivalSystem));
            m_StorageInfoLookup = GetEntityStorageInfoLookup();
            m_ActiveThreatLookup = GetComponentLookup<ActiveThreat>(true);
            m_DeletedLookup = GetComponentLookup<Deleted>(true);
            m_PendingDestructionLookup = GetComponentLookup<PendingDestruction>(true);
            m_ShahedLookup = GetComponentLookup<Shahed>(true);
            m_ShahedCombatStateLookup = GetComponentLookup<ShahedCombatState>(true);
            m_BallisticLookup = GetComponentLookup<Ballistic>(true);
            m_BallisticInterceptLookup = GetComponentLookup<BallisticInterceptState>(true);
            m_PlayerOutboundLookup = GetComponentLookup<PlayerOutboundThreat>(true);
            m_OutboundPayloadLookup = GetComponentLookup<OutboundStrikePayload>(true);
            m_ArrivalSignalQuery = GetEntityQuery(ComponentType.ReadWrite<OutboundArrivalSignal>());

            // PERF: Skip entirely when no active threats
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<ActiveThreat>()));

            m_ProducerImpacts = new NativeList<ThreatImpactData>(32, Allocator.Persistent);
            m_ConsumerImpacts = new NativeList<ThreatImpactData>(32, Allocator.Persistent);
            m_ArrivalSource = NullThreatArrivalSource.Instance;

            Log.Info(" Created (zero queries, IThreatArrivalSource, 16-tick interval)");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            // Resolve cross-domain service (TMS may not be registered in OnCreate yet)
            if (!TryWireArrivalSource())
            {
                // Expected on cold start: ThreatFlight feature may register later in the boot
                // order. OnUpdateImpl retries every tick while m_ArrivalSource is the null-object;
                // downstream IsCreated guards keep the read path safe in the meantime.
                Log.Info(" ThreatArrivalSource not yet wired in OnStartRunning — will retry from OnUpdateImpl");
            }
        }

        protected override void OnStopRunning()
        {
            // Transfer hit arrivals to the producer impact buffer — entities may be invalid for ECB
            // (ActiveThreat removed by InterceptProcessingSystem), but impact position data
            // is still valid for damage processing by ThreatDamageSystem.
            if (m_ArrivalSource.IsCreated
                && m_ArrivalSource.ArrivalCount > 0)
            {
                var arrivals = m_ArrivalSource.ArrivedThreats;
                int count = m_ArrivalSource.ArrivalCount;
                int transferred = 0;

                // Intercepted/coasting threats are neutralized (deferred-intercept) and must NEVER deal
                // city damage — even when their arrival lands in this shutdown drain. OnUpdateImpl gates
                // this via CanProcessArrival / TryResolveAwaitingArrival; mirror the IsIntercepted gate
                // here so the symmetric path is not the one that leaks damage from a "shot-down" drone.
                m_ShahedCombatStateLookup.Update(this);
                m_BallisticInterceptLookup.Update(this);
                // Faction gate also applies to the shutdown drain: an outbound projectile caught in
                // this path must not be transferred as a city impact (mirror of CanProcessArrival).
                m_PlayerOutboundLookup.Update(this);

                for (int i = 0; i < count; i++)
                {
                    var arrival = arrivals[i];
                    if (arrival.IsHit && !IsInterceptedArrival(arrival) && !IsOutbound(arrival.Entity))
                    {
                        float severity = arrival.IsBallistic
                            ? arrival.DamageSeverity
                            : ThreatConstants.SHAHED_IMPACT_SEVERITY;
                        float radius = arrival.IsBallistic ? arrival.ImpactRadius : 0f;
                        EnqueuePendingImpact(ThreatImpactData.FromArrival(in arrival, severity, radius));
                        transferred++;
                    }
                    // Crashed + intercepted/coasting arrivals: skip — no damage (the latter is neutralized).
                }

                if (transferred > 0)
                    Log.Info($"OnStopRunning: transferred {transferred} hit impacts to producer buffer");
                if (count > transferred)
                    Log.Info($"OnStopRunning: discarded {count - transferred} crashed arrivals (ECB unsafe)");

                m_ArrivalSource.ConsumeAndClear();
            }
            base.OnStopRunning();
        }

        protected override void OnDestroy()
        {
            if (m_ProducerImpacts.IsCreated) m_ProducerImpacts.Dispose();
            if (m_ConsumerImpacts.IsCreated) m_ConsumerImpacts.Dispose();

            base.OnDestroy();
            Log.Info(" Destroyed");
        }

#pragma warning disable CIVIC187 // Pending impacts are retained until ThreatDamageSystem consumes and clears them.
        protected override void OnUpdateImpl()
        {
            if (ReferenceEquals(m_ArrivalSource, NullThreatArrivalSource.Instance)
                && !TryWireArrivalSource())
            {
                // Still pending — keep the null-object in place. The `m_ArrivalSource.IsCreated`
                // guard below short-circuits the read loop so processing safely no-ops until
                // ThreatFlight finishes wiring. No log here: this branch runs every tick on
                // cold start until resolution succeeds.
            }

            using (PerformanceProfiler.Measure("SP:TAS.LookupSync"))
            {
                m_StorageInfoLookup.Update(this);
                m_ActiveThreatLookup.Update(this);
                m_DeletedLookup.Update(this);
                m_PendingDestructionLookup.Update(this);
                m_ShahedLookup.Update(this);
                m_ShahedCombatStateLookup.Update(this);
                m_BallisticLookup.Update(this);
                m_BallisticInterceptLookup.Update(this);
                m_PlayerOutboundLookup.Update(this);
                m_OutboundPayloadLookup.Update(this);
            }
            m_TerminalizationQueue ??= ServiceRegistry.Instance.Require<IThreatTerminalizationSink>();
            var config = BalanceConfig.Current.Threats;
            int queued = 0;

            // ============================================================
            // Process all arrivals from IThreatArrivalSource (NativeList — zero sync points)
            // TMS filled this during Apply loop (Shahed) and STEP 1c (Ballistic).
            // No SystemAPI.Query — no CompleteDependency — no sync points.
            // ============================================================
            if (m_ArrivalSource.IsCreated)
            {
            using (PerformanceProfiler.Measure("TAS.ReadList"))
            {
                const float SHAHED_IMPACT_SEVERITY = ThreatConstants.SHAHED_IMPACT_SEVERITY;
                var arrivals = m_ArrivalSource.ArrivedThreats;
                int count = m_ArrivalSource.ArrivalCount;

                // Outbound arrival signal buffer (GridWarfare consumer's host). Resolved once per
                // tick; may be absent (GridWarfare closed) — then an outbound projectile is just
                // terminalized with no axis effect.
                bool hasSignalBuffer = m_ArrivalSignalQuery.TryGetSingletonBuffer<OutboundArrivalSignal>(out var arrivalSignals, isReadOnly: false);

                for (int i = 0; i < count; i++)
                {
                    var arrival = arrivals[i];

                    // Outbound branch: a player counter-strike that reached the frontier never deals
                    // city damage. Emit its deferred enemy-axis signal (read off OutboundStrikePayload)
                    // and terminalize the render entity (DebugDeleteOnly — no impact, no debris, no
                    // explosion, render-safe via PendingThreatDeletion). The GridWarfare effect owner
                    // resolves the signal against a mirror-city target after its intercept roll.
                    if (TryResolveOutboundArrival(arrival, hasSignalBuffer ? arrivalSignals : default, hasSignalBuffer))
                    {
                        queued++;
                        continue;
                    }
                    // Backstop (trigger #3): a coasting Patriot-intercepted threat that reached its
                    // target / exhausted. The interceptor never resolved it (no missile spawned, or it
                    // despawned and the threat coasted on). Terminalize as an intercept at the target —
                    // explosion + render delete, NO damage (it is neutralized). Sink dedups, so a
                    // missile-arrival trigger that already queued it makes this a no-op.
                    if (TryResolveAwaitingArrival(arrival))
                    {
                        queued++;
                        continue;
                    }
                    if (!CanProcessArrival(arrival))
                        continue;

                    if (arrival.IsHit)
                    {
                        float severity = arrival.IsBallistic ? arrival.DamageSeverity : SHAHED_IMPACT_SEVERITY;
                        float radius = arrival.IsBallistic ? arrival.ImpactRadius : 0f;
                        m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
                        {
                            Entity = arrival.Entity,
                            Kind = ThreatTerminalOutcomeKind.DirectHit,
                            Position = arrival.Position,
                            EventPosition = arrival.TargetPosition,
                            IsBallistic = arrival.IsBallistic,
                            ImpactRadius = radius,
                            DamageSeverity = severity,
                            ThreatGeneration = arrival.ThreatGeneration
                        });
                        queued++;
                    }
                    else
                    {
                        if (arrival.IsBallistic && arrival.ImpactRadius > 0f && arrival.DamageSeverity > 0f)
                        {
                            m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
                            {
                                Entity = arrival.Entity,
                                Kind = ThreatTerminalOutcomeKind.BallisticExhaustedImpact,
                                Position = arrival.Position,
                                EventPosition = arrival.Position,
                                IsBallistic = true,
                                ImpactRadius = arrival.ImpactRadius,
                                DamageSeverity = arrival.DamageSeverity,
                                ThreatGeneration = arrival.ThreatGeneration
                            });
                        }
                        else if (arrival.IsBallistic)
                        {
                            m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
                            {
                                Entity = arrival.Entity,
                                Kind = ThreatTerminalOutcomeKind.BallisticExhaustedDeleteOnly,
                                Position = arrival.Position,
                                EventPosition = arrival.Position,
                                IsBallistic = true,
                                ThreatGeneration = arrival.ThreatGeneration
                            });
                        }
                        else
                        {
                            m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
                            {
                                Entity = arrival.Entity,
                                Kind = ThreatTerminalOutcomeKind.ShahedExhausted,
                                Position = arrival.Position,
                                EventPosition = arrival.Position,
                                DebrisFallTime = config.DebrisFallTime,
                                ThreatGeneration = arrival.ThreatGeneration
                            });
                        }
                        queued++;
                    }
                }
            }
            }

            // Consumer-owned clear: TMS (Interval=16) appends arrivals every 16 ticks,
            // TAS (Interval=16) reads and clears. Without this, arrivals accumulate
            // and get reprocessed on the next TAS tick.
            if (m_ArrivalSource.IsCreated)
            {
                m_ArrivalSource.ConsumeAndClear();
            }

            if (queued > 0 && Log.IsDebugEnabled)
                Log.Debug($"Queued {queued} terminal arrival outcome(s)");
        }
#pragma warning restore CIVIC187

        public void EnqueuePendingImpact(ThreatImpactData impact)
        {
            m_ProducerImpacts.Add(impact);
        }

        /// <summary>
        /// Transfer producer-owned impacts to the consumer buffer. The consumer
        /// must call CompletePendingImpactTransfer after enqueueing. ThreatDamageIntakeSystem
        /// transfers unconditionally; ThreatDamageSystem retains its apply queue until the
        /// building cache is ready.
        /// </summary>
        public bool TryTransferPendingImpacts(out NativeList<ThreatImpactData> impacts)
        {
            if (m_ConsumerOwnsImpacts)
            {
                impacts = m_ConsumerImpacts;
                return impacts.IsCreated && impacts.Length > 0;
            }

            if (!HasPendingImpacts)
            {
                impacts = default;
                return false;
            }

            (m_ConsumerImpacts, m_ProducerImpacts) = (m_ProducerImpacts, m_ConsumerImpacts);
            m_ProducerImpacts.Clear();
            m_ConsumerOwnsImpacts = true;
            impacts = m_ConsumerImpacts;
            return impacts.Length > 0;
        }

        public void CompletePendingImpactTransfer()
        {
            if (!m_ConsumerOwnsImpacts)
                return;

            m_ConsumerImpacts.Clear();
            m_ConsumerOwnsImpacts = false;
        }

        /// <summary>
        /// True if the arrival's threat is an intercepted (deferred-coast or gun) kill — neutralized,
        /// must not deal city damage. Used by the OnStopRunning shutdown drain, which bypasses
        /// CanProcessArrival's IsIntercepted gate.
        /// </summary>
        private bool IsInterceptedArrival(ThreatArrivalInfo arrival)
        {
            if (arrival.IsBallistic)
                return m_BallisticInterceptLookup.TryGetComponent(arrival.Entity, out var bis) && bis.IsIntercepted;
            return m_ShahedCombatStateLookup.TryGetComponent(arrival.Entity, out var cs) && cs.IsIntercepted;
        }

        /// <summary>
        /// True when the threat is a player outbound counter-strike (enabled
        /// <see cref="PlayerOutboundThreat"/>). Such a projectile never deals city damage — its
        /// terminal effect is the enemy-axis hit resolved by the outbound arrival channel (later
        /// phase), not city terminalization. Inbound waves (bit absent/disabled) return false and
        /// follow the existing path unchanged.
        /// </summary>
        private bool IsOutbound(Entity entity)
        {
            // PlayerOutboundThreat is enableable and lives in the shared threat archetype, so
            // HasComponent is true for every threat (inbound included). Pair it with
            // IsComponentEnabled so only the enabled (outbound) bit counts — CIVIC485.
            return m_PlayerOutboundLookup.HasComponent(entity)
                   && m_PlayerOutboundLookup.IsComponentEnabled(entity);
        }

        /// <summary>
        /// Resolves a player outbound counter-strike that reached the frontier: emits its deferred
        /// enemy-axis signal (from <see cref="OutboundStrikePayload"/>) into the GridWarfare
        /// consumer's <see cref="OutboundArrivalSignal"/> buffer, then terminalizes the render
        /// entity with <see cref="ThreatTerminalOutcomeKind.DebugDeleteOnly"/> (render delete only —
        /// NO city impact, debris, or explosion). Returns true when this was an outbound arrival
        /// (handled), false for inbound waves (the normal city path takes over). When the signal
        /// buffer is absent (GridWarfare closed), the projectile is still terminalized but no axis
        /// effect is emitted.
        /// </summary>
        private bool TryResolveOutboundArrival(ThreatArrivalInfo arrival, DynamicBuffer<OutboundArrivalSignal> signals, bool hasSignalBuffer)
        {
            var entity = arrival.Entity;
            if (!m_StorageInfoLookup.Exists(entity))
                return false;
            if (!IsOutbound(entity))
                return false; // inbound waves fall through to the normal city arrival path
            if (m_DeletedLookup.HasComponent(entity)
                || (m_PendingDestructionLookup.HasComponent(entity) && m_PendingDestructionLookup.IsComponentEnabled(entity)))
                return true; // already terminalizing — consume the arrival, nothing more to do
            if (!m_ActiveThreatLookup.HasComponent(entity) || !m_ActiveThreatLookup.IsComponentEnabled(entity))
                return true;

            // Only act on a genuine arrival (the projectile reached its frontier target). For a
            // drone that is Shahed.IsArrived; for a ballistic that is Ballistic.IsArrived. If it is
            // not yet arrived, leave it in flight (return false would route it into the inbound
            // path — instead consume nothing and let the next tick re-evaluate).
            bool arrived = arrival.IsBallistic
                ? (m_BallisticLookup.TryGetComponent(entity, out var bal) && bal.IsArrived)
                : (m_ShahedLookup.TryGetComponent(entity, out var sh) && sh.IsArrived);
            if (!arrived)
                return false;

            if (hasSignalBuffer && m_OutboundPayloadLookup.TryGetComponent(entity, out var payload))
            {
                signals.Add(new OutboundArrivalSignal { Axis = payload.Axis, Damage = payload.Damage, Seed = payload.Seed, TargetId = payload.TargetId });
            }

            // Render-safe terminalization: render delete only, no city effect (sink flips
            // PendingThreatDeletion; ThreatDeletionApplySystem does the structural delete in
            // Modification4). Sink dedups via IThreatLifecycleDedup.
            m_TerminalizationQueue.Queue(new ThreatTerminalOutcome
            {
                Entity = entity,
                Kind = ThreatTerminalOutcomeKind.DebugDeleteOnly,
                Position = arrival.Position,
                EventPosition = arrival.Position,
                IsBallistic = arrival.IsBallistic,
                ThreatGeneration = arrival.ThreatGeneration
            });
            return true;
        }

        private bool CanProcessArrival(ThreatArrivalInfo arrival)
        {
            var entity = arrival.Entity;
            if (!m_StorageInfoLookup.Exists(entity))
                return false;
            // Faction gate: outbound projectiles never terminalize into the city.
            if (IsOutbound(entity))
                return false;
            if (!m_ActiveThreatLookup.HasComponent(entity) || !m_ActiveThreatLookup.IsComponentEnabled(entity))
                return false;
            if (m_DeletedLookup.HasComponent(entity)
                || (m_PendingDestructionLookup.HasComponent(entity) && m_PendingDestructionLookup.IsComponentEnabled(entity)))
                return false;

            if (arrival.IsBallistic)
            {
                return m_BallisticLookup.TryGetComponent(entity, out var ballistic)
                    && ballistic.IsArrived
                    && (!m_BallisticInterceptLookup.TryGetComponent(entity, out var interceptState) || !interceptState.IsIntercepted);
            }

            return m_ShahedLookup.TryGetComponent(entity, out var shahed)
                && m_ShahedCombatStateLookup.TryGetComponent(entity, out var combatState)
                && !combatState.IsIntercepted
                && shahed.IsArrived;
        }

        /// <summary>
        /// Backstop for a Patriot-intercepted, still-coasting threat (AwaitingInterceptorImpact) that
        /// reached its target / exhausted before its interceptor resolved it (no missile spawned, or it
        /// despawned mid-tail-chase). Terminalizes as an intercept at the target — NO damage (the threat
        /// is neutralized). Runs BEFORE CanProcessArrival, which would otherwise drop the arrival because
        /// IsIntercepted is set. Sink dedups, so a missile-arrival/despawn trigger already queued makes
        /// this a no-op. Generation comes from the arrival record (no clock read). Cross-domain via the
        /// Core sink + factory (Axiom 5).
        /// </summary>
        private bool TryResolveAwaitingArrival(ThreatArrivalInfo arrival)
        {
            var entity = arrival.Entity;
            if (!m_StorageInfoLookup.Exists(entity))
                return false;
            // Faction gate: an outbound projectile is never a city-side Patriot-coast backstop.
            if (IsOutbound(entity))
                return false;
            if (m_DeletedLookup.HasComponent(entity)
                || (m_PendingDestructionLookup.HasComponent(entity) && m_PendingDestructionLookup.IsComponentEnabled(entity)))
                return false;
            if (!m_ActiveThreatLookup.HasComponent(entity) || !m_ActiveThreatLookup.IsComponentEnabled(entity))
                return false;

            bool awaiting = arrival.IsBallistic
                ? (m_BallisticInterceptLookup.TryGetComponent(entity, out var bis) && bis.AwaitingInterceptorImpact)
                : (m_ShahedCombatStateLookup.TryGetComponent(entity, out var cs) && cs.AwaitingInterceptorImpact);
            if (!awaiting)
                return false;

            m_TerminalizationQueue.Queue(ThreatTerminalOutcome.Intercept(
                entity,
                arrival.Position,
                arrival.IsBallistic,
                arrival.ThreatGeneration,
                debrisFallTime: arrival.IsBallistic ? 0f : BalanceConfig.Current.Threats.DebrisFallTime));
            return true;
        }

        private bool TryWireArrivalSource()
        {
            return m_DependencyWire.EnsureWired(() =>
            {
                m_ArrivalSource = ServiceRegistryFeatureExtensions.TryGetOrNullObject(NullThreatArrivalSource.Instance);
                return !ReferenceEquals(m_ArrivalSource, NullThreatArrivalSource.Instance);
            });
        }
    }
}
