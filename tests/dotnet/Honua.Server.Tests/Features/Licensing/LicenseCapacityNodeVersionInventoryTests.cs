// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Licensing;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Unit coverage for the pure mapping from the capacity meter's live-instance reading to the migration
/// node-version barrier snapshot (#2812).
/// </summary>
public sealed class LicenseCapacityNodeVersionInventoryTests
{
    [Fact]
    public void BuildSnapshot_NotCoordinated_ReportsInertBarrier()
    {
        var reading = new ActiveNodeVersionReading(
            Coordinated: false,
            LocalInstanceId: "self",
            LocalVersion: "2.0.0",
            Instances: new[] { new ActiveNodeVersionEntry("other", "1.9.0") });

        var snapshot = LicenseCapacityNodeVersionInventory.BuildSnapshot(reading);

        snapshot.Coordinated.Should().BeFalse();
        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void BuildSnapshot_ExcludesSelfInstance()
    {
        var reading = new ActiveNodeVersionReading(
            Coordinated: true,
            LocalInstanceId: "self",
            LocalVersion: "2.0.0",
            Instances: new[]
            {
                new ActiveNodeVersionEntry("self", "2.0.0"),
                new ActiveNodeVersionEntry("peer", "2.0.0"),
            });

        var snapshot = LicenseCapacityNodeVersionInventory.BuildSnapshot(reading);

        snapshot.Coordinated.Should().BeTrue();
        snapshot.OtherActiveVersions.Should().ContainSingle().Which.Should().Be("2.0.0");
        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void BuildSnapshot_ExcludesUnknownVersionNodes_NoFalsePositive()
    {
        // An older binary that never advertised a version must not be read as skew — otherwise the
        // first rollout that ships this feature would wedge on every multi-node cluster.
        var reading = new ActiveNodeVersionReading(
            Coordinated: true,
            LocalInstanceId: "self",
            LocalVersion: "2.0.0",
            Instances: new[]
            {
                new ActiveNodeVersionEntry("self", "2.0.0"),
                new ActiveNodeVersionEntry("legacy", null),
            });

        var snapshot = LicenseCapacityNodeVersionInventory.BuildSnapshot(reading);

        snapshot.OtherActiveVersions.Should().BeEmpty();
        snapshot.MixedVersionDetected.Should().BeFalse();
    }

    [Fact]
    public void BuildSnapshot_DetectsMixedVersion_WhenPeerRunsOlderVersion()
    {
        var reading = new ActiveNodeVersionReading(
            Coordinated: true,
            LocalInstanceId: "self",
            LocalVersion: "2.0.0",
            Instances: new[]
            {
                new ActiveNodeVersionEntry("self", "2.0.0"),
                new ActiveNodeVersionEntry("old", "1.9.0"),
            });

        var snapshot = LicenseCapacityNodeVersionInventory.BuildSnapshot(reading);

        snapshot.MixedVersionDetected.Should().BeTrue();
        snapshot.DivergentVersions.Should().ContainSingle().Which.Should().Be("1.9.0");
    }
}
