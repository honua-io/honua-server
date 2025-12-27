// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Honua.Core.Features.Tiles;
using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.Core.Tests.Features.Tiles;

/// <summary>
/// Property-based tests for tile math operations ensuring mathematical correctness.
/// </summary>
public class TileMathPropertyTests
{
    private const double WebMercatorExtent = 20037508.342789244;
    private const double ExtentTolerance = 1e-6;

    /// <summary>
    /// Validates that tile bounds stay within the Web Mercator extent.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ValidTileCoordinateArbs) })]
    public bool TileBoundsStayWithinExtent(TileCoordinate tile)
    {
        var bounds = TileMath.GetTileBounds(tile.X, tile.Y, tile.Z);

        bounds.XMin.Should().BeGreaterThanOrEqualTo(-WebMercatorExtent - ExtentTolerance);
        bounds.XMax.Should().BeLessThanOrEqualTo(WebMercatorExtent + ExtentTolerance);
        bounds.YMin.Should().BeGreaterThanOrEqualTo(-WebMercatorExtent - ExtentTolerance);
        bounds.YMax.Should().BeLessThanOrEqualTo(WebMercatorExtent + ExtentTolerance);

        bounds.XMin.Should().BeLessThan(bounds.XMax);
        bounds.YMin.Should().BeLessThan(bounds.YMax);

        var width = bounds.XMax - bounds.XMin;
        var height = bounds.YMax - bounds.YMin;
        width.Should().BeApproximately(height, 1e-7);

        return true;
    }

    /// <summary>
    /// Validates that adjacent tiles share boundaries.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ValidTileCoordinateArbs) })]
    public bool AdjacentTilesTouch(TileCoordinate tile)
    {
        var maxTile = 1 << tile.Z;
        if (tile.X >= maxTile - 1 || tile.Y >= maxTile - 1)
            return true;

        var currentBounds = TileMath.GetTileBounds(tile.X, tile.Y, tile.Z);
        var rightBounds = TileMath.GetTileBounds(tile.X + 1, tile.Y, tile.Z);
        var bottomBounds = TileMath.GetTileBounds(tile.X, tile.Y + 1, tile.Z);

        currentBounds.XMax.Should().BeApproximately(rightBounds.XMin, 1e-7);
        currentBounds.YMin.Should().BeApproximately(bottomBounds.YMax, 1e-7);

        return true;
    }

    /// <summary>
    /// Validates that valid tile coordinates are accepted.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ValidTileCoordinateArbs) })]
    public bool ValidateTileCoordinatesAcceptsValidValues(TileCoordinate tile)
    {
        TileMath.ValidateTileCoordinates(tile.X, tile.Y, tile.Z).Should().BeTrue();
        return true;
    }

    /// <summary>
    /// Validates that invalid tile coordinates are rejected.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(InvalidTileCoordinateArbs) })]
    public bool ValidateTileCoordinatesRejectsInvalidValues(TileCoordinate tile)
    {
        TileMath.ValidateTileCoordinates(tile.X, tile.Y, tile.Z).Should().BeFalse();
        return true;
    }

    /// <summary>
    /// Validates that simplification tolerance does not increase with zoom.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ZoomLevelArbs) })]
    public bool SimplificationToleranceIsNonIncreasing(int zoomA, int zoomB)
    {
        var lower = Math.Min(zoomA, zoomB);
        var higher = Math.Max(zoomA, zoomB);

        var lowerTolerance = TileMath.GetSimplificationTolerance(lower);
        var higherTolerance = TileMath.GetSimplificationTolerance(higher);

        lowerTolerance.Should().BeGreaterThanOrEqualTo(higherTolerance);
        return true;
    }

    public readonly record struct TileCoordinate(int X, int Y, int Z);

    internal static class ValidTileCoordinateArbs
    {
        public static Arbitrary<TileCoordinate> TileCoordinate() =>
            Arb.From(
                from z in Gen.Choose(0, 22)
                let maxTile = 1 << z
                from x in Gen.Choose(0, maxTile - 1)
                from y in Gen.Choose(0, maxTile - 1)
                select new TileCoordinate(x, y, z));
    }

    internal static class InvalidTileCoordinateArbs
    {
        public static Arbitrary<TileCoordinate> TileCoordinate() =>
            Arb.From(Gen.OneOf(InvalidZoom(), InvalidX(), InvalidY()));

        private static Gen<TileCoordinate> InvalidZoom() =>
            Gen.OneOf(
                Gen.Choose(-10, -1).Select(z => new TileCoordinate(0, 0, z)),
                Gen.Choose(23, 30).Select(z => new TileCoordinate(0, 0, z)));

        private static Gen<TileCoordinate> InvalidX() =>
            from z in Gen.Choose(0, 22)
            let maxTile = 1 << z
            from x in Gen.OneOf(Gen.Constant(-1), Gen.Constant(maxTile))
            from y in Gen.Choose(0, maxTile - 1)
            select new TileCoordinate(x, y, z);

        private static Gen<TileCoordinate> InvalidY() =>
            from z in Gen.Choose(0, 22)
            let maxTile = 1 << z
            from x in Gen.Choose(0, maxTile - 1)
            from y in Gen.OneOf(Gen.Constant(-1), Gen.Constant(maxTile))
            select new TileCoordinate(x, y, z);
    }

    internal static class ZoomLevelArbs
    {
        public static Arbitrary<int> ZoomLevel() => Arb.From(Gen.Choose(0, 22));
    }
}
