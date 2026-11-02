// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Admin;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Verifies the release-channel handshake for platform build versions.
/// </summary>
public sealed class AdminReleaseChannelTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("2026.1.0-rc.2", "rc")]
    [InlineData("2026.1.0-rc.2+abcdef0", "rc")]
    [InlineData("2026.1.0-nightly.abcdef0+abcdef0", "nightly")]
    [InlineData("2026.1.0-preview.1+abcdef0", "preview")]
    [InlineData("2026.1.0", "stable")]
    [InlineData("2026.1.0+nightly", "stable")]
    public void DeriveReleaseChannel_PlatformBuildVersion_ReturnsExpectedChannel(string version, string expected)
    {
        var method = typeof(AdminInfoEndpoints).GetMethod("DeriveReleaseChannel", BindingFlags.NonPublic | BindingFlags.Static)!;

        method.Invoke(null, [version]).Should().Be(expected);
    }
}
