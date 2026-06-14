// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Resolves a stable <see cref="TemporalCheckpoint"/> down to a concrete change-tracker generation
/// (slices 2-5 of honua-server#1166). Every checkpoint kind — generation, timestamp, transaction,
/// release, job, edit session, named — is collapsed to a generation so the diff/timeline/rollback paths
/// share the slice-1 change-log foundation. The default resolver supports the generation cursor natively
/// and resolves timestamps against the change log; the remaining kinds resolve through the change log's
/// attribution/version columns where present, falling back to a validation error when unresolvable.
/// </summary>
public interface ITemporalCheckpointResolver
{
    /// <summary>
    /// Resolves a checkpoint for a layer to a concrete change-tracker generation.
    /// </summary>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="checkpoint">The checkpoint to resolve.</param>
    /// <param name="currentGeneration">The current change-tracker generation (the upper bound).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved checkpoint carrying the concrete generation.</returns>
    /// <exception cref="TemporalValidationException">Thrown when the checkpoint cannot be resolved.</exception>
    Task<ResolvedTemporalCheckpoint> ResolveAsync(
        int storageLayerId,
        TemporalCheckpoint checkpoint,
        long currentGeneration,
        CancellationToken cancellationToken = default);
}
