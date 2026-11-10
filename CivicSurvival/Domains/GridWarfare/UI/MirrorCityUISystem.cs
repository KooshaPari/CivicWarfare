using Unity.Entities;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Components.Domain.GridWarfare;
using CivicSurvival.Core.Components.Domain.Intel;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Systems.Base;
using CivicSurvival.Core.Utils;
using static CivicSurvival.Core.UI.B;

namespace CivicSurvival.Domains.GridWarfare.UI
{
    /// <summary>
    /// Publishes the mirror enemy city as a JSON snapshot for the War Room STRIKE view. Read-only
    /// consumer of another system's data — it never writes the <see cref="EnemyTarget"/> buffer, the
    /// <see cref="MirrorCityState"/> header, or the enemy axes; <c>MirrorCitySystem</c> owns those (same
    /// read-only-in-UI discipline as <c>GridWarfareUISystem</c> reading <c>EnemyState</c>).
    ///
    /// All serialization and — critically — all intel quantization lives in the pure, ECS-free
    /// <see cref="MirrorCitySnapshotBuilder"/> (sibling pattern to the map-contour publisher: builder in
    /// Core, publish in the UI system). This system only reads the ECS data, computes the effective intel
    /// level, and pushes the string to the standalone <c>MirrorCity</c> raw-string binding — it is
    /// NOT folded into the per-frame GridWarfareState payload.
    ///
    /// The snapshot changes rarely relative to the tick rate, so the JSON string is rebuilt and
    /// re-pushed only when a cheap signature of the already-quantized content changes
    /// (<see cref="MirrorCitySnapshotBuilder.ComputeSignature"/>) — no per-tick string churn.
    /// </summary>
    [ActIndependent]
    public partial class MirrorCityUISystem : CivicUIPanelSystem, IPostLoadValidation
    {
        public int HydrationOrder => CivicSurvival.Core.Interfaces.Core.HydrationPriority.DEFAULT;
        public void PurgeAfterLoad() { }

        // Signature of the last-published quantized snapshot. The string is rebuilt/re-pushed only when
        // this changes, so a repair tick that does not move any published (quantized) number is free.
        private long m_LastSignature;
        private bool m_HasPublished;

        // Baked-map catalog for the contour underlay: the process-wide shared cache, so the
        // sim system's OnStartRunning pre-warm covers the first STRIKE publish here too — no cold
        // disk read on the UI thread.
        private readonly CivicSurvival.Core.Services.MirrorCityMapCatalog m_MapCatalog = CivicSurvival.Core.Services.MirrorCityMapCatalog.Shared;

        // PERF-LOCK: the serialized contour fragment is cached per variant — at L2 the snapshot
        // republishes every throttled tick (exact values genuinely move), and re-serializing the
        // static water geometry (hundreds of vertices) on each publish was pure churn. Only a
        // variant change re-serializes. Empty string = variant has no baked mask (bare grid);
        // plain string, not string?, because the source-generated partial trips CS8669 on nullable
        // annotations in this class.
        private int m_ContourVariantId = -1;
        private string m_ContourFragment = string.Empty;

        protected override void ConfigureBindings()
        {
            Bindings.Add<string>(MirrorCity, "{}");
        }

        protected override void OnPanelUpdate()
        {
            // No generated city yet (pre-war, or genVersion reset pending regeneration) → leave the
            // default empty object. The STRIKE view treats "{}" as "no city intel".
            if (!SystemAPI.TryGetSingleton<MirrorCityState>(out var state) || !state.Generated)
                return;

            var targets = SystemAPI.GetSingletonBuffer<EnemyTarget>(isReadOnly: true).AsNativeArray();
            int level = EffectiveIntelLevel();

            long signature = MirrorCitySnapshotBuilder.ComputeSignature(state, targets, level);
            if (m_HasPublished && signature == m_LastSignature)
                return;

            m_LastSignature = signature;
            m_HasPublished = true;
            // Signature gate above already decided this is a real change; publish through the base
            // helper (CIVIC407) rather than a raw Bindings.Update. The contour underlay ships from
            // intel L1 when the variant's baked mask is present (null → targets on a bare grid).
            string contour = level >= 1 ? ContourFragmentFor(state.VariantId) : string.Empty;
            PublishJsonWhenComplete(MirrorCity, NoSourceChecks, () => MirrorCitySnapshotBuilder.Build(state, targets, level, contour));
        }

        /// <summary>
        /// The variant's pre-serialized contour fragment (see the PERF-LOCK note on the cache
        /// fields). Empty when the variant's baked mask is unavailable.
        /// </summary>
        private string ContourFragmentFor(int variantId)
        {
            if (variantId == m_ContourVariantId)
                return m_ContourFragment;

            var mask = m_MapCatalog.GetLandMask(CivicSurvival.Core.Logic.MirrorCityVariantCatalog.Get(variantId).MapId);
            m_ContourVariantId = variantId;
            m_ContourFragment = mask != null ? MirrorCitySnapshotBuilder.BuildContourFragment(mask) : string.Empty;
            return m_ContourFragment;
        }

        /// <summary>
        /// Effective intel precision — the shared <see cref="IntelStateSingleton.EffectiveIntelLevel"/>
        /// (insider folds to L2), so every intel-gated producer agrees. Missing singleton → L0
        /// (fail-closed, same convention as GridWarfareUISystem).
        /// </summary>
        private int EffectiveIntelLevel()
        {
            return SystemAPI.TryGetSingleton<IntelStateSingleton>(out var intel)
                ? intel.EffectiveIntelLevel
                : 0;
        }

        /// <summary>
        /// Re-arm the publish gate on load. Load-over-running-game reuses this system instance and does
        /// not call OnStartRunning, so a freshly generated / regenerated city must republish even if its
        /// signature happens to match the pre-load one. Clearing the latch forces the next tick to emit.
        /// </summary>
        public void ValidateAfterLoad()
        {
            m_HasPublished = false;
            m_LastSignature = 0L;
        }
    }
}
