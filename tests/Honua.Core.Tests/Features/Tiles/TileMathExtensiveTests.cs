// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using FsCheck;
using Honua.Core.Features.Tiles;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Tiles;

/// <summary>
/// Extensive tests for TileMath covering edge cases and invariants
/// </summary>
public class TileMathExtensiveTests
{
    [UnitTest]
    public Property LonLatToTileXY_ShouldProduceValidCoordinates()
    {
        var genValidLon = Gen.Choose(-180, 180).Select(x => x / 1.0);
        var genValidLat = Gen.Choose(-85, 85).Select(x => x / 1.0); // Web Mercator limits
        var genValidZoom = Gen.Choose(0, 18);

        return Prop.ForAll(
            Arb.From(genValidLon),
            Arb.From(genValidLat),
            Arb.From(genValidZoom),
            (lon, lat, zoom) =>
            {
                var (tileX, tileY) = TileMath.LonLatToTileXY(lon, lat, zoom);
                var maxTileCoord = Math.Pow(2, zoom);

                return tileX >= 0 && tileX < maxTileCoord &&
                       tileY >= 0 && tileY < maxTileCoord;
            });
    }

    [UnitTest]
    public Property TileXYToLonLat_ShouldProduceValidCoordinates()
    {
        var genValidZoom = Gen.Choose(0, 18);

        return Prop.ForAll(
            Arb.From(genValidZoom),
            zoom =>
            {
                var maxTileCoord = (int)Math.Pow(2, zoom);
                var genValidTileX = Gen.Choose(0, maxTileCoord - 1);
                var genValidTileY = Gen.Choose(0, maxTileCoord - 1);

                return Prop.ForAll(
                    Arb.From(genValidTileX),
                    Arb.From(genValidTileY),
                    (tileX, tileY) =>
                    {
                        var (lon, lat) = TileMath.TileXYToLonLat(tileX, tileY, zoom);

                        return lon >= -180 && lon <= 180 &&
                               lat >= -85.0511 && lat <= 85.0511; // Web Mercator limits
                    });
            });
    }

    [UnitTest]
    public Property RoundTripConversion_ShouldBeConsistent()
    {
        var genValidLon = Gen.Choose(-179, 179).Select(x => x / 1.0);
        var genValidLat = Gen.Choose(-84, 84).Select(x => x / 1.0);
        var genValidZoom = Gen.Choose(1, 15); // Use smaller range for precision

        return Prop.ForAll(
            Arb.From(genValidLon),
            Arb.From(genValidLat),
            Arb.From(genValidZoom),
            (originalLon, originalLat, zoom) =>
            {
                var (tileX, tileY) = TileMath.LonLatToTileXY(originalLon, originalLat, zoom);
                var (convertedLon, convertedLat) = TileMath.TileXYToLonLat(tileX, tileY, zoom);

                var lonDiff = Math.Abs(originalLon - convertedLon);
                var latDiff = Math.Abs(originalLat - convertedLat);

                // Allow for some precision loss due to tile quantization
                var maxError = 180.0 / Math.Pow(2, zoom); // Tile size in degrees

                return lonDiff <= maxError && latDiff <= maxError;
            });
    }

    [UnitTest]
    public void LonLatToTileXY_AtEquator_ShouldProduceCorrectResults()
    {
        // Arrange & Act
        var (tileX, tileY) = TileMath.LonLatToTileXY(0, 0, 1);

        // Assert - At zoom level 1, equator should be in tile (1, 1)
        tileX.Should().Be(1);
        tileY.Should().Be(1);
    }

    [UnitTest]
    public void LonLatToTileXY_AtPrimeMeridian_ShouldProduceCorrectResults()
    {
        // Arrange & Act
        var (tileX, tileY) = TileMath.LonLatToTileXY(0, 0, 0);

        // Assert - At zoom level 0, everything should be in tile (0, 0)
        tileX.Should().Be(0);
        tileY.Should().Be(0);
    }

    [UnitTest]
    public void LonLatToTileXY_AtMaximumLongitude_ShouldHandleCorrectly()
    {
        // Arrange & Act
        var (tileX, tileY) = TileMath.LonLatToTileXY(180, 0, 1);

        // Assert - 180° longitude should map to the rightmost tile
        tileX.Should().Be(1); // Due to wrapping at 180°
    }

    [UnitTest]
    public void LonLatToTileXY_AtMinimumLongitude_ShouldHandleCorrectly()
    {
        // Arrange & Act
        var (tileX, tileY) = TileMath.LonLatToTileXY(-180, 0, 1);

        // Assert - -180° longitude should map to the leftmost tile
        tileX.Should().Be(0);
    }

    [UnitTest]
    public void LonLatToTileXY_AtWebMercatorLimits_ShouldHandleCorrectly()
    {
        // Arrange - Web Mercator latitude limits
        const double maxLat = 85.0511287798;
        const double minLat = -85.0511287798;

        // Act & Assert
        var (_, maxTileY) = TileMath.LonLatToTileXY(0, maxLat, 1);
        var (_, minTileY) = TileMath.LonLatToTileXY(0, minLat, 1);

        maxTileY.Should().Be(0); // North pole maps to top tiles
        minTileY.Should().Be(1); // South pole maps to bottom tiles
    }

