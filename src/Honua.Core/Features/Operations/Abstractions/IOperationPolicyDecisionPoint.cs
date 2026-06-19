// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Policy seam consulted before any operation side effect. Every submit flows through this
/// decision point; only an <see cref="PolicyDecisionKind.Allow"/> reaches the executor.
/// </summary>
/// <remarks>
/// This is the guardrail seam, designed now so Phase 4's tier/role-aware policy engine is
/// additive rather than a rewrite. The default implementation
/// (<see cref="AllowAllPolicyDecisionPoint"/>) is a no-op pass-through = Community tier.
/// </remarks>
public interface IOperationPolicyDecisionPoint
{
    /// <summary>
    /// Evaluates the policy decision for an operation invocation.
    /// </summary>
    /// <param name="descriptor">The descriptor of the operation being invoked.</param>
    /// <param name="request">The operation request.</param>
    /// <param name="context">Resolved connection/principal/tier context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PolicyDecision> EvaluateAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op pass-through policy decision point: always allows. This is the Community tier
/// behavior (bring-your-own-model, no policy gate). The seam is real even with this default —
/// a stricter decision point swapped in (Pro/Enterprise) short-circuits the executor without
/// any executor change.
/// </summary>
public sealed class AllowAllPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    /// <inheritdoc />
    public Task<PolicyDecision> EvaluateAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PolicyDecision.Allowed);
}
