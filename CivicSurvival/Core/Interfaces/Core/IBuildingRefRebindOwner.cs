using System;
using System.Collections.Generic;
using Unity.Entities;

namespace CivicSurvival.Core.Interfaces.Core
{
    /// <summary>
    /// Owns post-load reconciliation for mod entities that store indexed building refs.
    /// Runs before IPostLoadValidation so cleanup cannot purge an indexed ref before
    /// the owning domain has had a deterministic chance to rebind or rebuild from it.
    /// </summary>
    public interface IBuildingRefRebindOwner
    {
        /// <summary>Lower values run earlier during the post-load rebind phase.</summary>
        int RebindOrder { get; }

        /// <summary>
        /// Component types this owner has reconciled. Post-load cleanup for these
        /// components is blocked until the rebind owner completes successfully.
        /// Implementations typically expose a cached <see cref="Type"/>[] which is
        /// assignment-compatible with <see cref="IReadOnlyList{T}"/>; the read-only
        /// contract avoids the CA1819 mutable-array-property smell.
        /// </summary>
        IReadOnlyList<Type> ReboundComponentTypes { get; }

        /// <summary>
        /// Rebind or rebuild state that depends on indexed building references.
        /// Implementations must be idempotent and refresh any ComponentLookup fields
        /// before reading them, same as IPostLoadValidation.
        /// </summary>
        void RebindBuildingRefsAfterLoad(EntityManager entityManager);
    }
}
