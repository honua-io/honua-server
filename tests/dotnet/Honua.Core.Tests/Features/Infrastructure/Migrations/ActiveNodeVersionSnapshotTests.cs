// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Core.Tests.Features.Infrastructure.Migrations;

public sealed class ActiveNodeVersionSnapshotTests
{
    [Fact]
    public void NotCoordinated_IsNeverMixedVersion()
    {
        ActiveNodeVersionSnapshot.NotCoordinated.Coordinated.Should().BeFalse();
        ActiveNodeVersionSnapshot.NotCoordinated.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void MixedVersionDetected_WhenOtherNodeRunsDifferentVersion()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = "2.0.0",
            OtherActiveVersions = new[] { "1.9.0" },
        };

        snapshot.MixedVersionDetected.Should().BeTrue();
        snapshot.DivergentVersions.Should().ContainSingle().Which.Should().Be("1.9.0");
    }

    [Fact]
    public void NotMixed_WhenEveryOtherNodeRunsLocalVersion()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = "2.0.0",
            OtherActiveVersions = new[] { "2.0.0", "2.0.0" },
        };

        snapshot.MixedVersionDetected.Should().BeFalse();
        snapshot.DivergentVersions.Should().BeEmpty();
    }

    [Fact]
    public void NotMixed_WhenNoOtherNodes()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = "2.0.0",
            OtherActiveVersions = Array.Empty<string>(),
        };

        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void NotMixed_WhenNotCoordinatedEvenIfVersionsDiffer()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = false,
            LocalVersion = "2.0.0",
            OtherActiveVersions = new[] { "1.9.0" },
        };

        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void NotMixed_WhenLocalVersionUnknown()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = null,
            OtherActiveVersions = new[] { "1.9.0" },
        };

        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void DivergentVersions_AreDistinctAndInFirstSeenOrder()
    {
        var snapshot = new ActiveNodeVersionSnapshot
        {
            Coordinated = true,
            LocalVersion = "2.0.0",
            OtherActiveVersions = new[] { "1.9.0", "2.0.0", "1.8.0", "1.9.0" },
        };

        snapshot.DivergentVersions.Should().Equal("1.9.0", "1.8.0");
    }
}
