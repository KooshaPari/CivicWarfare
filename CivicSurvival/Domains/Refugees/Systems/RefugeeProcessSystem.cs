using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.Localization;
using Unity.Collections;
using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using CivicSurvival.Domains.Refugees.Data;
using CivicSurvival.Localization;

namespace CivicSurvival.Domains.Refugees.Systems
{
    /// <summary>
    /// Disables PropertySeeker on newly-spawned refugee households so that vanilla
    /// HouseholdFindPropertySystem cannot assign them permanent housing — refugees
    /// must remain at park/border (HomelessHousehold) until the player builds a
    /// refugee park. Also raises each new household's wallet to the middle-class
    /// wealth floor (refugees arrive with savings — see RefugeeWealthUtil); the
    /// floor is maintained afterwards by RefugeeRetentionSystem's aid pass.
    ///
    /// Ordering matters. HouseholdInitializeSystem.InitializeHouseholdJob runs
    /// `m_CommandBuffer.AddComponent(entity, default(PropertySeeker))` for every
    /// freshly-initialised non-tourist household, which overwrites any disable we
    /// schedule via ECB at spawn time. We therefore run after it and before the
    /// vanilla seek system. HouseholdFindPropertySystem.GetUpdateInterval is 16,
    /// so the race window is up to 16 sim frames — the explicit RegisterBefore
    /// makes the resolution deterministic regardless of which tick HFPS lands on.
    ///
    /// P11 ordering exception: the old After(HouseholdInitializeSystem) edge is
    /// covered by the vanilla household initialization cascade. The same-phase
    /// enforceable edge is RegisterBefore(RefugeeProcessSystem,
    /// HouseholdFindPropertySystem) at the Refugees domain registration site.
    ///
    /// RequireForUpdate on the enableable marker query means the system gets no
    /// scheduling tick outside the refugee influx window.
    /// </summary>
    [ActIndependent]
    public partial class RefugeeProcessSystem : CivicSystemBase, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        private static readonly LogContext Log = new("RefugeeProcessSystem");

        private EntityQuery m_PendingQuery;
        private EntityQuery m_RefugeeHouseholdQuery;
        private EntityQuery m_HappinessParamQuery;
        private Game.UI.NameSystem m_NameSystem = null!;
        private PrefabSystem m_PrefabSystem = null!;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PendingQuery = GetEntityQuery(
                ComponentType.ReadWrite<PendingRefugeeProcess>(),
                ComponentType.Exclude<Deleted>());
            RequireForUpdate(m_PendingQuery);

