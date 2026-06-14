// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Submits a governed rollback as a NEW forward corrective operation through the job runner (slice 5 of
/// honua-server#1166). A rollback never deletes change-log history: the corrective job re-applies the
/// target state through the canonical edit pipeline, which records new change-log rows and advances the
/// generation, producing a new checkpoint while leaving the immutable audit history intact.
/// </summary>
/// <remarks>
/// This abstraction isolates the canonical temporal service from the job-runner wiring. The default
/// implementation enqueues a corrective feature-edit job and returns its handle for polling; deployments
/// that route corrective work through a specialized batch backend can substitute their own runner.
/// </remarks>
public interface ITemporalRollbackRunner
{
    /// <summary>
    /// Submits an approved rollback as a forward corrective job and returns a handle for polling.
    /// </summary>
    /// <param name="serviceId">Owning service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="target">The resolved checkpoint the corrective job restores the layer to.</param>
    /// <param name="reason">Operator-supplied reason recorded in the corrective operation's attribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle to the submitted corrective job.</returns>
    Task<TemporalRollbackJobHandle> SubmitRollbackAsync(
        string serviceId,
        int layerId,
        int storageLayerId,
        ResolvedTemporalCheckpoint target,
        string? reason,
        CancellationToken cancellationToken = default);
}
