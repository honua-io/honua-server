// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Durable approval persistence seam used after policy requires approval.
/// </summary>
/// <remarks>
/// Implementations must return success only after both the exact bounded request and its
/// canonical envelope have been durably accepted and the proposal audit write has returned
/// an assigned identity. This interface does not authorize an independent policy or actuator
/// path; runtime convergence remains the responsibility of the canonical runtime.
/// </remarks>
public interface IOperationApprovalBridge
{
    /// <summary>
    /// Persists an approval-required request and returns its joinable durable identities.
    /// </summary>
    /// <param name="descriptor">Resolved typed operation descriptor.</param>
    /// <param name="request">Exact bounded typed request.</param>
    /// <param name="context">Trusted caller and canonical invocation context.</param>
    /// <param name="decision">Policy decision that required approval.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationApprovalBridgeResult> CreateProposalAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from the durable approval persistence seam.
/// </summary>
public sealed record OperationApprovalBridgeResult
{
    /// <summary>
    /// Whether durable proposal and audit persistence both succeeded.
    /// </summary>
    public required bool IsDurable { get; init; }

    /// <summary>
    /// Durable proposal identity. Required when <see cref="IsDurable"/> is true.
    /// </summary>
    public string? ProposalId { get; init; }

    /// <summary>
    /// Durable audit identity assigned at write time. Required when
    /// <see cref="IsDurable"/> is true.
    /// </summary>
    public string? AuditId { get; init; }

    /// <summary>
    /// Human-readable persistence failure or approval routing detail.
    /// </summary>
    public string? Reason { get; init; }
}
