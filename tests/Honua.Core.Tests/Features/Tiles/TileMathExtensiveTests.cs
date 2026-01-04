// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Tiles;

/// <summary>
/// Extensive tests for TileMath covering edge cases and invariants.
/// </summary>
public class TileMathExtensiveTests
{
    private const double WebMercatorExtent = 20037508.342789244;

    [UnitTest]
    public void GetTileBounds_ZoomZero_ShouldCoverFullExtent()
    {
        var bounds = TileMath.GetTileBounds(0, 0, 0);

        bounds.XMin.Should().BeApproximately(-WebMercatorExtent, 0.001);
        bounds.YMin.Should().BeApproximately(-WebMercatorExtent, 0.001);
        bounds.XMax.Should().BeApproximately(WebMercatorExtent, 0.001);
        bounds.YMax.Should().BeApproximately(WebMercatorExtent, 0.001);
    }

    [UnitTest]
    public void GetTileBounds_AdjacentTiles_ShouldShareBoundary()
    {
        var left = TileMath.GetTileBounds(0, 0, 2);
        var right = TileMath.GetTileBounds(1, 0, 2);
        var below = TileMath.GetTileBounds(0, 1, 2);

        left.XMax.Should().BeApproximately(right.XMin, 0.001);
        left.YMin.Should().BeApproximately(below.YMax, 0.001);
    }

    [UnitTest]
    public void GetTileBounds_ZoomIncrement_ShouldHalveTileSize()
    {
        var zoom2 = TileMath.GetTileBounds(0, 0, 2);
        var zoom3 = TileMath.GetTileBounds(0, 0, 3);

        var size2 = zoom2.XMax - zoom2.XMin;
        var size3 = zoom3.XMax - zoom3.XMin;

        size3.Should().BeApproximately(size2 / 2.0, 0.001);
    }

    [UnitTest]
    public void ValidateTileCoordinates_MaxZoom_ShouldAllowLastTileOnly()
    {
        const int z = 22;
        var maxIndex = (1 << z) - 1;

        TileMath.ValidateTileCoordinates(maxIndex, maxIndex, z).Should().BeTrue();
        TileMath.ValidateTileCoordinates(maxIndex + 1, maxIndex, z).Should().BeFalse();
        TileMath.ValidateTileCoordinates(maxIndex, maxIndex + 1, z).Should().BeFalse();
    }

    [UnitTest]
    public void GetSimplificationTolerance_ShouldUseDefinedBuckets()
    {
        TileMath.GetSimplificationTolerance(5).Should().Be(1000.0);
        TileMath.GetSimplificationTolerance(8).Should().Be(500.0);
        TileMath.GetSimplificationTolerance(10).Should().Be(100.0);
        TileMath.GetSimplificationTolerance(12).Should().Be(50.0);
        TileMath.GetSimplificationTolerance(13).Should().Be(0.0);
    }
}
