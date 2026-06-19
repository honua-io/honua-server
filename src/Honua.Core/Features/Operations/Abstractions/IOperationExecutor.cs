// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Executes a single operation. This is a SEPARATE contract from
/// <see cref="IOperationDescriptor"/>: the descriptor is groundable schema, the executor
/// does the work by wiring into existing server execution paths (publish, jobs, etc.).
/// </summary>
/// <remarks>
/// The contract is deliberately <c>Validate / Submit→Handle / GetStatus</c> rather than a
/// single flat <c>ExecuteAsync</c>. A flat sync execute cannot model the publish transaction,
/// the job-based geoprocessing path, or approval-lane "queued" outcomes; the handle returned
/// by <see cref="SubmitAsync"/> represents all three.
/// </remarks>
public interface IOperationExecutor
{
    /// <summary>
    /// Operation identifier this executor handles (matches a descriptor's <c>OperationId</c>).
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Read-only, idempotent validation/plan. Produces diagnostics without side effects.
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits the operation for execution and returns a handle. The handle — not a flat
    /// result — represents a completed sync op, a queued job, or a requires-approval outcome.
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="context">Resolved connection/principal/tier context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current status for a previously submitted handle (the async/job case).
    /// </summary>
    /// <param name="handle">Handle returned by <see cref="SubmitAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default);
}
