using Game;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Power;
using CivicSurvival.Core.Components.Requests;
using CivicSurvival.Core.Components.Threats;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems.Requests;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.Utils;
using CivicSurvival.Core.Attributes;

namespace CivicSurvival.Domains.Corruption.Systems
{
    /// <summary>
    /// Processes CorruptionSchemeRequest ephemeral entities.
    /// Contains FULL business logic for corruption scheme settings.
    ///
    /// Handles:
    /// - EmergencyFundWithdraw → EmergencyFundSingleton (ECS-pure)
    /// - FuelSiphon → FuelSiphoningSingleton (ECS-pure)
    ///
    /// Uses RequireForUpdate - zero overhead when no requests pending.
    /// </summary>
    [SingletonOwner(typeof(EmergencyFundSettings))]
    [OwnedSingletonLifecycle(
        Persisted = false,
        EnsurePhase = SingletonLifecyclePhase.OnCreate | SingletonLifecyclePhase.ReconcileAfterLoad,
        DisposePhase = SingletonLifecyclePhase.None)]
    [ActIndependent]
    [HandlesRequestKind(RequestKind.CorruptionScheme)]
    [TransientConsumerReconcile(typeof(CorruptionSchemeRequest), ReconcileMode.ExplicitlyLossyAndSafe,
        Justification = "Transient UI command: emergency-fund/fuel-siphon settings are changed only inside this consumer, so pre-consume load loss has no committed durable side-effect.")]
    public partial class CorruptionSchemeRequestSystem : EmittingRequestProcessor<CorruptionSchemeRequest>, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("CorruptionSchemeRequestSystem");

        private static readonly int[] FuelSiphonPresets = { 0, 15, 30, 50 };
        private static readonly int[] DraftDeferralPresets = { 0, 15, 30, 50 };
        private static readonly int[] EmergencyFundPresets = { 0, 25, 50, 75, 100 };
        private static readonly int[] ConstructionKickbackPresets = { 0, 5, 10, 20 };

        private EntityQuery m_EmergencyFundSettingsQuery;
        private ComponentLookup<EmergencyFundSettings> m_EmergencyFundSettingsLookup;
        private IFuelSiphoningSettingsWriter? m_FuelSiphoningSettings;
        private IDraftExemptionSettingsWriter? m_DraftExemptionSettings;
        private IConstructionKickbackSettingsWriter? m_ConstructionKickbackSettings;

        protected override ActionKey ActionKey => ActionKey.EmergencyFundPreset;
        public override RequestKind RequestKind => RequestKind.CorruptionScheme;

        // Registered in ModificationEnd (CorruptionDomain) — pause-safe per Axiom 14.
        protected override bool RunsInModificationEnd => true;

        protected override void OnCreateProcessor()
        {
            m_EmergencyFundSettingsQuery = GetEntityQuery(
                ComponentType.ReadWrite<EmergencyFundSettings>()
            );

            m_EmergencyFundSettingsLookup = GetComponentLookup<EmergencyFundSettings>(false);
            EmergencyFundSingleton.EnsureExists(EntityManager);

            Log.Info("Created");
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_FuelSiphoningSettings ??= ServiceRegistry.Instance.Require<IFuelSiphoningSettingsWriter>();
            m_DraftExemptionSettings ??= ServiceRegistry.Instance.Require<IDraftExemptionSettingsWriter>();
            m_ConstructionKickbackSettings ??= ServiceRegistry.Instance.Require<IConstructionKickbackSettingsWriter>();
        }

        protected override RequestStatus Apply(
            in CorruptionSchemeRequest request,
            in RequestMeta meta,
            ref EntityCommandBuffer ecb,
            ref bool ecbCreated,
            out string reasonId)
        {
            _ = ecb;
            _ = ecbCreated;
            m_EmergencyFundSettingsLookup.Update(this);

            return ProcessRequest(request, out reasonId)
                ? RequestStatus.Success
                : RequestStatus.Failed;
        }

        protected override ActionKey ResolveActionKey(in CorruptionSchemeRequest request)
            => request.SchemeType switch
            {
                CorruptionSchemeType.None => throw new System.ArgumentOutOfRangeException(
                    nameof(request),
                    request.SchemeType,
                    "CorruptionSchemeType.None is not executable"),
                CorruptionSchemeType.EmergencyFundWithdraw => ActionKey.EmergencyFundPreset,
                CorruptionSchemeType.FuelSiphon => ActionKey.FuelSiphonPreset,
                CorruptionSchemeType.DraftDeferral => ActionKey.DraftDeferralPreset,
                CorruptionSchemeType.ConstructionKickback => ActionKey.ConstructionKickbackPreset,
                _ => throw new System.ArgumentOutOfRangeException(
                    nameof(request),
                    request.SchemeType,
                    $"Unknown CorruptionSchemeType — extend ResolveActionKey when adding new enum values")
            };

