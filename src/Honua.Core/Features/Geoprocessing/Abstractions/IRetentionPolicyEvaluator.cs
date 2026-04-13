// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Evaluates retention policy rules for workspaces and artifacts.
/// </summary>
public interface IRetentionPolicyEvaluator
{
    /// <summary>
    /// Computes the expiration time for a new workspace based on its kind and the active retention policy.
    /// Returns null when the workspace kind has no automatic expiration.
    /// </summary>
    DateTimeOffset? ComputeExpiration(WorkspaceKind kind, DateTimeOffset createdAt);

    /// <summary>
    /// Clamps a requested expiration extension to the maximum allowed by policy.
    /// Returns the effective expiration, which may be earlier than requested.
    /// </summary>
    DateTimeOffset ClampExpiration(WorkspaceKind kind, DateTimeOffset createdAt, DateTimeOffset requestedExpiration);

    /// <summary>
    /// Returns whether an artifact in the given workspace kind is eligible for promotion
    /// when the workspace is in the specified lifecycle state.
    /// </summary>
    bool IsEligibleForPromotion(WorkspaceKind sourceKind, WorkspaceLifecycleState sourceState);

    /// <summary>
    /// Evaluates whether adding a workspace would exceed the quota for the given usage summary.
    /// </summary>
    QuotaEvaluation EvaluateQuota(WorkspaceUsageSummary usage, WorkspaceQuota quota);

    /// <summary>
    /// Returns the configured workspace quota, using defaults for any values not overridden
    /// via configuration.
    /// </summary>
    WorkspaceQuota GetConfiguredQuota();
}

/// <summary>
/// Result of a quota evaluation.
/// </summary>
public sealed record QuotaEvaluation
{
    /// <summary>
    /// Whether the quota allows the requested operation.
    /// </summary>
    public required bool IsWithinQuota { get; init; }

    /// <summary>
    /// Reasons the quota would be exceeded, when applicable.
    /// </summary>
    public IReadOnlyList<string> Violations { get; init; } = [];

    /// <summary>
    /// Creates a passing evaluation.
    /// </summary>
    public static QuotaEvaluation Allowed { get; } = new() { IsWithinQuota = true };

    /// <summary>
    /// Creates a failing evaluation with the given violations.
    /// </summary>
    public static QuotaEvaluation Exceeded(params string[] violations) => new()
    {
        IsWithinQuota = false,
        Violations = violations
    };
}
