using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Serialization;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Domains.Refugees.Systems
{
    /// <summary>
    /// Coordinates refugee influx based on scenario acts.
    /// Subscribes to ActChangedEvent via EventBus (no direct Scenario dependency).
    /// Does NOT decide when acts change — only reacts.
    ///
    /// Spawn rate multipliers by act:
    /// - PreWar (Village): 0.3x (slow trickle before war)
    /// - Crisis: 2.0x (double spawn during crisis)
    /// - Adaptation: 0.5x (reduced spawn)
    /// - Routine: 0.1x (minimal spawn)
    ///
    /// For Village scenario: starts refugees in PreWar phase (not waiting for Crisis).
    /// This gives player something to do and explains the situation narratively.
    ///
    /// Activates RefugeeSpawnSystem when:
    /// - Village ONLY: ScenarioTypeDetectedEvent (decision from Scenario domain)
    /// - Town/City: NEVER (people LEAVE via Exodus, not arrive as refugees)
    ///
    /// ARCHITECTURE: Scenario domain decides activation via ScenarioTypeDetectedEvent.
    /// This coordinator only adjusts SpawnRateMultiplier based on act changes.
    ///
    /// This is a coordinator (not a worker system) — it coordinates behavior
    /// but delegates actual spawn logic to RefugeeSpawnSystem.
    ///
    /// NOTE: No RegisterAfter] needed — OnUpdate() is empty, all logic is in event handlers.
    /// </summary>
    public partial class RefugeeInfluxCoordinator : CivicSystemBase, IDefaultSerializable, IResettable, IPostLoadValidation, IBootDefaultsReset
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("RefugeeInfluxCoordinator");

        /// <summary>Current spawn rate multiplier based on act.</summary>
        public float SpawnRateMultiplier { get; private set; } = 1f;

        private bool m_InfluxActivated = false;

        /// <summary>Called by RefugeeSpawnSystem.CompleteInflux when influx finishes.</summary>
        public void ClearInfluxActivated() { m_InfluxActivated = false; }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Subscribe to orchestration events
            SubscribeRequired<ActChangedEvent>(OnActChanged);
            SubscribeRequired<ScenarioTypeDetectedEvent>(OnScenarioDetected);

            Log.Info("Created");
        }

        protected override void OnDestroy()
        {
            UnsubscribeSafe<ActChangedEvent>(OnActChanged);
            UnsubscribeSafe<ScenarioTypeDetectedEvent>(OnScenarioDetected);

            base.OnDestroy();
        }

        /// <summary>
        /// Handle ScenarioTypeDetectedEvent — start slow refugee trickle for Village scenario.
        /// This happens in PreWar phase, before war actually starts.
        /// Gives player something to do and explains the situation narratively.
        /// </summary>
        private void OnScenarioDetected(ScenarioTypeDetectedEvent evt)
        {
            // Only for Village scenario — Town/City wait for Crisis act
            if (evt.Type != ScenarioType.Village)
                return;

            if (m_InfluxActivated)
            {
                Log.Debug("Already activated, ignoring ScenarioTypeDetectedEvent");
                return;
            }

            m_InfluxActivated = true;
            SpawnRateMultiplier = BalanceConfig.Current.Scenario.RefugeePrewarMultiplier;  // 0.3x slow trickle

            var spawnSystem = World.GetExistingSystemManaged<RefugeeSpawnSystem>();
            if (spawnSystem != null)
            {
                spawnSystem.StartRefugeeInflux();
                Log.Info($"Village PreWar — slow refugee trickle started (rate={SpawnRateMultiplier}x)");
            }
            else
            {
                Log.Error("RefugeeSpawnSystem not found!");
            }

            var costSystem = World.GetExistingSystemManaged<RefugeeSupportCostSystem>();
            if (costSystem != null)
            {
                costSystem.AnchorDeductionTimer();
                Log.Info("Anchored RefugeeSupportCostSystem timer for Village PreWar influx");
            }
        }

        /// <summary>
        /// React to act changes — adjust spawn rate multiplier only.
        /// BUG-HOMELESS-SPIKE FIX: Removed activation logic. Activation decision
        /// belongs to Scenario domain (via ScenarioTypeDetectedEvent).
        /// </summary>
        private void OnActChanged(ActChangedEvent evt)
        {
            // War reached the region — the influx window closes permanently. For
            // Village the war start is PreWar → Crisis (ScenarioStateMachine.StartWar),
            // but any PreWar exit (debug act jumps included) ends the influx.
            // EndInflux → CompleteInflux also clears m_InfluxActivated via
            // ClearInfluxActivated; the explicit clear covers the missing-system path.
            if (evt.NewAct != Act.PreWar && m_InfluxActivated)
            {
                World.GetExistingSystemManaged<RefugeeSpawnSystem>()?.EndInflux($"war reached the region (act → {evt.NewAct})");
                m_InfluxActivated = false;
            }

            // Only adjust rate multiplier - activation is handled by OnScenarioDetected
            switch (evt.NewAct)
            {
                case Act.Crisis:
                    SpawnRateMultiplier = 2.0f;
                    Log.Info("Crisis act — spawn rate 2x (if active)");
                    break;

                case Act.Exodus:
                    SpawnRateMultiplier = 0f;
                    Log.Info("Exodus act — refugee influx stopped (FIX TN-5)");
                    break;

                case Act.Adaptation:
                    SpawnRateMultiplier = 0.5f;
                    Log.Info("Adaptation act — spawn rate 0.5x");
                    break;

                case Act.Routine:
                    SpawnRateMultiplier = 0.1f;
                    Log.Info("Routine act — spawn rate 0.1x");
                    break;

                case Act.PreWar:
                    SpawnRateMultiplier = BalanceConfig.Current.Scenario.RefugeePrewarMultiplier;
                    break;

                default:
                    SpawnRateMultiplier = 1f;
                    break;
            }
        }

        // No per-frame work — all behaviour is event-driven (OnScenarioDetected /
        // OnActChanged) or post-load (ValidateAfterLoad). Kept empty by design.
        protected override void OnUpdateImpl()
        {
        }

        // Post-load reconciliation. Runs in PLVS Phase 2, strictly
        // after RestoreSingletonOwners() — so ScenarioSingleton / CurrentActSingleton
        // are guaranteed restored and live. This replaces the deleted Deserialize/
        // first-update one-shot (m_NeedResyncSpawnSystem) that read those singletons
        // before PLVS recreated them (G1-C5). SpawnRateMultiplier is now a projection
        // of the restored act, not a deserialized value.
        public void ValidateAfterLoad()
        {
            bool hasAct = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton);
            bool inPreWar = hasAct && actSingleton.CurrentAct == Act.PreWar;

            var spawnSystem = World.GetExistingSystemManaged<RefugeeSpawnSystem>();
            if (spawnSystem != null && m_InfluxActivated)
            {
                if (!inPreWar)
                {
                    // Save predates the act-exit cutoff (old semantics ran the influx
                    // into Crisis) — reconcile quietly, no completion narrative on load.
                    spawnSystem.EndInflux("post-load reconcile: act is past PreWar", quiet: true);
                    m_InfluxActivated = false;
                }
                else if (spawnSystem.IsTargetReached)
                {
                    Log.Info("Post-load: influx target already reached, clearing stale m_InfluxActivated");
                    m_InfluxActivated = false;
                }
                else if (!spawnSystem.IsActive)
                {
                    spawnSystem.ResumeRefugeeInflux();
                    Log.Warn("Post-load: resumed SpawnSystem (coordinator active but spawn was inactive after load)");
                }
            }

            if (hasAct)
                ApplyActMultiplier(actSingleton.CurrentAct);
        }

        // ===== Serialization =====

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            var block = SerializationGuard.BeginBlock(writer, SaveVersions.GLOBAL);
            try
            {
                var state = new RefugeeInfluxPersistState(m_InfluxActivated);
                RefugeeInfluxCodec.Write(state, writer);
            }
            finally
            {
                SerializationGuard.EndBlock(writer, block);
            }
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            if (!SerializationGuard.TryBeginBlock(reader, SaveVersions.GLOBAL, out _, out var block, GetType().Name))
            {
                ResetToBootDefaults(ResetReason.VersionMismatch);
                return;
            }
            try
            {
                RefugeeInfluxCodec.Read(reader, out var state);
                m_InfluxActivated = state.InfluxActivated;

                // Deserialize is data-only. SpawnRateMultiplier is a projection of the
                // restored act, recomputed in ValidateAfterLoad (PLVS Phase 2) once
                // CurrentActSingleton is guaranteed restored — not a deserialized value.
                Log.Info($"Deserialized: activated={m_InfluxActivated} (multiplier reconciled post-load)");
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

        public void ResetState()
        {
            if (SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton))
                ApplyActMultiplier(actSingleton.CurrentAct);
            else
                SpawnRateMultiplier = 1f;
            m_InfluxActivated = false;
        }

        public void SetDefaults(Context context) => ResetState();

        public void ResetToBootDefaults(ResetReason reason)
        {
            SpawnRateMultiplier = 1f;
            m_InfluxActivated = false;
        }

        private void ApplyActMultiplier(Act act)
        {
            SpawnRateMultiplier = act switch
            {
                Act.PreWar => BalanceConfig.Current.Scenario.RefugeePrewarMultiplier,
                Act.Crisis => 2.0f,
                Act.Exodus => 0f,
                Act.Adaptation => 0.5f,
                Act.Routine => 0.1f,
                _ => 1f
            };
        }
    }
}
