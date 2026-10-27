namespace CivicSurvival.Core.Interfaces.Domain.Scenario
{
    /// <summary>
    /// Interface for Narrative Resolvers that convert Core domain events into social media posts.
    /// Resolvers subscribe to EventBus events and push notifications to INotificationSink.
    ///
    /// Lifecycle (managed by NarrativeNotificationSystem):
    /// 1. Subscribe() — on system init
    /// 2. Update() — every 10 frames (batch flushing)
    /// 3. FlushAll() — before save (no notifications lost)
    /// 4. NotifyDeserialized() — after load (suppress false positives)
    /// 5. Reset() — on new game
    /// 6. Unsubscribe() — on system destroy
    /// </summary>
    public interface INarrativeResolver
    {
        /// <summary>
        /// Domain name for logging (e.g., "Threat", "Blackout").
        /// </summary>
        string Domain { get; }

        /// <summary>
        /// Subscribe to EventBus events.
        /// Called once during NarrativeNotificationSystem initialization.
        /// </summary>
        void Subscribe();

        /// <summary>
        /// Unsubscribe from EventBus events.
        /// Called during NarrativeNotificationSystem destruction.
        /// </summary>
        void Unsubscribe();

        /// <summary>
        /// Called every frame for batch flushing.
        /// High-frequency resolvers flush pending events after batch window (Engine.Narrative.BATCH_WINDOW_SECONDS).
        /// Low-frequency resolvers use no-op implementation.
        /// </summary>
        /// <param name="currentTime">Current game time in hours (GameTimeSystem.TotalGameHours)</param>
        void Update(float currentTime);

        /// <summary>
        /// FIX NAR-P2-005: Flush all pending batches immediately.
        /// Called before game save to ensure no in-flight notifications are lost.
        /// Resolvers with pending batches should immediately push all notifications.
        /// </summary>
        void FlushAll();

        /// <summary>
        /// Called after deserialization. Suppress first transition notifications
        /// (internal state resets to defaults on load ≠ actual state change).
        /// Resolvers without transition detection can leave the default no-op.
        /// </summary>
        void NotifyDeserialized();

        /// <summary>
        /// Called on new game. Reset all internal state.
        /// </summary>
        void Reset();
    }
}