            m_RefugeeHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<RefugeeHousehold>(),
                ComponentType.Exclude<Deleted>());

            m_HappinessParamQuery = GetEntityQuery(
                ComponentType.ReadOnly<CitizenHappinessParameterData>());

            m_NameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            Log.Info("Created");
        }

        protected override void OnUpdateImpl()
        {
            int processed = 0;

            // Middle-class wealth floor: refugees arrive with savings. Resolved from
            // the vanilla happiness singleton once per pass; 0 disables the boost.
            int wealthFloor = 0;
            if (m_HappinessParamQuery.TryGetSingleton<CitizenHappinessParameterData>(out var happinessParams))
                wealthFloor = RefugeeWealthUtil.GetWealthFloor(happinessParams, BalanceConfig.Current.Scenario.RefugeeWealthFloorMultiplier);

            foreach (var (pending, entity) in
                SystemAPI.Query<RefRW<PendingRefugeeProcess>>()
                .WithEntityAccess())
            {
                if (!pending.ValueRO.NeedsPropertySeekerDisable)
                {
                    // Second pass, one tick after the PropertySeeker disable:
                    // RandomLocalizationInitializeSystem has rolled the household's
                    // surname index by now, so the label resolves with a family name
                    // (same-tick resolution raced it and fell back to the plain label).
                    SetRefugeeFamilyName(entity);
                    SystemAPI.SetComponentEnabled<PendingRefugeeProcess>(entity, false);
                    continue;
                }

                if (!SystemAPI.HasComponent<PropertySeeker>(entity))
                {
                    // HouseholdInitializeSystem has not attached PropertySeeker yet.
                    // Keep PendingRefugeeProcess enabled so the park/border shelter
                    // owner can disable it before HouseholdFindPropertySystem runs.
                    continue;
                }

                if (SystemAPI.IsComponentEnabled<PropertySeeker>(entity))
                {
                    SystemAPI.SetComponentEnabled<PropertySeeker>(entity, false);
                }

                // PropertySeeker presence means HouseholdInitializeSystem has run and
                // the initial wealth roll is in the wallet — raise it to the floor.
                if (wealthFloor > 0 && SystemAPI.HasBuffer<Game.Economy.Resources>(entity))
                {
                    var resources = SystemAPI.GetBuffer<Game.Economy.Resources>(entity);
                    if (EconomyUtils.GetResources(Resource.Money, resources) < wealthFloor)
                        EconomyUtils.SetResources(Resource.Money, resources, wealthFloor);
                }

                // Keep PendingRefugeeProcess enabled: the naming pass above runs on
                // the next tick, after vanilla rolls RandomLocalizationIndex.
                pending.ValueRW.NeedsPropertySeekerDisable = false;
                processed++;
            }

            if (processed > 0 && Log.IsDebugEnabled)
                Log.Debug($"Processed {processed} new refugees");
        }

        /// <summary>
        /// Retroactive labeling for refugee households that predate the naming
        /// feature (or were loaded from a save). Idempotent: households that
        /// already carry a custom name are skipped.
        /// </summary>
        public void ValidateAfterLoad()
        {
            int labeled = 0;
            // A bare plain label means the surname was not resolved when the name
            // was set — retry those too, the index is in the save by now.
            string plainLabel = LocalizationManager.Get("REFUGEE_FAMILY_NAME_PLAIN");
            var households = m_RefugeeHouseholdQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < households.Length; i++)
            {
                if (m_NameSystem.TryGetCustomName(households[i], out var existing) && existing != plainLabel)
                    continue;
                SetRefugeeFamilyName(households[i]);
                labeled++;
            }
            if (households.IsCreated) households.Dispose();

            if (labeled > 0)
                Log.Info($"Labeled {labeled} refugee households after load");
        }

        /// <summary>
        /// Label the household as refugees while keeping the vanilla-rolled random
        /// surname: "Refugees: {LastName}". Key derivation mirrors vanilla
        /// NameSystem.GetFamilyName (RandomGenderedLocalization pool on the prefab
        /// + the household's RandomLocalizationIndex), resolved through the game's
        /// active locale dictionary. Falls back to the plain label when the index
        /// has not been rolled yet or the key is missing from the dictionary.
        /// </summary>
        private void SetRefugeeFamilyName(Entity household)
        {
            string lastName = ResolveVanillaLastName(household);
            string label = string.IsNullOrEmpty(lastName)
                ? LocalizationManager.Get("REFUGEE_FAMILY_NAME_PLAIN")
                : LocalizationManager.Get("REFUGEE_FAMILY_NAME", lastName);
            m_NameSystem.SetCustomName(household, label);
        }

        private string ResolveVanillaLastName(Entity household)
        {
            if (!SystemAPI.HasComponent<PrefabRef>(household))
            {
                return string.Empty;
            }

            Entity prefabEntity = SystemAPI.GetComponent<PrefabRef>(household).m_Prefab;
            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var prefab) || prefab == null)
            {
                return string.Empty;
            }

            if (!SystemAPI.HasBuffer<RandomLocalizationIndex>(household))
            {
                return string.Empty;
            }
            var indices = SystemAPI.GetBuffer<RandomLocalizationIndex>(household);
            if (indices.Length == 0)
            {
                // Vanilla rolls these in RandomLocalizationInitializeSystem
                // (ModificationEnd, keyed on the one-frame Created tag). At >1x game
                // speed several simulation ticks run per rendered frame, so Created
                // can be cleaned up before that system ever sees the household and
                // the buffer stays empty until the next load (where Game.Serialization.
                // RandomLocalizationSystem repairs it). Roll it ourselves with the
                // same vanilla helper the load-time repair uses.
                if (!SystemAPI.HasBuffer<Game.Prefabs.LocalizationCount>(prefabEntity))
                {
                    return string.Empty;
                }

                var counts = SystemAPI.GetBuffer<Game.Prefabs.LocalizationCount>(prefabEntity);
                var random = new Unity.Mathematics.Random(((uint)(household.Index + 1) ^ (uint)System.Environment.TickCount) | 1u);
                RandomLocalizationIndex.GenerateRandomIndices(indices, counts, ref random);
                if (indices.Length == 0)
                {
                    return string.Empty;
                }
            }

            string poolKey;
            if (prefab.TryGet<RandomGenderedLocalization>(out var gendered))
            {
                // Vanilla GetFamilyName semantics: masculine pool form when the
                // household has more than one male member, feminine otherwise.
                int males = 0;
                if (SystemAPI.HasBuffer<HouseholdCitizen>(household))
                {
                    var citizens = SystemAPI.GetBuffer<HouseholdCitizen>(household);
                    for (int i = 0; i < citizens.Length; i++)
                    {
                        Entity citizen = citizens[i].m_Citizen;
                        if (SystemAPI.HasComponent<Citizen>(citizen)
                            && (SystemAPI.GetComponent<Citizen>(citizen).m_State & CitizenFlags.Male) != CitizenFlags.None)
                            males++;
                    }
                }
                poolKey = males > 1 ? gendered.m_MaleID : gendered.m_FemaleID;
            }
            else if (prefab.TryGet<Game.Prefabs.RandomLocalization>(out var plainLoc))
            {
                // Non-gendered pool — vanilla resolves these via GetId:
                // m_LocalizationID + the rolled index.
                poolKey = plainLoc.m_LocalizationID;
            }
            else
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(poolKey))
            {
                return string.Empty;
            }

            string fullKey = LocalizationUtils.AppendIndex(poolKey, indices[0]);
            if (GameManager.instance.localizationManager.activeDictionary.TryGetValue(fullKey, out string lastName))
                return lastName;

            return string.Empty;
        }
    }
}
