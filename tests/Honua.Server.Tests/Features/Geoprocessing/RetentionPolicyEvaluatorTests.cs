// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Tests for the retention policy evaluator.
/// </summary>
public class RetentionPolicyEvaluatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    private static RetentionPolicyEvaluator CreateEvaluator(WorkspaceOptions? options = null)
    {
        return new RetentionPolicyEvaluator(
            Options.Create(options ?? new WorkspaceOptions()));
    }

    [Theory]
    [InlineData(WorkspaceKind.Scratch)]
    [InlineData(WorkspaceKind.TempLayer)]
    [InlineData(WorkspaceKind.ResultCollection)]
    public void ComputeExpiration_TemporaryKinds_ReturnsExpiration(WorkspaceKind kind)
    {
        var evaluator = CreateEvaluator();

        var expiration = evaluator.ComputeExpiration(kind, BaseTime);

        Assert.NotNull(expiration);
        Assert.True(expiration > BaseTime);
    }

    [Theory]
    [InlineData(WorkspaceKind.Persistent)]
    [InlineData(WorkspaceKind.SavedLayer)]
    public void ComputeExpiration_DurableKinds_ReturnsNull(WorkspaceKind kind)
    {
        var evaluator = CreateEvaluator();

        var expiration = evaluator.ComputeExpiration(kind, BaseTime);

        Assert.Null(expiration);
    }

    [Fact]
    public void ComputeExpiration_ScratchDefault_IsOneHour()
    {
        var evaluator = CreateEvaluator();

        var expiration = evaluator.ComputeExpiration(WorkspaceKind.Scratch, BaseTime);

        Assert.Equal(BaseTime.AddHours(1), expiration);
    }

    [Fact]
    public void ComputeExpiration_WithConfigOverride_UsesOverride()
    {
        var evaluator = CreateEvaluator(new WorkspaceOptions
        {
            ScratchDefaultTtl = TimeSpan.FromHours(2)
        });

        var expiration = evaluator.ComputeExpiration(WorkspaceKind.Scratch, BaseTime);

        Assert.Equal(BaseTime.AddHours(2), expiration);
    }

    [Fact]
    public void ClampExpiration_WithinMax_ReturnsRequested()
    {
        var evaluator = CreateEvaluator();
        var requested = BaseTime.AddHours(12);

        var clamped = evaluator.ClampExpiration(WorkspaceKind.Scratch, BaseTime, requested);

        Assert.Equal(requested, clamped);
    }

    [Fact]
    public void ClampExpiration_ExceedsMax_ClampsToMax()
    {
        var evaluator = CreateEvaluator();
        var requested = BaseTime.AddDays(30);

        var clamped = evaluator.ClampExpiration(WorkspaceKind.Scratch, BaseTime, requested);

        Assert.Equal(BaseTime.AddHours(24), clamped);
    }

    [Fact]
    public void ClampExpiration_DurableKind_ReturnsRequested()
    {
        var evaluator = CreateEvaluator();
        var requested = BaseTime.AddDays(365);

        var clamped = evaluator.ClampExpiration(WorkspaceKind.Persistent, BaseTime, requested);

        Assert.Equal(requested, clamped);
    }

    [Theory]
    [InlineData(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active, true)]
    [InlineData(WorkspaceKind.Scratch, WorkspaceLifecycleState.Expired, true)]
    [InlineData(WorkspaceKind.Scratch, WorkspaceLifecycleState.Deleted, false)]
    [InlineData(WorkspaceKind.Scratch, WorkspaceLifecycleState.Archived, false)]
    [InlineData(WorkspaceKind.Persistent, WorkspaceLifecycleState.Active, true)]
    [InlineData(WorkspaceKind.Persistent, WorkspaceLifecycleState.Expired, false)]
    public void IsEligibleForPromotion_ReturnsExpected(
        WorkspaceKind kind, WorkspaceLifecycleState state, bool expected)
    {
        var evaluator = CreateEvaluator();

        var eligible = evaluator.IsEligibleForPromotion(kind, state);

        Assert.Equal(expected, eligible);
    }

    [Fact]
    public void EvaluateQuota_WithinLimits_ReturnsAllowed()
    {
        var evaluator = CreateEvaluator();
        var usage = new WorkspaceUsageSummary
        {
            ActiveWorkspaceCount = 5,
            TotalArtifactCount = 10,
            TotalStorageBytes = 1024
        };

        var result = evaluator.EvaluateQuota(usage, WorkspaceQuota.Default);

        Assert.True(result.IsWithinQuota);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EvaluateQuota_WorkspaceCountExceeded_ReturnsViolation()
    {
        var evaluator = CreateEvaluator();
        var usage = new WorkspaceUsageSummary
        {
            ActiveWorkspaceCount = 100,
            TotalArtifactCount = 0,
            TotalStorageBytes = 0
        };
        var quota = new WorkspaceQuota { MaxWorkspaceCount = 100 };

        var result = evaluator.EvaluateQuota(usage, quota);

        Assert.False(result.IsWithinQuota);
        Assert.Single(result.Violations);
    }

    [Fact]
    public void EvaluateQuota_MultipleViolations_ReportsAll()
    {
        var evaluator = CreateEvaluator();
        var usage = new WorkspaceUsageSummary
        {
            ActiveWorkspaceCount = 10,
            TotalArtifactCount = 50,
            TotalStorageBytes = 1024 * 1024 * 1024
        };
        var quota = new WorkspaceQuota
        {
            MaxWorkspaceCount = 5,
            MaxArtifactCount = 25,
            MaxStorageBytes = 512 * 1024 * 1024
        };

        var result = evaluator.EvaluateQuota(usage, quota);

        Assert.False(result.IsWithinQuota);
        Assert.Equal(3, result.Violations.Count);
    }
}
