// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Unit tests for the WCS 2.0 Scaling extension arithmetic used by the WCS
/// protocol adapter to translate SCALEFACTOR/SCALEAXES/SCALEEXTENT into explicit
/// output grid dimensions.
/// </summary>
public sealed class CoverageScalingTests
{
    [UnitTest]
    public void TryResolveFactor_HalvesBothAxes()
    {
        CoverageScaling.TryResolveFactor(64, 32, 0.5, out var result).Should().BeTrue();
        result.Width.Should().Be(32);
        result.Height.Should().Be(16);
    }

    [UnitTest]
    public void TryResolveFactor_RoundsAwayFromZeroAndNeverDropsBelowOne()
    {
        CoverageScaling.TryResolveFactor(3, 3, 0.5, out var result).Should().BeTrue();
        result.Width.Should().Be(2); // 1.5 rounds away from zero to 2.
        result.Height.Should().Be(2);

        CoverageScaling.TryResolveFactor(1, 1, 0.01, out var tiny).Should().BeTrue();
        tiny.Width.Should().Be(1);
        tiny.Height.Should().Be(1);
    }

    [UnitTest]
    public void TryResolveFactor_RejectsNonPositiveFactorOrGrid()
    {
        CoverageScaling.TryResolveFactor(64, 64, 0, out _).Should().BeFalse();
        CoverageScaling.TryResolveFactor(64, 64, -1, out _).Should().BeFalse();
        CoverageScaling.TryResolveFactor(0, 64, 2, out _).Should().BeFalse();
        CoverageScaling.TryResolveFactor(64, 64, double.NaN, out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryResolveAxes_ScalesEachAxisIndependently()
    {
        CoverageScaling.TryResolveAxes(64, 64, 0.5, 2, out var result).Should().BeTrue();
        result.Width.Should().Be(32);
        result.Height.Should().Be(128);
    }

    [UnitTest]
    public void TryResolveAxes_LeavesOmittedAxisUnchanged()
    {
        CoverageScaling.TryResolveAxes(64, 48, 0.5, null, out var result).Should().BeTrue();
        result.Width.Should().Be(32);
        result.Height.Should().Be(48);
    }

    [UnitTest]
    public void TryResolveAxes_RejectsWhenNoAxisSupplied()
    {
        CoverageScaling.TryResolveAxes(64, 64, null, null, out _).Should().BeFalse();
    }

    [UnitTest]
    public void TryResolveExtent_UsesExplicitSizes()
    {
        CoverageScaling.TryResolveExtent(64, 64, 100, 200, out var result).Should().BeTrue();
        result.Width.Should().Be(100);
        result.Height.Should().Be(200);
    }

    [UnitTest]
    public void TryResolveExtent_LeavesOmittedAxisAtBaseSize()
    {
        CoverageScaling.TryResolveExtent(64, 48, 100, null, out var result).Should().BeTrue();
        result.Width.Should().Be(100);
        result.Height.Should().Be(48);
    }

    [UnitTest]
    public void TryResolveExtent_RejectsNonPositiveSize()
    {
        CoverageScaling.TryResolveExtent(64, 64, 0, 10, out _).Should().BeFalse();
        CoverageScaling.TryResolveExtent(64, 64, null, null, out _).Should().BeFalse();
    }
}
