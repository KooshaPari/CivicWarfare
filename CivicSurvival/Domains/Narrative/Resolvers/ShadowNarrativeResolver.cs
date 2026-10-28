using System;
using CivicSurvival.Core.Events;
using CivicSurvival.Core.Infrastructure;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Interfaces.Domain.Scenario;
using CivicSurvival.Core.Interfaces.Services;
using CivicSurvival.Core.Systems;
using CivicSurvival.Core.Types;
using CivicSurvival.Domains.Narrative.Infrastructure;
using CivicSurvival.Localization;
using CivicSurvival.Core.Services;
using CivicSurvival.Core.Utils;
using Unity.Entities;

namespace CivicSurvival.Domains.Narrative.Resolvers
{
    /// <summary>
    /// Resolves shadow economy and procurement events into notification DTOs.
    /// Handles: procurement, counterfeit fires, contracts, import sanctions.
    /// Uses batching for counterfeit fire events.
    /// </summary>
#pragma warning disable CIVIC252 // Event-driven: transient events don't re-fire on load
    public sealed class ShadowNarrativeResolver : INarrativeResolver
#pragma warning restore CIVIC252
    {
        public void NotifyDeserialized() { }

        public string Domain => "Shadow";

        private static readonly LogContext Log = new("ShadowNarrativeResolver");

        private readonly NotificationState m_Sink;
        private readonly IDistrictStateReader? m_DistrictService;
        private IEventBus? m_EventBus;

        // Batch state for fires — store resolved name at add-time to avoid stale Entity.Index at flush
        private readonly BatchAggregator<(int BuildingIndex, string BuildingName, int DistrictIndex)> m_PendingFires = new(
            BatchIdentityPolicy.NoDedup<(int BuildingIndex, string BuildingName, int DistrictIndex)>());

        public ShadowNarrativeResolver(NotificationState sink, IDistrictStateReader? districtService)
        {
            m_Sink = sink;
            m_DistrictService = districtService;
        }

        public void Subscribe()
        {
            m_EventBus = ServiceRegistry.Instance.Require<IEventBus>();

            m_EventBus.Subscribe<ShadowNarrativeEvent>(OnShadowNarrative);
        }

        public void Unsubscribe()
        {
            if (m_EventBus == null)
            {
                Log.Warn("EventBus not cached during Unsubscribe");
                return;
            }

            m_EventBus.Unsubscribe<ShadowNarrativeEvent>(OnShadowNarrative);
            m_EventBus = null;
        }

        public void Update(float currentTime)
        {
            try
            {
                FlushPendingFires();
            }
            catch (Exception ex)
            {
                Log.Error($"Error flushing fires: {ex}");
            }
        }

        public void Reset()
        {
            m_PendingFires.Clear();
        }

        public void FlushAll()
        {
            var fires = m_PendingFires.ForceFlush();
            if (fires.Count > 0)
            {
                SafeEmitFireNotifications(fires);
            }
        }

        private void OnShadowNarrative(ShadowNarrativeEvent evt)
        {
            switch (evt.Type)
            {
                case ShadowNarrativeEventType.Procurement:
                    HandleProcurement(evt.DistrictIndex, evt.BuildingCount, evt.Cost, evt.IsCorrupt, evt.KickbackAmount);
                    break;
                case ShadowNarrativeEventType.CounterfeitFire:
                    HandleCounterfeitFire(evt.BuildingIndex, evt.BuildingName, evt.DistrictIndex);
                    break;
                case ShadowNarrativeEventType.ContractSigned:
                    HandleContractSigned(evt.IsCorrupt, evt.KickbackAmount);
                    break;
                case ShadowNarrativeEventType.ImportDiscovered:
                    HandleImportDiscovered(evt.SanctionDays);
                    break;
                case ShadowNarrativeEventType.ImportSanctionsLifted:
                    HandleImportSanctionsLifted();
                    break;
                // FIX #172: WalletFrozen/Unfrozen/Confiscated were published by ShadowWalletSystem
                // but had no narrative handler — player got ZERO feedback on wallet state changes.
                case ShadowNarrativeEventType.WalletFrozen:
                    HandleWalletFrozen(evt.Cost);
                    break;
                case ShadowNarrativeEventType.WalletUnfrozen:
                    HandleWalletUnfrozen(evt.Cost);
                    break;
                case ShadowNarrativeEventType.WalletConfiscated:
                    HandleWalletConfiscated(evt.Cost);
                    break;
                case ShadowNarrativeEventType.ProcurementFailed:
                    HandleProcurementFailed(evt.DistrictIndex, evt.Cost, evt.IsCorrupt);
                    break;
                default:
                    Log.Warn($"Unhandled {nameof(ShadowNarrativeEventType)}: {evt.Type}");
                    break;
            }
        }

        private void HandleProcurement(int districtIndex, int buildingCount, long totalCost, bool isCorrupt, int kickbackAmount)
        {
            // System alert - now includes totalCost for player visibility
            NarrativeEmitter.Alert(
                m_Sink,
                $"procurement_{districtIndex}",
                "NOTIFY_TITLE_PROCUREMENT",
                isCorrupt
                    ? LocalizationManager.Get("NOTIFY_PROCUREMENT_CORRUPT_MSG", buildingCount, totalCost, kickbackAmount)
                    : LocalizationManager.Get("NOTIFY_PROCUREMENT_HONEST_MSG", buildingCount, totalCost),
                isCorrupt ? NotificationStatus.Warning : NotificationStatus.Success
            );

            // NEWS: Procurement report (@FreePress_Live → Herald)
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                isCorrupt
                    ? LocalizationManager.GetRandom("NEWS_PROCUREMENT_CORRUPT")
                    : LocalizationManager.GetRandom("NEWS_PROCUREMENT"),
                string.Empty,
                isCorrupt ? SocialMood.Suspicious : SocialMood.Neutral
            );

            // CHIRP: Kotleta gloats if corrupt
            if (isCorrupt)
            {
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "KOTLETA",
                    LocalizationManager.GetRandom("CHIRP_KOTLETA_PROCUREMENT"),
                    SocialMood.Smug
                );
            }

