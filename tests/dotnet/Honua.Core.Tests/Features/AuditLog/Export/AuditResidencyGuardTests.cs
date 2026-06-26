// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Tests.Features.AuditLog.Export;

/// <summary>
/// Unit tests for <see cref="AuditResidencyGuard"/> data-residency policy (#2157).
/// </summary>
public sealed class AuditResidencyGuardTests
{
    [Fact]
    public void IsAllowed_EmptyAllowList_PermitsEverything()
    {
        var guard = new AuditResidencyGuard(Array.Empty<string>());

        guard.IsRestricted.Should().BeFalse();
        guard.IsAllowed("eu-west-1").Should().BeTrue();
        guard.IsAllowed(null).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_NullAllowList_PermitsEverything()
    {
        var guard = new AuditResidencyGuard(null);

        guard.IsAllowed("anywhere").Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_Restricted_RejectsOutOfRegion()
    {
        var guard = new AuditResidencyGuard(new[] { "us-east-1" });

        guard.IsRestricted.Should().BeTrue();
        guard.IsAllowed("eu-west-1").Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_Restricted_RejectsNullRegion()
    {
        var guard = new AuditResidencyGuard(new[] { "us-east-1" });

        guard.IsAllowed(null).Should().BeFalse();
        guard.IsAllowed("   ").Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_Restricted_AllowsInRegionCaseInsensitively()
    {
        var guard = new AuditResidencyGuard(new[] { "US-East-1" });

        guard.IsAllowed("us-east-1").Should().BeTrue();
        guard.IsAllowed("US-EAST-1").Should().BeTrue();
    }

    [Fact]
    public void EnsureAllowed_OutOfRegion_Throws()
    {
        var guard = new AuditResidencyGuard(new[] { "us-east-1" });

        var act = () => guard.EnsureAllowed("eu-west-1");

        act.Should().Throw<AuditResidencyViolationException>()
            .Which.SinkRegion.Should().Be("eu-west-1");
    }

    [Fact]
    public void EnsureAllowed_InRegion_DoesNotThrow()
    {
        var guard = new AuditResidencyGuard(new[] { "us-east-1" });

        var act = () => guard.EnsureAllowed("us-east-1");

        act.Should().NotThrow();
    }
}
