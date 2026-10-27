using System;
using System.Collections.Generic;
using Unity.Mathematics;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves threat/defense events into notification DTOs.
    /// Subscribes to EventBus, queues batched events through <see cref="ThreatNarrativeBatcher"/>,
    /// and dispatches single events through <see cref="ThreatNarrativeEmitter"/>.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class ThreatNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }

        public string Domain => "Threat";

        private const int LARGE_BATCH_THRESHOLD = 50;
        private const float MS_PER_SECOND = 1000f;
        private const float SLOW_THRESHOLD_MS = 5f;

        private static readonly LogContext Log = new("ThreatNarrativeResolver");

        private readonly NotificationState m_Sink;
        private readonly ThreatNarrativeBatcher m_Batcher = new();
        private IEventBus? m_EventBus;

        public ThreatNarrativeResolver(NotificationState sink)
        {
            m_Sink = sink;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<ThreatImpactEvent>(OnThreatImpact);
            m_EventBus.Subscribe<ThreatInterceptEvent>(OnThreatIntercept);
            m_EventBus.Subscribe<WaveEndedEvent>(OnWaveEnded);
            m_EventBus.Subscribe<ThreatNarrativeEvent>(OnThreatNarrative);
            m_EventBus.Subscribe<AAResupplyEvent>(OnAAResupply);
            m_EventBus.Subscribe<HeritageGrantedEvent>(OnHeritageGranted);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<ThreatImpactEvent>(OnThreatImpact);
            m_EventBus.Unsubscribe<ThreatInterceptEvent>(OnThreatIntercept);
            m_EventBus.Unsubscribe<WaveEndedEvent>(OnWaveEnded);
            m_EventBus.Unsubscribe<ThreatNarrativeEvent>(OnThreatNarrative);
            m_EventBus.Unsubscribe<AAResupplyEvent>(OnAAResupply);
            m_EventBus.Unsubscribe<HeritageGrantedEvent>(OnHeritageGranted);

            m_EventBus = null;
        }

        private void OnThreatNarrative(ThreatNarrativeEvent evt)
        {
            switch (evt.Type)
            {
                case ThreatNarrativeEventType.WavePhaseChanged:
                    ThreatNarrativeEmitter.EmitWavePhaseChanged(m_Sink, evt.Phase, evt.WaveNumber);
                    break;
                case ThreatNarrativeEventType.ThreatAlert:
                    ThreatNarrativeEmitter.EmitThreatAlert(m_Sink, m_EventBus, evt.WaveNumber, evt.ThreatCount);
                    break;
                case ThreatNarrativeEventType.DebrisDamage:
                    if (m_Batcher.QueueDebris(evt.Position))
                        SafeEmitDebris(m_Batcher.ForceFlushDebris());
                    break;
                case ThreatNarrativeEventType.HospitalHitScandal:
                    ThreatNarrativeEmitter.EmitHospitalScandal(m_Sink, m_EventBus, evt.Position);
                    break;
                case ThreatNarrativeEventType.PowerPlantDamaged:
                    HandlePowerPlantDamaged(evt.LostMW, evt.RemainingMW, evt.IsFirstHit, evt.AffectedPlantCount);
                    break;
                case ThreatNarrativeEventType.RepairNoFunds:
                    ThreatNarrativeEmitter.EmitRepairNoFunds(m_Sink);
                    break;
                case ThreatNarrativeEventType.AAInstallationLost:
                    ThreatNarrativeEmitter.EmitAAInstallationLost(m_Sink, evt.Position);
                    break;
                default:
                    Log.Warn($"Unhandled {nameof(ThreatNarrativeEventType)}: {evt.Type}");
                    break;
            }
        }

        private void HandlePowerPlantDamaged(int lostMW, int remainingMW, bool isFirstHit, int affectedPlantCount)
        {
            if (isFirstHit)
                ThreatNarrativeEmitter.EmitFirstStrike(m_Sink, affectedPlantCount);

            if (m_Batcher.AccumulateDamage(lostMW, remainingMW, Engine.Threats.MIN_SIGNIFICANT_LOSS_MW, out int reportMW))
                ThreatNarrativeEmitter.EmitPowerPlantDamage(m_Sink, m_EventBus, reportMW, remainingMW);
        }

        private void OnThreatIntercept(ThreatInterceptEvent evt)
        {
            if (m_Batcher.QueueIntercept(evt))
                SafeEmitIntercepts(m_Batcher.ForceFlushIntercepts());
        }

        private void OnThreatImpact(ThreatImpactEvent evt)
        {
            if (m_Batcher.QueueImpact(evt))
                SafeEmitImpacts(m_Batcher.ForceFlushImpacts());
        }

        private void OnWaveEnded(WaveEndedEvent evt)
        {
            SafeEmitImpacts(m_Batcher.ForceFlushImpacts());
            SafeEmitDebris(m_Batcher.ForceFlushDebris());

            if (m_Batcher.TryConsumePendingDamage(out int lostMW, out int remainingMW))
                ThreatNarrativeEmitter.EmitPowerPlantDamageNoSocial(m_Sink, lostMW, remainingMW);

            ThreatNarrativeEmitter.EmitWaveEnded(m_Sink, m_EventBus, evt);
        }

        private void OnAAResupply(AAResupplyEvent evt)
            => ThreatNarrativeEmitter.EmitAAResupply(m_Sink, m_EventBus, evt);

        private void OnHeritageGranted(HeritageGrantedEvent evt)
            => ThreatNarrativeEmitter.EmitHeritageGranted(m_Sink, m_EventBus, evt);

        public void Update(float currentTime)
        {
            int impactCount = m_Batcher.PendingImpactCount;
            int debrisCount = m_Batcher.PendingDebrisCount;
            int interceptCount = m_Batcher.PendingInterceptCount;

            if (impactCount == 0 && debrisCount == 0 && interceptCount == 0) return;

            float t0 = UnityEngine.Time.realtimeSinceStartup;

            if (impactCount > LARGE_BATCH_THRESHOLD || debrisCount > LARGE_BATCH_THRESHOLD || interceptCount > LARGE_BATCH_THRESHOLD)
            {
                Log.Warn($"LARGE BATCH: impacts={impactCount}, debris={debrisCount}, intercepts={interceptCount}, time={currentTime:F4}h");
            }

            try
            {
                if (m_Batcher.IsInterceptsReadyToFlush())
                    SafeEmitIntercepts(m_Batcher.FlushReadyIntercepts());
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing intercepts: {ex}");
                m_Batcher.ClearIntercepts();
            }

            try
            {
                if (m_Batcher.IsImpactsReadyToFlush())
                    SafeEmitImpacts(m_Batcher.FlushReadyImpacts());
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing impacts: {ex}");
                m_Batcher.ClearImpacts();
            }

            try
            {
                if (m_Batcher.IsDebrisReadyToFlush())
                    SafeEmitDebris(m_Batcher.FlushReadyDebris());
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing debris: {ex}");
                m_Batcher.ClearDebris();
            }

            float elapsedMs = (UnityEngine.Time.realtimeSinceStartup - t0) * MS_PER_SECOND;
            if (elapsedMs > SLOW_THRESHOLD_MS)
            {
                Log.Warn($"SLOW Update={elapsedMs:F0}ms impacts={impactCount} debris={debrisCount} intercepts={interceptCount}");
            }
        }

        public void Reset() => m_Batcher.Reset();

        public void FlushAll()
        {
            var intercepts = m_Batcher.ForceFlushIntercepts();
            if (intercepts.Count > 0)
                SafeEmitIntercepts(intercepts);

            var impacts = m_Batcher.ForceFlushImpacts();
            if (impacts.Count > 0)
                SafeEmitImpacts(impacts);

            var debris = m_Batcher.ForceFlushDebris();
            if (debris.Count > 0)
                SafeEmitDebris(debris);

            if (m_Batcher.TryConsumePendingDamage(out int lostMW, out int remainingMW))
                ThreatNarrativeEmitter.EmitPowerPlantDamageNoSocial(m_Sink, lostMW, remainingMW);
        }

        private void SafeEmitImpacts(IReadOnlyList<ThreatImpactEvent> impacts)
        {
            if (impacts.Count == 0) return;
            try { ThreatNarrativeEmitter.EmitImpacts(m_Sink, m_EventBus, impacts); }
            catch (Exception ex) { Log.Error($"Error emitting impacts: {ex}"); }
        }

        private void SafeEmitIntercepts(IReadOnlyList<ThreatInterceptEvent> intercepts)
        {
            if (intercepts.Count == 0) return;
            try { ThreatNarrativeEmitter.EmitIntercepts(m_Sink, m_EventBus, intercepts); }
            catch (Exception ex) { Log.Error($"Error emitting intercepts: {ex}"); }
        }

        private void SafeEmitDebris(IReadOnlyList<float3> debris)
        {
            if (debris.Count == 0) return;
            try { ThreatNarrativeEmitter.EmitDebris(m_Sink, m_EventBus, debris); }
            catch (Exception ex) { Log.Error($"Error emitting debris: {ex}"); }
        }
    }
}
