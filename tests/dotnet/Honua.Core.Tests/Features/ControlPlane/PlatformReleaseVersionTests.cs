// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Tests for the platform-release <c>targetVersion</c> comparison rule (rc.3 fix unit S3): the
/// <c>honua-</c> tag prefix, a <c>v</c> prefix, surrounding whitespace, case, and a zero patch are
/// normalized; everything else stays a distinct release.
/// </summary>
public sealed class PlatformReleaseVersionTests
{
    [Theory]
    [InlineData("2026.1-rc.3", "2026.1-rc.3")]
    [InlineData("2026.1.0-rc.3", "2026.1-rc.3")]
    [InlineData("honua-2026.1-rc.3", "2026.1-rc.3")]
    [InlineData("HONUA-2026.1.0-RC.3", "2026.1-rc.3")]
    [InlineData("v2026.1-rc.3", "2026.1-rc.3")]
    [InlineData("honua-v2026.1.0-rc.3", "2026.1-rc.3")]
    [InlineData("  2026.1-rc.3  ", "2026.1-rc.3")]
    [InlineData("2026.07.0", "2026.07")]
    [InlineData("2026.1.2-rc.3", "2026.1.2-rc.3")]
    [InlineData("2026.1+build.7", "2026.1+build.7")]
    [InlineData("2026.07.0-smoke", "2026.07-smoke")]
    public void Normalize_StripsPrefixesAndZeroPatch(string input, string expected)
        => Assert.Equal(expected, PlatformReleaseVersion.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_Empty_ReturnsNull(string? input)
        => Assert.Null(PlatformReleaseVersion.Normalize(input));

    [Fact]
    public void Normalize_LoneVIsNotAPrefix()
        => Assert.Equal("vnext", PlatformReleaseVersion.Normalize("vnext"));

    [Theory]
    [InlineData("2026.1-rc.3", "2026.1-rc.3")]
    [InlineData("2026.1-rc.3", "honua-2026.1-rc.3")]
    [InlineData("2026.1.0-rc.3", "v2026.1-rc.3")]
    [InlineData("2026.1-rc.3", "2026.1.0-rc.3")]
    public void Matches_EquivalentForms_AreTheSameRelease(string declared, string target)
        => Assert.True(PlatformReleaseVersion.Matches(declared, target));

    [Theory]
    [InlineData("2026.1-rc.3", "2026.1-rc.4")]
    [InlineData("2026.1-rc.3", "2026.1")]
    [InlineData("2026.1-rc.3", "2026.1.1-rc.3")]
    [InlineData("2026.1-rc.3", "2026.01-rc.3")]
    [InlineData("2026.1-rc.3", "")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Matches_DifferentOrEmpty_IsAMismatch(string? declared, string? target)
        => Assert.False(PlatformReleaseVersion.Matches(declared, target));
}
