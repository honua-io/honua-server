// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Pass-through policy decision point. Always returns <see cref="PolicyDecision.Allow"/>,
/// modelling Community-tier behaviour (bring-your-own-model, no enforced guardrails).
/// </summary>
/// <remarks>
/// This is the default implementation of the guardrail seam in the de-risking spike.
/// Pro/Enterprise enforcement (approval lanes, dry-run-first, blast-radius limits,
/// RBAC) is a later phase that replaces this registration; the seam itself does not
/// change.
/// </remarks>
internal sealed class AllowAllPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    public Task<PolicyDecision> EvaluateAsync(
        HonuaOperationDefinition operation,
        OperationInvocationContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PolicyDecision.Allow);
}
