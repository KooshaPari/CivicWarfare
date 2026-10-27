namespace CivicSurvival.Core.Interfaces.Core
{
    /// <summary>
    /// Called AFTER all systems have deserialized, BEFORE first OnUpdateImpl.
    /// Use for:
    /// - Cross-system consistency checks (e.g., sanctions state matches across 3 systems)
    /// - Time-base migration (e.g., PatriotExpiry from ElapsedTime to TotalGameHours)
    /// - Derived state reconstruction (e.g., rebuilding lookup tables from persisted data)
    ///
    /// Orchestrated by PostLoadValidationSystem (runs once after load, then disables).
    /// CivicSystemBase unregisters implementations automatically on system destroy.
    /// Systems are called in HydrationOrder (lower = earlier). Use explicit ordering
    /// when multiple systems write split capacity components (each owned exclusively)
    /// or when one validator depends on another's result (e.g., PlayerAttack needs ShadowWallet first).
    /// </summary>
    public interface IPostLoadValidation
    {
        /// <summary>
        /// Determines execution order during post-load validation.
        /// Lower values run first. Default is <see cref="HydrationPriority.DEFAULT"/> (no ordering requirement).
        /// Use <see cref="HydrationPriority"/> constants for systems with ordering dependencies.
        /// </summary>
        int HydrationOrder { get; }

        /// <summary>
        /// Validate and reconcile state after all systems have deserialized.
        /// Called once per load. Implementation should be idempotent.
        ///
        /// WARNING: ComponentLookup&lt;T&gt; / BufferLookup&lt;T&gt; fields used here must
        /// call .Update(this) before reading or writing. The method is invoked by
        /// PostLoadValidationSystem, so stale lookup safety handles are otherwise possible.
        /// Enforced by analyzer CIVIC288 for OnInitialize and expected here as well.
        ///
        /// This is the RECONCILE pass. Rebuild derived state from durable state;
        /// stamp/repair durable invariants. Do NOT destroy stale transient/orphan
        /// entities of a type other validators may still reconcile-read here —
        /// that goes in <see cref="PurgeAfterLoad"/>, which runs strictly after
        /// every reconcile pass.
        /// </summary>
        void ValidateAfterLoad();

        /// <summary>
        /// PURGE pass. Destroy stale transient/orphan entities this system owns.
        /// Runs once per load, after every <see cref="ValidateAfterLoad"/> across
        /// all validators (same HydrationOrder). Default no-op: only consumers
        /// that purge a shared transient type need to override it. Moving such a
        /// purge here (instead of ValidateAfterLoad) makes "no validator purges a
        /// transient another validator still reconciles from" structural rather
        /// than a HydrationOrder accident.
        ///
        /// Same lookup-staleness rule as ValidateAfterLoad: call .Update(this) on
        /// any ComponentLookup/BufferLookup before use.
        /// </summary>
        void PurgeAfterLoad();
    }

    /// <summary>
    /// Named constants for <see cref="IPostLoadValidation.HydrationOrder"/>.
    /// Documents the execution order contract between systems.
    ///
    /// Chain 1 — Split capacity modifier hydration:
    ///   OperationalDamage(10) → Disaster/Construction(20) → EquipmentWear(21)/assign(22)
    ///   → SnapshotPublish(25) → Readers(30)
    /// The snapshot publisher (PowerCapacityResolverSystem) must run after every split
    /// modifier writer but before any reader that reads the published capacity snapshot
    /// (EquipmentUISystem, GridStressSystem), otherwise the first post-load frame reads a
    /// stale or empty snapshot.
    ///
    /// Chain 2 — Wallet/Slot reconciliation:
    ///   ShadowWallet(50) → PlayerAttack(60)
    ///
    /// Chain 3 — Population-derived readers:
    ///   ResidentPopulationModel(80) → DEFAULT readers (100)
    /// </summary>
    public static class HydrationPriority
    {
        /// <summary>Cleanup orphaned mod entities before validators reconcile domain state.</summary>
        public const int CLEANUP_FIRST = 5;

        /// <summary>First split capacity modifier hydration (OperationalDamageSystem).</summary>
        public const int POWER_MODIFIERS_FIRST = 10;

        /// <summary>Mid-tier capacity modifier hydration (PowerPlantDisaster, ConstructionDelay).</summary>
        public const int POWER_MODIFIERS_MID = 20;

        /// <summary>Late capacity modifier hydration (EquipmentWear after disaster re-arm).</summary>
        public const int POWER_MODIFIERS_WEAR = 21;

        /// <summary>Publish the resolved capacity snapshot after all split modifier writers,
        /// before any snapshot reader (PowerCapacityResolverSystem).</summary>
        public const int POWER_SNAPSHOT_PUBLISH = 25;

        /// <summary>After all capacity modifier writers and snapshot publish (GridStressSystem,
        /// EquipmentUISystem read final state).</summary>
        public const int POWER_MODIFIERS_READER = 30;

        /// <summary>Cross-system reconciler (ShadowWallet — before dependents).</summary>
        public const int WALLET_RECONCILE = 50;

        /// <summary>Depends on wallet reconciliation (PlayerAttackSystem checks locks).</summary>
        public const int SLOT_RECONCILE = 60;

        /// <summary>Seeds resident population snapshots before default-order post-load readers.</summary>
        public const int POPULATION_SEED = 80;

        /// <summary>No ordering requirement — runs after all explicitly ordered systems.</summary>
        public const int DEFAULT = 100;
    }
}
