// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FsCheck;
using FsCheck.Fluent;
using NetTopologySuite.Geometries;

using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.TestKit.PropertyBased;

/// <summary>
/// Property-based test generators for geometric data.
/// Ensures comprehensive testing of spatial operations with valid and edge case geometries.
/// </summary>
public static class GeometryGenerators
{
    private const int CoordinateScale = 1000;

    /// <summary>
    /// Generates valid WGS84 coordinates within reasonable bounds.
    /// </summary>
    public static Arbitrary<Coordinate> ValidCoordinate() =>
        Arb.From(
            from lon in Gen.Choose(-180 * CoordinateScale, 180 * CoordinateScale).Select(ToDegrees)
            from lat in Gen.Choose(-90 * CoordinateScale, 90 * CoordinateScale).Select(ToDegrees)
            select new Coordinate(lon, lat));

    /// <summary>
    /// Generates coordinates near boundaries to test edge cases.
    /// </summary>
    public static Arbitrary<Coordinate> BoundaryCoordinate() =>
        Arb.From(
            Gen.OneOf(
                // Poles
                Gen.Constant(new Coordinate(0, 90)),
                Gen.Constant(new Coordinate(0, -90)),
                // Antimeridian
                Gen.Constant(new Coordinate(180, 0)),
                Gen.Constant(new Coordinate(-180, 0)),
                // Near boundaries with small variations
                from lon in Gen.Choose(179900, 180000).Select(ToDegrees)
                from lat in Gen.Choose(89900, 90000).Select(ToDegrees)
                select new Coordinate(lon, lat)));

    /// <summary>
    /// Generates invalid coordinates to test error handling.
    /// </summary>
    public static Arbitrary<Coordinate> InvalidCoordinate() =>
        Arb.From(
            Gen.OneOf(
                Gen.Constant(new Coordinate(double.NaN, 0)),
                Gen.Constant(new Coordinate(0, double.NaN)),
                Gen.Constant(new Coordinate(double.PositiveInfinity, 0)),
                Gen.Constant(new Coordinate(0, double.NegativeInfinity)),
                Gen.Constant(new Coordinate(181, 0)),
                Gen.Constant(new Coordinate(0, 91))));

    /// <summary>
    /// Generates simple polygon geometries for testing.
    /// </summary>
    public static Arbitrary<Polygon> SimplePolygon() =>
        Arb.From(
            from centerX in Gen.Choose(-179 * CoordinateScale, 179 * CoordinateScale).Select(ToDegrees)
            from centerY in Gen.Choose(-89 * CoordinateScale, 89 * CoordinateScale).Select(ToDegrees)
            from size in Gen.Choose(100, 1000).Select(ToDegrees)
            let coords = new[]
            {
                new Coordinate(centerX - size, centerY - size),
                new Coordinate(centerX + size, centerY - size),
                new Coordinate(centerX + size, centerY + size),
                new Coordinate(centerX - size, centerY + size),
                new Coordinate(centerX - size, centerY - size) // Close the ring
            }
            select GeometryHelper.CreatePolygon(coords));

    /// <summary>
    /// Generates bounding box coordinates for spatial queries.
    /// </summary>
    public static Arbitrary<(double MinX, double MinY, double MaxX, double MaxY)> BoundingBox() =>
        Arb.From(
            from minXInt in Gen.Choose(-180 * CoordinateScale, 179 * CoordinateScale)
            from minYInt in Gen.Choose(-90 * CoordinateScale, 89 * CoordinateScale)
            from widthInt in Gen.Choose(100, 180 * CoordinateScale - minXInt)
            from heightInt in Gen.Choose(100, 90 * CoordinateScale - minYInt)
            let minX = ToDegrees(minXInt)
            let minY = ToDegrees(minYInt)
            select (minX, minY, ToDegrees(minXInt + widthInt), ToDegrees(minYInt + heightInt)));

    /// <summary>
    /// Generates malformed bounding boxes for error testing.
    /// </summary>
    public static Arbitrary<(double MinX, double MinY, double MaxX, double MaxY)> InvalidBoundingBox() =>
        Arb.From(
            Gen.OneOf(
                // Inverted coordinates
                from minXInt in Gen.Choose(-179900, 180000)
                from minYInt in Gen.Choose(-89900, 90000)
                from maxXInt in Gen.Choose(-180 * CoordinateScale, minXInt - 100)
                from maxYInt in Gen.Choose(-90 * CoordinateScale, minYInt - 100)
                select (ToDegrees(minXInt), ToDegrees(minYInt), ToDegrees(maxXInt), ToDegrees(maxYInt)),
                // Out of bounds
                Gen.Constant((-181.0, -91.0, 181.0, 91.0)),
                // NaN values
                Gen.Constant((double.NaN, 0.0, 1.0, 1.0))));

    private static double ToDegrees(int value) => value / (double)CoordinateScale;
}

/// <summary>
/// Helper class for creating geometric objects safely.
/// </summary>
internal static class GeometryHelper
{
    private static readonly GeometryFactory _factory = new(new PrecisionModel(), 4326);

    public static Polygon CreatePolygon(Coordinate[] coordinates)
    {
        var shell = _factory.CreateLinearRing(coordinates);
        return _factory.CreatePolygon(shell);
    }
}
