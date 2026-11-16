// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IRetentionPolicyEvaluator
{
    DateTimeOffset? ComputeExpiration(WorkspaceKind kind, DateTimeOffset createdAt);

    DateTimeOffset ClampExpiration(WorkspaceKind kind, DateTimeOffset createdAt, DateTimeOffset requestedExpiration);

    bool IsEligibleForPromotion(WorkspaceKind sourceKind, WorkspaceLifecycleState sourceState);

    QuotaEvaluation EvaluateQuota(WorkspaceUsageSummary usage, WorkspaceQuota quota);

    WorkspaceQuota GetConfiguredQuota();
}

public sealed record QuotaEvaluation
{
    public required bool IsWithinQuota { get; init; }

    public IReadOnlyList<string> Violations { get; init; } = [];

    public static QuotaEvaluation Allowed { get; } = new() { IsWithinQuota = true };

    public static QuotaEvaluation Exceeded(params string[] violations) => new()
    {
        IsWithinQuota = false,
        Violations = violations
    };
}
