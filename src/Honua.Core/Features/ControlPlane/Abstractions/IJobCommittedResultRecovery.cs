// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Optional executor contract for reconciling an already committed sink transaction
/// without restoring execution authority or performing another source read or mutation.
/// </summary>
public interface IJobCommittedResultRecovery
{
    /// <summary>
    /// Reads persistence evidence bound to the original job intent and records its
    /// committed-effect reference through the current worker context before returning success.
    /// </summary>
    /// <param name="job">The job claimed by the current worker attempt.</param>
    /// <param name="context">The context enforcing current ownership and attempt fences.</param>
    /// <param name="cancellationToken">An independent bounded reconciliation token.</param>
    /// <returns>
    /// The original successful result with committed effects, or null when no committed
    /// result exists. Null never grants permission to execute; ordinary authorization and
    /// license checks still apply. Unavailable or inconsistent evidence must throw.
    /// </returns>
    Task<JobExecutionResult?> TryRecoverCommittedAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken = default);
}
