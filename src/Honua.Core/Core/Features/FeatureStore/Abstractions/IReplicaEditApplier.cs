// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Services;

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IReplicaEditApplier
{
    /// <summary>
    /// Applies the supplied edits for a single layer through the shared edit pipeline.
    /// </summary>
    /// <param name="serviceId">Service the layer belongs to.</param>
    /// <param name="publicLayerId">Service-local layer id the edits target.</param>
    /// <param name="edits">Resolved edits to apply (conflicting edits already filtered when skipped).</param>
    /// <param name="rollbackOnFailure">
    /// When true, the layer's edits are applied atomically so a single failing row rolls back the whole
    /// batch (leaving the layer's server state unchanged); when false, edits apply best-effort per row
    /// (#2136).
    /// </param>
    /// <param name="preconditions">
    /// Optimistic-concurrency preconditions to carry into the write transaction, keyed by object id.
    /// Supplied under manual review, where the mode's contract is that a conflicting edit is withheld:
    /// a server edit committing between conflict detection and this write would otherwise be silently
    /// overwritten by an edit detection had judged non-conflicting. Empty leaves the write
    /// unconditional (#2430).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-layer apply result.</returns>
    Task<ReplicaLayerApplyResult> ApplyAsync(
        string serviceId,
        int publicLayerId,
        ImmutableArray<ReplicaUploadEdit> edits,
        bool rollbackOnFailure,
        ImmutableArray<FeatureEditPrecondition> preconditions = default,
        CancellationToken cancellationToken = default);
}