        // Row W2-59 (S233): exclude SchemeType.None from the action gate. The base processor calls
        // ResolveActionKey() only when this returns true; resolving None throws ArgumentOutOfRangeException
        // and crashes the simulation update. None is a no-op at the Apply layer (ProcessRequest returns
        // false for None), so the gate must mirror that.
        protected override bool ShouldRunActionGate(in CorruptionSchemeRequest request)
            => request.SchemeType != CorruptionSchemeType.None && request.Percent > 0;

        protected override ActionContext BuildActionContext(in CorruptionSchemeRequest request)
        {
            bool hasActSingleton = SystemAPI.TryGetSingleton<CurrentActSingleton>(out var actSingleton);
            var ctx = new ActionContext(
                false,
                GamePhase.Calm,
                hasActSingleton,
                hasActSingleton ? actSingleton.CurrentAct : Act.PreWar);

            return SystemAPI.TryGetSingleton<ShadowWalletSingleton>(out var wallet)
                ? ctx.WithWallet(wallet)
                : ctx;
        }

        private bool ProcessRequest(in CorruptionSchemeRequest request, out string failReason)
        {
            failReason = "";

            bool success = request.SchemeType switch
            {
                CorruptionSchemeType.None => false,
                CorruptionSchemeType.EmergencyFundWithdraw => ProcessEmergencyFund(request, out failReason),
                CorruptionSchemeType.FuelSiphon => ProcessFuelSiphon(request, out failReason),
                CorruptionSchemeType.DraftDeferral => ProcessDraftDeferral(request, out failReason),
                CorruptionSchemeType.ConstructionKickback => ProcessConstructionKickback(request, out failReason),
                _ => false
            };

            if (Log.IsDebugEnabled) Log.Debug($"{request.SchemeType} = {request.Percent}%: {(success ? "Success" : "Failed")}");
            return success;
        }

        private bool ProcessEmergencyFund(in CorruptionSchemeRequest request, out string failReason)
        {
            failReason = "";
            // TOCTOU FIX: Use TryGetSingletonEntity instead of IsEmpty + GetSingletonEntity
            if (!m_EmergencyFundSettingsQuery.TryGetSingletonEntity<EmergencyFundSettings>(out var entity))
            {
                failReason = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            var config = m_EmergencyFundSettingsLookup[entity];

            int snapped = Engine.Util.SnapToPreset(request.Percent, EmergencyFundPresets);
            config.WithdrawPercent = snapped;
            m_EmergencyFundSettingsLookup[entity] = config;

            Log.Info($"[EmergencyFund] Withdraw target set to {snapped}%");
            return true;
        }

        private bool ProcessFuelSiphon(in CorruptionSchemeRequest request, out string failReason)
        {
            failReason = "";
            int snapped = Engine.Util.SnapToPreset(request.Percent, FuelSiphonPresets);
            if (m_FuelSiphoningSettings == null || !m_FuelSiphoningSettings.TrySetFuelSiphonPercent(snapped))
            {
                failReason = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            return true;
        }

        private bool ProcessDraftDeferral(in CorruptionSchemeRequest request, out string failReason)
        {
            failReason = "";
            int snapped = Engine.Util.SnapToPreset(request.Percent, DraftDeferralPresets);
            if (m_DraftExemptionSettings == null || !m_DraftExemptionSettings.TrySetDraftDeferralPercent(snapped))
            {
                failReason = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            return true;
        }

        private bool ProcessConstructionKickback(in CorruptionSchemeRequest request, out string failReason)
        {
            failReason = "";
            int snapped = Engine.Util.SnapToPreset(request.Percent, ConstructionKickbackPresets);
            if (m_ConstructionKickbackSettings == null || !m_ConstructionKickbackSettings.TrySetConstructionKickbackPercent(snapped))
            {
                failReason = ReasonIds.GenericSingletonNotReady;
                return false;
            }

            return true;
        }

        public void ValidateAfterLoad() => EmergencyFundSingleton.EnsureExists(EntityManager);

        protected override void OnDestroy()
        {
            Log.Info("Destroyed");
            base.OnDestroy();
        }
    }
}
