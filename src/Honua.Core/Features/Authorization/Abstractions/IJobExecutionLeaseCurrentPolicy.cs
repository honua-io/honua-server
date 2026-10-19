// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Closed current policy evaluation restricted to exact original sealed operations.</summary>
public interface IJobExecutionLeaseCurrentPolicy
{
    /// <summary>Checks current source lineage before and after canonical authorization and approval evaluation.</summary>
    /// <param name="request">An exact original canonical operation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only decisions, never a principal or execution capability.</returns>
    Task<JobExecutionLeaseCurrentPolicyDecision> EvaluateAsync(OperatorAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Current canonical authorization and additional approval requirement.</summary>
public sealed record JobExecutionLeaseCurrentPolicyDecision
{
    /// <summary>Current canonical access decision.</summary>
    public required AccessDecision Authorization { get; init; }

    /// <summary>Current approval requirement; evaluated only when authorization allows.</summary>
    public required ApprovalRequirement Approval { get; init; }
}
