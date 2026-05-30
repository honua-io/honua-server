// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Evaluates retention policies using configured overrides and built-in defaults.
/// </summary>
internal sealed class RetentionPolicyEvaluator : IRetentionPolicyEvaluator
{
    private readonly Dictionary<WorkspaceKind, RetentionPolicy> _policies;
    private readonly WorkspaceQuota _configuredQuota;

    public RetentionPolicyEvaluator(IOptions<WorkspaceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _policies = BuildPolicies(options.Value);
        _configuredQuota = BuildQuota(options.Value);
    }

    public DateTimeOffset? ComputeExpiration(WorkspaceKind kind, DateTimeOffset createdAt)
    {
        if (!_policies.TryGetValue(kind, out var policy) || policy.DefaultTimeToLive is null)
            return null;

        return createdAt + policy.DefaultTimeToLive.Value;
    }

    public DateTimeOffset ClampExpiration(WorkspaceKind kind, DateTimeOffset createdAt, DateTimeOffset requestedExpiration)
    {
        if (!_policies.TryGetValue(kind, out var policy) || policy.MaxTimeToLive is null)
            return requestedExpiration;

        var maxExpiration = createdAt + policy.MaxTimeToLive.Value;
        return requestedExpiration > maxExpiration ? maxExpiration : requestedExpiration;
    }

    public bool IsEligibleForPromotion(WorkspaceKind sourceKind, WorkspaceLifecycleState sourceState)
    {
        if (!_policies.TryGetValue(sourceKind, out var policy))
            return false;

        // Only temporary workspace kinds are valid promotion sources.
        if (sourceKind is not (WorkspaceKind.Scratch or WorkspaceKind.TempLayer or WorkspaceKind.ResultCollection))
            return false;

        return sourceState switch
        {
            WorkspaceLifecycleState.Active => true,
            WorkspaceLifecycleState.Expired => policy.AllowPromotionBeforeCleanup,
            _ => false
        };
    }

    public WorkspaceQuota GetConfiguredQuota() => _configuredQuota;

    public QuotaEvaluation EvaluateQuota(WorkspaceUsageSummary usage, WorkspaceQuota quota)
    {
        var violations = new List<string>();

        if (quota.MaxWorkspaceCount.HasValue && usage.ActiveWorkspaceCount >= quota.MaxWorkspaceCount.Value)
            violations.Add($"Workspace count {usage.ActiveWorkspaceCount} would exceed limit of {quota.MaxWorkspaceCount.Value}");

        if (quota.MaxArtifactCount.HasValue && usage.TotalArtifactCount >= quota.MaxArtifactCount.Value)
            violations.Add($"Artifact count {usage.TotalArtifactCount} would exceed limit of {quota.MaxArtifactCount.Value}");

        if (quota.MaxStorageBytes.HasValue && usage.TotalStorageBytes >= quota.MaxStorageBytes.Value)
            violations.Add($"Storage {usage.TotalStorageBytes} bytes would exceed limit of {quota.MaxStorageBytes.Value} bytes");

        return violations.Count > 0
            ? QuotaEvaluation.Exceeded(violations.ToArray())
            : QuotaEvaluation.Allowed;
    }

    private static Dictionary<WorkspaceKind, RetentionPolicy> BuildPolicies(WorkspaceOptions options)
    {
        var policies = new Dictionary<WorkspaceKind, RetentionPolicy>(RetentionPolicy.Defaults);

        if (options.ScratchDefaultTtl.HasValue)
        {
            policies[WorkspaceKind.Scratch] = policies[WorkspaceKind.Scratch] with
            {
                DefaultTimeToLive = options.ScratchDefaultTtl.Value
            };
        }

        if (options.TempLayerDefaultTtl.HasValue)
        {
            policies[WorkspaceKind.TempLayer] = policies[WorkspaceKind.TempLayer] with
            {
                DefaultTimeToLive = options.TempLayerDefaultTtl.Value
            };
        }

        if (options.ResultCollectionDefaultTtl.HasValue)
        {
            policies[WorkspaceKind.ResultCollection] = policies[WorkspaceKind.ResultCollection] with
            {
                DefaultTimeToLive = options.ResultCollectionDefaultTtl.Value
            };
        }

        return policies;
    }

    private static WorkspaceQuota BuildQuota(WorkspaceOptions options) => new()
    {
        MaxWorkspaceCount = options.MaxWorkspaceCount ?? WorkspaceQuota.Default.MaxWorkspaceCount,
        MaxArtifactCount = options.MaxArtifactCount ?? WorkspaceQuota.Default.MaxArtifactCount,
        MaxStorageBytes = options.MaxStorageBytes ?? WorkspaceQuota.Default.MaxStorageBytes
    };
}
