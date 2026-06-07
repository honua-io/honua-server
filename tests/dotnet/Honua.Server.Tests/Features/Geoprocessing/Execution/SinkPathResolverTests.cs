// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage for <see cref="SinkPathResolver"/>, the containment guard shared by the file-writing
/// sink executors. Verifies the opt-in behavior: no root configured passes through unchanged, and a
/// configured root contains paths and rejects traversal/absolute escapes.
/// </summary>
public sealed class SinkPathResolverTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "honua-sink-root");

    [UnitTest]
    public void NoRootConfigured_ReturnsPathUnchanged()
    {
        var input = Path.Combine(Path.GetTempPath(), "anywhere.geojson");

        SinkPathResolver.TryResolve(null, input, out var resolved, out var error).Should().BeTrue();
        resolved.Should().Be(input);
        error.Should().BeNull();

        SinkPathResolver.TryResolve("   ", input, out resolved, out error).Should().BeTrue();
        resolved.Should().Be(input);
    }

    [UnitTest]
    public void RelativePath_IsResolvedUnderRoot()
    {
        SinkPathResolver.TryResolve(Root, Path.Combine("sub", "out.geojson"), out var resolved, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        resolved.Should().StartWith(Path.GetFullPath(Root));
        resolved.Should().EndWith("out.geojson");
    }

    [UnitTest]
    public void AbsolutePathInsideRoot_IsAllowed()
    {
        var inside = Path.Combine(Path.GetFullPath(Root), "nested", "out.geojson");

        SinkPathResolver.TryResolve(Root, inside, out var resolved, out var error).Should().BeTrue();
        error.Should().BeNull();
        resolved.Should().Be(inside);
    }

    [UnitTest]
    public void AbsolutePathOutsideRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "honua-elsewhere", "out.geojson");

        SinkPathResolver.TryResolve(Root, outside, out _, out var error).Should().BeFalse();
        error.Should().Contain("outside the configured sink root");
    }

    [UnitTest]
    public void TraversalEscape_IsRejected()
    {
        SinkPathResolver.TryResolve(Root, Path.Combine("..", "honua-elsewhere", "out.geojson"), out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("outside the configured sink root");
    }
}
