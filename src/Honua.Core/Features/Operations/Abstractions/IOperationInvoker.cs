// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Single entry point for invoking operations from the deterministic surface (and, later,
/// the AI orchestrator). Resolves descriptor + executor by id, runs the policy decision
/// point, and only on Allow calls the executor. On Deny / RequireApproval / DryRunFirst it
/// returns a handle reflecting the decision WITHOUT touching the executor.
/// </summary>
public interface IOperationInvoker
{
    /// <summary>
    /// Validates an operation request via its executor (read-only, no policy gate).
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an operation: descriptor + executor lookup, policy decision, then execute on Allow.
    /// </summary>
    /// <param name="request">Operation request.</param>
    /// <param name="context">Resolved connection/principal/tier context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when an operation id has no registered descriptor or executor.
/// </summary>
public sealed class OperationNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="OperationNotFoundException"/>.
    /// </summary>
    /// <param name="operationId">The unresolved operation identifier.</param>
    public OperationNotFoundException(string operationId)
        : base($"Operation '{operationId}' is not registered.")
        => OperationId = operationId;

    /// <summary>
    /// The operation identifier that could not be resolved.
    /// </summary>
    public string OperationId { get; }
}