    [UnitTest]
    public void TileXYToLonLat_AtTileOrigins_ShouldProduceCorrectResults()
    {
        // Arrange & Act
        var (lon00, lat00) = TileMath.TileXYToLonLat(0, 0, 1);
        var (lon11, lat11) = TileMath.TileXYToLonLat(1, 1, 1);

        // Assert
        lon00.Should().Be(-180);
        lat00.Should().BeApproximately(85.0511, 0.001);
        lon11.Should().Be(0);
        lat11.Should().BeApproximately(0, 0.001);
    }

    [UnitTest]
    public void GetTileBounds_ShouldReturnCorrectBounds()
    {
        // Arrange & Act
        var bounds = TileMath.GetTileBounds(1, 1, 1);

        // Assert
        bounds.MinLon.Should().Be(0);
        bounds.MaxLon.Should().Be(180);
        bounds.MinLat.Should().BeApproximately(-85.0511, 0.001);
        bounds.MaxLat.Should().BeApproximately(0, 0.001);
    }

    [UnitTest]
    public void GetTileBounds_AtZoomZero_ShouldCoverWholeWorld()
    {
        // Arrange & Act
        var bounds = TileMath.GetTileBounds(0, 0, 0);

        // Assert
        bounds.MinLon.Should().Be(-180);
        bounds.MaxLon.Should().Be(180);
        bounds.MinLat.Should().BeApproximately(-85.0511, 0.001);
        bounds.MaxLat.Should().BeApproximately(85.0511, 0.001);
    }

    [UnitTest]
    public void GetTileBounds_AtHighZoom_ShouldHaveSmallBounds()
    {
        // Arrange & Act
        var bounds = TileMath.GetTileBounds(512, 512, 10);

        // Assert - At zoom 10, tiles should be small
        var lonRange = bounds.MaxLon - bounds.MinLon;
        var latRange = bounds.MaxLat - bounds.MinLat;

        lonRange.Should().BeLessThan(1); // Less than 1 degree
        latRange.Should().BeLessThan(1); // Less than 1 degree
    }

    [UnitTest]
    public void GetParentTile_ShouldReturnCorrectParent()
    {
        // Arrange & Act
        var (parentX, parentY) = TileMath.GetParentTile(5, 7, 3);

        // Assert
        parentX.Should().Be(2);
        parentY.Should().Be(3);
    }

    [UnitTest]
    public void GetChildTiles_ShouldReturnFourChildren()
    {
        // Arrange & Act
        var children = TileMath.GetChildTiles(2, 3, 2);

        // Assert
        children.Should().HaveCount(4);
        children.Should().Contain((4, 6));
        children.Should().Contain((4, 7));
        children.Should().Contain((5, 6));
        children.Should().Contain((5, 7));
    }

    [UnitTest]
    public void GetChildTiles_AtMaxZoom_ShouldReturnEmpty()
    {
        // Arrange & Act
        var children = TileMath.GetChildTiles(0, 0, 22); // Max zoom

        // Assert
        children.Should().BeEmpty();
    }

    [UnitTest]
    public void IsValidTile_WithValidCoordinates_ShouldReturnTrue()
    {
        // Arrange & Act & Assert
        TileMath.IsValidTile(0, 0, 0).Should().BeTrue();
        TileMath.IsValidTile(1, 1, 1).Should().BeTrue();
        TileMath.IsValidTile(255, 255, 8).Should().BeTrue();
    }

    [UnitTest]
    public void IsValidTile_WithInvalidCoordinates_ShouldReturnFalse()
    {
        // Arrange & Act & Assert
        TileMath.IsValidTile(-1, 0, 1).Should().BeFalse();
        TileMath.IsValidTile(0, -1, 1).Should().BeFalse();
        TileMath.IsValidTile(2, 0, 1).Should().BeFalse(); // Out of bounds for zoom 1
        TileMath.IsValidTile(0, 2, 1).Should().BeFalse(); // Out of bounds for zoom 1
    }

    [UnitTest]
    public void IsValidTile_WithNegativeZoom_ShouldReturnFalse()
    {
        // Arrange & Act & Assert
        TileMath.IsValidTile(0, 0, -1).Should().BeFalse();
    }

    [UnitTest]
    public void GetTilesInBounds_ShouldReturnCorrectTiles()
    {
        // Arrange
        var bounds = new TileBounds(-1, -1, 1, 1);

        // Act
        var tiles = TileMath.GetTilesInBounds(bounds, 1).ToList();

        // Assert
        tiles.Should().HaveCount(4);
        tiles.Should().Contain((0, 0));
        tiles.Should().Contain((0, 1));
        tiles.Should().Contain((1, 0));
        tiles.Should().Contain((1, 1));
    }

    [UnitTest]
    public void GetTileSize_ShouldReturnCorrectSize()
    {
        // Arrange & Act & Assert
        TileMath.GetTileSize(0).Should().BeApproximately(40075016.686, 1); // Earth circumference
        TileMath.GetTileSize(1).Should().BeApproximately(20037508.343, 1); // Half circumference
    }

    [UnitTest]
    public void GetResolution_ShouldReturnCorrectResolution()
    {
        // Arrange & Act
        var resolution = TileMath.GetResolution(10);

        // Assert
        resolution.Should().BeApproximately(152.87, 0.01); // meters per pixel at zoom 10
    }
}
