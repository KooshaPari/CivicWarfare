using System;
using Game.Simulation;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.CrossDomain;
using CivicSurvival.Core.Components.Domain.Economy;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Core.Systems
{
    /// <summary>
    /// World-owned host for <see cref="CivicSurvival.Patches.ElectricityPatch.IsolatedGridPatch"/>.
    /// Owns the per-World <c>ShadowImportState</c> EntityQuery and import-cap state
    /// (logged values, transition flags). OnDestroy disposes the query and resets
    /// <c>ImportCapRuntimeState</c>.
    /// </summary>
    [FrameworkSystem]
    [ActIndependent]
    public partial class IsolatedGridHandlerSystem : SystemBase, IPostLoadValidation
    {
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("IsolatedGridHandler");
        private const int IMPORT_LIMIT_LOG_INTERVAL_FRAMES = 60;

        private readonly object m_Lock = new();
        private EntityQuery m_ShadowTradeQuery;
        private bool m_QueryCreated;

        private int m_LogCountdown;
        private int m_LastLoggedImport = -1;
        private int m_LastLoggedShadowMW = -1;
        private volatile bool m_ImportLimitActive;
        private volatile bool m_HasPublishedImportCap;
        private int m_LastPublishedImportCapKW = -1;
        private volatile bool m_HasPublishedExportCap;
        private int m_LastPublishedExportCapKW = -1;
        private bool m_TradePriceLogged;
        private EntityQuery m_TradeParameterQuery;
        private bool m_TradeParameterQueryCreated;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ShadowTradeQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ShadowImportState>());
            m_QueryCreated = true;
            m_TradeParameterQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Game.Prefabs.OutsideTradeParameterData>());
            m_TradeParameterQueryCreated = true;
            World.GetExistingSystemManaged<PostLoadValidationSystem>()?.Register(this);
        }

        protected override void OnDestroy()
        {
            World.GetExistingSystemManaged<PostLoadValidationSystem>()?.Unregister(this);
            ResetForUnload(disposeQuery: true);
            base.OnDestroy();
        }

        /// <summary>
        /// Reset import-cap state and clear <see cref="ImportCapRuntimeState"/>.
        /// Normal world teardown also disposes the world-owned query; hot-reload
        /// cleanup leaves the live query intact and only clears static state so
        /// <c>PowerCapacityMath</c> stops reading the last-published cap.
        /// </summary>
        internal void ResetForUnload(bool disposeQuery = false)
        {
            lock (m_Lock)
            {
                if (disposeQuery && m_QueryCreated)
                {
                    try { m_ShadowTradeQuery.Dispose(); }
                    catch (ObjectDisposedException) { /* expected during shutdown */ }
                    catch (Exception ex)
                    {
                        if (Log.IsDebugEnabled) Log.Debug($"Unexpected error disposing shadow trade query: {ex.GetType().Name}: {ex.Message}");
                    }
                    m_ShadowTradeQuery = default;
                    m_QueryCreated = false;
                }

                if (disposeQuery && m_TradeParameterQueryCreated)
                {
                    try { m_TradeParameterQuery.Dispose(); }
                    catch (ObjectDisposedException) { /* expected during shutdown */ }
                    catch (Exception ex)
                    {
                        if (Log.IsDebugEnabled) Log.Debug($"Unexpected error disposing trade parameter query: {ex.GetType().Name}: {ex.Message}");
                    }
                    m_TradeParameterQuery = default;
                    m_TradeParameterQueryCreated = false;
                }

                m_LogCountdown = 0;
                m_LastLoggedImport = -1;
                m_LastLoggedShadowMW = -1;
                m_ImportLimitActive = false;
                m_HasPublishedImportCap = false;
                m_LastPublishedImportCapKW = -1;
                m_HasPublishedExportCap = false;
                m_LastPublishedExportCapKW = -1;
                m_TradePriceLogged = false;
                ImportCapRuntimeState.Reset();
            }
        }

        // ORDER-INVARIANT: import-cap static state must be republished before
        // PowerCapacityIndexSystem(POWER_MODIFIERS_FIRST - 1) seeds ImportCapModifier.
        public int HydrationOrder => HydrationPriority.POWER_MODIFIERS_FIRST - 2;

        public void ValidateAfterLoad()
        {
            lock (m_Lock)
            {
                m_LogCountdown = 0;
                m_LastLoggedImport = -1;
                m_LastLoggedShadowMW = -1;
                m_ImportLimitActive = false;
                m_HasPublishedImportCap = false;
                m_LastPublishedImportCapKW = -1;
                m_HasPublishedExportCap = false;
                m_LastPublishedExportCapKW = -1;
                m_TradePriceLogged = false;
                ImportCapRuntimeState.Reset();
            }

            if (ServiceRegistry.IsInitialized)
            {
                PublishImportLimit(ServiceRegistry.Instance, emitTransitions: false);
            }
            else
            {
                ImportCapRuntimeState.SetCurrentImportCapKW(
                    Engine.PowerGrid.DEFAULT_LEGAL_IMPORT_MW * Engine.PowerGrid.KW_PER_MW);
                ImportCapRuntimeState.SetCurrentExportCapKW(
                    Engine.PowerGrid.DEFAULT_LEGAL_EXPORT_MW * Engine.PowerGrid.KW_PER_MW);
            }
        }

        protected override void OnUpdate() { /* handler is event-driven via PublishImportLimit */ }

        /// <summary>
        /// Called from the Harmony bridge in <c>IsolatedGridPatch.OnElectricityFlowPostfix</c>.
        /// Publishes the combined legal+shadow import cap and emits InfraEvent transitions.
        /// </summary>
        internal void PublishImportLimit(ServiceRegistry registry, bool emitTransitions = true)
        {
            var settings = registry.Get<ModSettings>();
            if (settings == null)
                Log.Warn("ModSettings unavailable - using default limit (100 MW)");

            int legalImportMW = settings?.LegalImportMW ?? Engine.PowerGrid.DEFAULT_LEGAL_IMPORT_MW;
            int shadowImportMW = GetShadowImportMW();

            int totalLimitMW = legalImportMW + shadowImportMW;
            int maxImportKW = totalLimitMW * Engine.PowerGrid.KW_PER_MW;
            int importMW = maxImportKW / Engine.PowerGrid.KW_PER_MW;

            // Per-interconnector export cap: ONLY the legal limit, WITHOUT the shadow
            // import bonus — the covert channel does not widen the line for selling out.
            int legalExportMW = settings?.LegalExportMW ?? Engine.PowerGrid.DEFAULT_LEGAL_EXPORT_MW;
            int exportCapKW = legalExportMW * Engine.PowerGrid.KW_PER_MW;

            var transition = CaptureImportLimitState(maxImportKW, importMW, shadowImportMW, exportCapKW, out bool shouldLog, out bool shouldLogExport);
            var eventBus = registry.Get<IEventBus>();

            if (emitTransitions && transition.HasValue)
                eventBus?.SafePublish(new InfraEvent(transition.Value));

            if (shouldLog)
            {
                if (shadowImportMW > 0)
                    Log.Info($"ImportLimit: Set to {importMW} MW (legal {legalImportMW} + shadow {shadowImportMW})");
                else
                    Log.Info($"ImportLimit: Set to {importMW} MW (legal limit)");
            }

            if (shouldLogExport)
                Log.Info($"ExportLimit: Set to {exportCapKW / Engine.PowerGrid.KW_PER_MW} MW (legal limit)");

            // One-shot vanilla trade price probe. Lives here (not in ValidateAfterLoad)
            // because this bridge fires on every flow update, including a fresh new game
            // that never goes through the post-load hook. Unit verified against the
            // decompile: a day is 262144 frames, ElectricityTradeSystem ticks every 128
            // frames -> 2048 updates/day, each paying flow/2048 x price, so the daily
            // income is exactly export_kW x price (price = revenue per 1 kW per 24h).
            // The latch and the query flag are reset under m_Lock (ResetForUnload /
            // ValidateAfterLoad), so the probe takes the same lock; IO stays outside it.
            bool logTradePrice = false;
            Game.Prefabs.OutsideTradeParameterData trade = default;
            lock (m_Lock)
            {
                if (!m_TradePriceLogged
                    && m_TradeParameterQueryCreated
                    && m_TradeParameterQuery.TryGetSingleton<Game.Prefabs.OutsideTradeParameterData>(out trade))
                {
                    m_TradePriceLogged = true;
                    logTradePrice = true;
                }
            }

            if (logTradePrice)
                Log.Info($"[TradePrice] electricityExport={trade.m_ElectricityExportPrice:F2} electricityImport={trade.m_ElectricityImportPrice:F2} (revenue per 1 kW per 24h)");
        }

        private InfraEventType? CaptureImportLimitState(int maxImportKW, int importMW, int shadowImportMW, int exportCapKW, out bool shouldLog, out bool shouldLogExport)
        {
            shouldLog = false;
            shouldLogExport = false;
            InfraEventType? transition = null;

            lock (m_Lock)
            {
                if (!m_HasPublishedImportCap || m_LastPublishedImportCapKW != maxImportKW)
                {
                    ImportCapRuntimeState.SetCurrentImportCapKW(maxImportKW);
                    m_LastPublishedImportCapKW = maxImportKW;
                    m_HasPublishedImportCap = true;

                    bool limitActive = maxImportKW > 0;
                    if (limitActive && !m_ImportLimitActive)
                    {
                        m_ImportLimitActive = true;
                        transition = InfraEventType.ImportLimitReached;
                    }
                    else if (!limitActive && m_ImportLimitActive)
                    {
                        m_ImportLimitActive = false;
                        transition = InfraEventType.ImportLimitCleared;
                    }
                }

                // Export cap publication. No InfraEvent transitions: there is no
                // "export limit reached" UI signal in scope. Logging is deferred to the
                // caller — IO inside the lock would stretch the hold time.
                if (!m_HasPublishedExportCap || m_LastPublishedExportCapKW != exportCapKW)
                {
                    ImportCapRuntimeState.SetCurrentExportCapKW(exportCapKW);
                    m_LastPublishedExportCapKW = exportCapKW;
                    m_HasPublishedExportCap = true;
                    shouldLogExport = true;
                }

                bool logValuesChanged = importMW != m_LastLoggedImport || shadowImportMW != m_LastLoggedShadowMW;
                if (m_LogCountdown > 0)
                    m_LogCountdown--;

                if (logValuesChanged && m_LogCountdown <= 0)
                {
                    m_LastLoggedImport = importMW;
                    m_LastLoggedShadowMW = shadowImportMW;
                    m_LogCountdown = IMPORT_LIMIT_LOG_INTERVAL_FRAMES;
                    shouldLog = true;
                }
            }

            return transition;
        }

        private int GetShadowImportMW()
        {
            ShadowImportState state;
            lock (m_Lock)
            {
                if (!m_QueryCreated)
                    EnsureShadowTradeQueryUnsafe();
                if (!m_ShadowTradeQuery.TryGetSingleton<ShadowImportState>(out state))
                    return 0;
            }

            if (state.ImportIsSanctioned)
                return 0;

            return state.ImportMW;
        }

        [CallerHoldsLock("m_Lock")]
        private void EnsureShadowTradeQueryUnsafe()
        {
            if (m_QueryCreated)
                return;

            m_ShadowTradeQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ShadowImportState>());
            m_QueryCreated = true;
        }
    }
}
