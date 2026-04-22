// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Tests for retention policy defaults and workspace quota evaluation.
/// </summary>
public class RetentionPolicyTests
{
    [Fact]
    public void Defaults_ContainsAllWorkspaceKinds()
    {
        var allKinds = Enum.GetValues<WorkspaceKind>();
        foreach (var kind in allKinds)
        {
            Assert.True(RetentionPolicy.Defaults.ContainsKey(kind),
                $"RetentionPolicy.Defaults missing entry for {kind}");
        }
    }

    [Theory]
    [InlineData(WorkspaceKind.Scratch)]
    [InlineData(WorkspaceKind.TempLayer)]
    [InlineData(WorkspaceKind.ResultCollection)]
    public void Defaults_TemporaryKinds_HaveDefaultTtl(WorkspaceKind kind)
    {
        var policy = RetentionPolicy.Defaults[kind];
        Assert.NotNull(policy.DefaultTimeToLive);
        Assert.True(policy.DefaultTimeToLive > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(WorkspaceKind.Persistent)]
    [InlineData(WorkspaceKind.SavedLayer)]
    public void Defaults_DurableKinds_HaveNoTtl(WorkspaceKind kind)
    {
        var policy = RetentionPolicy.Defaults[kind];
        Assert.Null(policy.DefaultTimeToLive);
        Assert.Null(policy.MaxTimeToLive);
    }

    [Theory]
    [InlineData(WorkspaceKind.Scratch)]
    [InlineData(WorkspaceKind.TempLayer)]
    [InlineData(WorkspaceKind.ResultCollection)]
    public void Defaults_TemporaryKinds_AllowPromotionBeforeCleanup(WorkspaceKind kind)
    {
        var policy = RetentionPolicy.Defaults[kind];
        Assert.True(policy.AllowPromotionBeforeCleanup);
    }

    [Fact]
    public void Defaults_ScratchMaxTtl_IsLessThanTempLayer()
    {
        var scratch = RetentionPolicy.Defaults[WorkspaceKind.Scratch];
        var tempLayer = RetentionPolicy.Defaults[WorkspaceKind.TempLayer];

        Assert.True(scratch.MaxTimeToLive < tempLayer.MaxTimeToLive);
    }

    [Fact]
    public void WorkspaceQuota_Default_HasReasonableLimits()
    {
        var quota = WorkspaceQuota.Default;
        Assert.NotNull(quota.MaxWorkspaceCount);
        Assert.NotNull(quota.MaxArtifactCount);
        Assert.NotNull(quota.MaxStorageBytes);
        Assert.True(quota.MaxWorkspaceCount > 0);
        Assert.True(quota.MaxArtifactCount > 0);
        Assert.True(quota.MaxStorageBytes > 0);
    }
}
