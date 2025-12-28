// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class TileMathTests
{
    [Theory]
    [InlineData(0, 0, 0, -20037508.342789244, -20037508.342789244, 20037508.342789244, 20037508.342789244)]
    [InlineData(0, 0, 1, -20037508.342789244, 0, 0, 20037508.342789244)]
    [InlineData(1, 0, 1, 0, 0, 20037508.342789244, 20037508.342789244)]
    [InlineData(0, 1, 1, -20037508.342789244, -20037508.342789244, 0, 0)]
    [InlineData(1, 1, 1, 0, -20037508.342789244, 20037508.342789244, 0)]
    public void GetTileBounds_ValidCoordinates_ReturnsCorrectBounds(
        int x, int y, int z, double expectedXMin, double expectedYMin, double expectedXMax, double expectedYMax)
    {
        // Act
        var bounds = TileMath.GetTileBounds(x, y, z);

        // Assert
        bounds.XMin.Should().BeApproximately(expectedXMin, 0.001);
        bounds.YMin.Should().BeApproximately(expectedYMin, 0.001);
        bounds.XMax.Should().BeApproximately(expectedXMax, 0.001);
        bounds.YMax.Should().BeApproximately(expectedYMax, 0.001);
    }

    [Theory]
    [InlineData(5, 1000.0)]
    [InlineData(8, 500.0)]
    [InlineData(10, 100.0)]
    [InlineData(12, 50.0)]
    [InlineData(15, 0.0)]
    public void GetSimplificationTolerance_VariousZoomLevels_ReturnsCorrectTolerance(int zoom, double expectedTolerance)
    {
        // Act
        var tolerance = TileMath.GetSimplificationTolerance(zoom);

        // Assert
        tolerance.Should().Be(expectedTolerance);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(0, 0, 1, true)]
    [InlineData(1, 1, 1, true)]
    [InlineData(255, 255, 8, true)]
    [InlineData(1023, 1023, 10, true)]
    [InlineData(-1, 0, 1, false)]
    [InlineData(0, -1, 1, false)]
    [InlineData(2, 0, 1, false)] // x >= maxTile (2^1 = 2)
    [InlineData(0, 2, 1, false)] // y >= maxTile (2^1 = 2)
    [InlineData(0, 0, -1, false)] // negative zoom
    [InlineData(0, 0, 23, false)] // zoom > 22
    public void ValidateTileCoordinates_VariousInputs_ReturnsExpectedResult(int x, int y, int z, bool expected)
    {
        // Act
        var result = TileMath.ValidateTileCoordinates(x, y, z);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetTileBounds_ZoomLevel10_HasCorrectTileSize()
    {
        // Arrange
        const int z = 10;
        const double expectedTileSize = 39135.75848201024; // 2 * 20037508.342789244 / 2^10

        // Act
        var bounds1 = TileMath.GetTileBounds(0, 0, z);
        var bounds2 = TileMath.GetTileBounds(1, 0, z);

        // Assert
        var actualTileSize = bounds2.XMin - bounds1.XMin;
        actualTileSize.Should().BeApproximately(expectedTileSize, 0.001);
    }

    [Fact]
    public void TileBounds_Record_HasCorrectProperties()
    {
        // Arrange
        const double xMin = -1000;
        const double yMin = -500;
        const double xMax = 1000;
        const double yMax = 500;

        // Act
        var bounds = new TileBounds(xMin, yMin, xMax, yMax);

        // Assert
        bounds.XMin.Should().Be(xMin);
        bounds.YMin.Should().Be(yMin);
        bounds.XMax.Should().Be(xMax);
        bounds.YMax.Should().Be(yMax);
    }
}
