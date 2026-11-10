namespace CivicSurvival.Core.Interfaces.Core
{
    /// <summary>
    /// Implement on any <see cref="CivicSurvival.Core.Systems.Base.CivicSystemBase"/> subclass
    /// to guarantee its singleton output is computed before any throttled system reads it on
    /// the first frame after load.
    ///
    /// Execution order (PostLoadValidationSystem post-load pass):
    ///   IPostLoadValidation validators  (repair deserialized state)
    ///   IInitializable.OnInitialize()   (compute derived/aggregated singletons)  ← THIS
    ///   Reset throttled schedules       (staggered post-load refresh, no mass force-fire)
    ///
    /// By implementing this interface, HIGH-08 / HIGH-19 / MED-03 class bugs
    /// (system reads uninitialized singleton on frame 1) become impossible by construction.
    ///
    /// Registration is automatic: CivicSystemBase.OnCreate() detects the interface and
    /// calls PostLoadValidationSystem.RegisterInitializable(). CivicSystemBase.OnDestroy()
    /// unregisters it symmetrically. No manual wiring needed.
    ///
    /// Typical usage:
    /// <code>
    /// public partial class FooAggregatorSystem : ThrottledSystemBase, IInitializable
    /// {
    ///     public int InitOrder => InitPriority.DEFAULT;
    ///
    ///     public void OnInitialize() => ComputeAndWriteSingleton(deltaSeconds: 0f);
    ///
    ///     protected override void OnThrottledUpdate() =>
    ///         ComputeAndWriteSingleton(ThrottledDeltaSeconds);
    /// }
    /// </code>
    /// </summary>
    public interface IInitializable
    {
        /// <summary>
        /// Execution order. Lower = earlier. Default: <see cref="InitPriority.DEFAULT"/>.
        /// Use <see cref="InitPriority"/> constants when one initializer depends on another's output.
        /// </summary>
        int InitOrder { get; }

        /// <summary>
        /// Compute initial derived state. Called once per game load, before any throttled
        /// system's first update. All systems exist and vanilla deserialization has
        /// completed — safe to read components and singletons.
        ///
        /// WARNING: ComponentLookup&lt;T&gt; / BufferLookup&lt;T&gt; fields used here must
        /// call .Update(this) before reading or writing. OnInitialize is invoked from
        /// PostLoadValidationSystem's update context, so this system's lookup safety
        /// handles are stale until refreshed. Enforced by analyzer CIVIC288.
        /// </summary>
        void OnInitialize();
    }

    /// <summary>
    /// Named constants for <see cref="IInitializable.InitOrder"/>.
    /// Add entries here when inter-initializer ordering becomes necessary.
    /// </summary>
    public static class InitPriority
    {
        /// <summary>No ordering requirement — runs after all explicitly ordered initializers.</summary>
        public const int DEFAULT = 100;
    }
}