            // CHIRP: Citizen reaction
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                isCorrupt
                    ? LocalizationManager.GetRandom("CHIRP_PROCUREMENT_SUSPICIOUS")
                    : LocalizationManager.GetRandom("CHIRP_PROCUREMENT"),
                isCorrupt ? SocialMood.Suspicious : SocialMood.Neutral
            );

            // SATIRE: Procurement outcome commentary. Corrupt → @DeputatKotleta (citizen
            // CHIPPER); honest → @CityAlert (official-feed handle → Herald). Routing follows
            // the resolved handle, exactly as the old author demux did.
            if (isCorrupt)
            {
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "KOTLETA",
                    SatireRegistry.GetMessage("SATIRE_PROCUREMENT_CORRUPT"),
                    SocialMood.Smug
                );
            }
            else
            {
                NarrativeEmitter.EmitNews(
                    m_EventBus,
                    "CITY_ALERT",
                    SatireRegistry.GetMessage("SATIRE_PROCUREMENT_HONEST"),
                    string.Empty,
                    SocialMood.Neutral
                );
            }
        }

        private void HandleCounterfeitFire(int buildingIndex, string? eventBuildingName, int districtIndex)
        {
            // Use name from event (resolved at publish time via PrefabRef), fallback to generic
            string buildingName = string.IsNullOrWhiteSpace(eventBuildingName)
                ? LocalizationManager.Get("NARRATIVE_UNKNOWN_BUILDING")
                : eventBuildingName!;
#pragma warning disable CIVIC230 // NoDedup policy intentionally preserves every counterfeit fire event
            bool forceFlush = m_PendingFires.Add((buildingIndex, buildingName, districtIndex));
#pragma warning restore CIVIC230

            if (forceFlush)
            {
                var fires = m_PendingFires.ForceFlush();
                SafeEmitFireNotifications(fires);
            }
        }

        private void HandleContractSigned(bool isShady, int kickbackAmount)
        {
            if (isShady)
            {
                // CHIRP: Kotleta celebrates the deal
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "KOTLETA",
                    LocalizationManager.GetRandom("CHIRP_KOTLETA_CONTRACT", kickbackAmount),
                    SocialMood.Smug
                );

                // CHIRP: Tech worker worried
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "TECH_WORKER",
                    LocalizationManager.GetRandom("CHIRP_TECH_CONTRACT_SHADY"),
                    SocialMood.Warning
                );
            }
            else
            {
                // CHIRP: Tech worker approves official contract
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "TECH_WORKER",
                    LocalizationManager.GetRandom("CHIRP_TECH_CONTRACT_OFFICIAL"),
                    SocialMood.Neutral
                );
            }
        }

        private void HandleImportDiscovered(int sanctionDays)
        {
            // System alert - scandal!
            NarrativeEmitter.Alert(
                m_Sink,
                "shadow_import_discovered",
                "NOTIFY_TITLE_SHADOW_DISCOVERED",
                LocalizationManager.Get("NOTIFY_SHADOW_DISCOVERED_MSG", sanctionDays),
                NotificationStatus.Error
            );

            // NEWS: Scandal report (@FreePress_Live → Herald)
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_SHADOW_DISCOVERED"),
                string.Empty,
                SocialMood.Suspicious
            );

            // CHIRP: Citizen reaction
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_SHADOW_DISCOVERED"),
                SocialMood.Suspicious
            );

            // CHIRP: Kotleta denies
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "KOTLETA",
                LocalizationManager.GetRandom("CHIRP_KOTLETA_SHADOW"),
                SocialMood.Angry
            );

            // SATIRE: Mariana investigates
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "MARIANA",
                SatireRegistry.GetMessage("SATIRE_SHADOW_DISCOVERED", sanctionDays),
                SocialMood.Suspicious
            );
        }

        private void HandleImportSanctionsLifted()
        {
            // System alert - sanctions ended
            NarrativeEmitter.Alert(
                m_Sink,
                "shadow_sanctions_lifted",
                "NOTIFY_TITLE_SANCTIONS_END",
                LocalizationManager.Get("NOTIFY_SHADOW_SANCTIONS_LIFTED_MSG"),
                NotificationStatus.Info
            );

            // NEWS: Sanctions ended (@FreePress_Live → Herald)
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_SHADOW_SANCTIONS_LIFTED"),
                string.Empty,
                SocialMood.Neutral
            );

            // CHIRP: Citizen
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_SHADOW_SANCTIONS_LIFTED"),
                SocialMood.Neutral
            );

            // CHIRP: Kotleta gloats
            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "KOTLETA",
                LocalizationManager.GetRandom("CHIRP_KOTLETA_SANCTIONS_LIFTED"),
                SocialMood.Smug
            );

            // SATIRE: @CityAlert is an official-feed handle → Herald (preserves demux routing).
            NarrativeEmitter.EmitNews(
                m_EventBus,
                "CITY_ALERT",
                SatireRegistry.GetMessage("SATIRE_SHADOW_SANCTIONS_LIFTED"),
                string.Empty,
                SocialMood.Neutral
            );
        }

        private void HandleProcurementFailed(int districtIndex, long totalCost, bool isCorrupt)
        {
            NarrativeEmitter.Alert(
                m_Sink,
                $"procurement_failed_{districtIndex}",
                "NOTIFY_TITLE_PROCUREMENT",
                LocalizationManager.Get("NOTIFY_PROCUREMENT_FAILED_MSG", totalCost),
                NotificationStatus.Error
            );

            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_PROCUREMENT_FAILED"),
                string.Empty,
                SocialMood.Warning
            );

            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_PROCUREMENT_FAILED"),
                SocialMood.Warning
            );

            if (isCorrupt)
            {
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "KOTLETA",
                    LocalizationManager.GetRandom("CHIRP_KOTLETA_PROCUREMENT_FAILED"),
                    SocialMood.Angry
                );
            }
        }

        private void HandleWalletFrozen(long balance)
        {
            NarrativeEmitter.Alert(
                m_Sink,
                "wallet_frozen",
                "NOTIFY_TITLE_WALLET_FROZEN",
                LocalizationManager.Get("NOTIFY_WALLET_FROZEN_MSG", balance),
                NotificationStatus.Error
            );

            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_WALLET_FROZEN"),
                string.Empty,
                SocialMood.Suspicious
            );
        }

        private void HandleWalletUnfrozen(long balance)
        {
            NarrativeEmitter.Alert(
                m_Sink,
                "wallet_unfrozen",
                "NOTIFY_TITLE_WALLET_UNFROZEN",
                LocalizationManager.Get("NOTIFY_WALLET_UNFROZEN_MSG", balance),
                NotificationStatus.Success
            );

            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_WALLET_UNFROZEN"),
                string.Empty,
                SocialMood.Neutral
            );

            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_WALLET_UNFROZEN"),
                SocialMood.Neutral
            );
        }

        private void HandleWalletConfiscated(long confiscated)
        {
            NarrativeEmitter.Alert(
                m_Sink,
                "wallet_confiscated",
                "NOTIFY_TITLE_WALLET_CONFISCATED",
                LocalizationManager.Get("NOTIFY_WALLET_CONFISCATED_MSG", confiscated),
                NotificationStatus.Error
            );

            NarrativeEmitter.EmitNews(
                m_EventBus,
                "NEXTA",
                LocalizationManager.GetRandom("NEWS_WALLET_CONFISCATED"),
                string.Empty,
                SocialMood.Warning
            );

            NarrativeEmitter.EmitSocial(
                m_EventBus,
                "LOCAL_RESIDENT",
                LocalizationManager.GetRandom("CHIRP_WALLET_CONFISCATED"),
                SocialMood.Warning
            );
        }

        private void FlushPendingFires()
        {
            if (!m_PendingFires.IsReadyToFlush()) return;

            var fires = m_PendingFires.FlushAndGet();
            SafeEmitFireNotifications(fires);
        }

        private void SafeEmitFireNotifications(System.Collections.Generic.IReadOnlyList<(int BuildingIndex, string BuildingName, int DistrictIndex)> fires)
        {
            if (fires.Count == 0) return;
            try
            {
                EmitFireNotifications(fires);
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Error emitting fire batch: {ex}");
            }

            for (int i = 0; i < fires.Count; i++)
            {
                try
                {
                    EmitFireNotifications(new[] { fires[i] });
                }
                catch (Exception itemEx)
                {
                    Log.Error($"Error emitting fire notification for building {fires[i].BuildingIndex}: {itemEx}");
                }
            }
        }

        private void EmitFireNotifications(System.Collections.Generic.IReadOnlyList<(int BuildingIndex, string BuildingName, int DistrictIndex)> fires)
        {
            if (fires.Count == 0) return;

            if (fires.Count == 1)
            {
                // Single fire — vanilla FireSimulationSystem owns the repair cost, so the toast
                // carries no fictional damage number.
                var (buildingIndex, buildingName, fireDistrict) = fires[0];

                NarrativeEmitter.Alert(
                    m_Sink,
                    $"fire_{buildingIndex}",
                    "NOTIFY_TITLE_FIRE",
                    LocalizationManager.Get("NOTIFY_FIRE_MSG", buildingName),
                    NotificationStatus.Error
                );

                NarrativeEmitter.EmitNews(
                    m_EventBus,
                    "NEXTA",
                    LocalizationManager.GetRandom("NEWS_FIRE", buildingName),
                    string.Empty,
                    SocialMood.Warning
                );

                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "TECH_WORKER",
                    LocalizationManager.GetRandom("CHIRP_TECH_FIRE_SUSPICIOUS"),
                    SocialMood.Suspicious
                );

                // SATIRE: Counterfeit battery fire commentary
                string fireDistrictName = m_DistrictService?.GetDistrictName(fireDistrict) ?? $"District {fireDistrict}";
                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "LOCAL_RESIDENT",
                    SatireRegistry.GetMessage("SATIRE_COUNTERFEIT_FIRE", fireDistrictName),
                    SocialMood.Angry
                );
            }
            else
            {
                // Multiple fires — aggregated notification by count (vanilla owns repair cost).
                int fireCount = fires.Count;

                NarrativeEmitter.Alert(
                    m_Sink,
                    "fire_batch",
                    "NOTIFY_TITLE_FIRE",
                    LocalizationManager.Get("NOTIFY_FIRE_BATCH_MSG", fireCount),
                    NotificationStatus.Error
                );

                NarrativeEmitter.EmitNews(
                    m_EventBus,
                    "NEXTA",
                    LocalizationManager.Get("NEWS_FIRE_BATCH", fireCount),
                    string.Empty,
                    SocialMood.Warning
                );

                NarrativeEmitter.EmitSocial(
                    m_EventBus,
                    "TECH_WORKER",
                    LocalizationManager.Get("TECH_FIRE_BATCH", fireCount),
                    SocialMood.Suspicious
                );
            }
        }
    }
}
