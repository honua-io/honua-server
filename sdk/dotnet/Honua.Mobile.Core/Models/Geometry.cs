// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Base class for all geometry types.
/// </summary>
public abstract record Geometry;

/// <summary>
/// Represents a single point geometry.
/// </summary>
public sealed record PointGeometry : Geometry
{
    public double X { get; init; }
    public double Y { get; init; }
    public double? Z { get; init; }
    public double? M { get; init; }

    public static PointGeometry Create(double x, double y, double? z = null, double? m = null)
    {
        return new PointGeometry { X = x, Y = y, Z = z, M = m };
    }
}

/// <summary>
/// Represents a collection of points.
/// </summary>
public sealed record MultiPointGeometry : Geometry
{
    public IReadOnlyList<PointGeometry> Points { get; init; } = Array.Empty<PointGeometry>();

    public static MultiPointGeometry Create(IReadOnlyList<PointGeometry> points)
    {
        return new MultiPointGeometry { Points = points };
    }
}

/// <summary>
/// Represents a coordinate in a path or ring.
/// </summary>
public sealed record Coordinate
{
    public double X { get; init; }
    public double Y { get; init; }
    public double? Z { get; init; }
    public double? M { get; init; }

    public static Coordinate Create(double x, double y, double? z = null, double? m = null)
    {
        return new Coordinate { X = x, Y = y, Z = z, M = m };
    }
}

/// <summary>
/// Represents a sequence of coordinates forming a path or ring.
/// </summary>
public sealed record CoordinateSequence
{
    public IReadOnlyList<Coordinate> Coordinates { get; init; } = Array.Empty<Coordinate>();

    public static CoordinateSequence Create(IReadOnlyList<Coordinate> coordinates)
    {
        return new CoordinateSequence { Coordinates = coordinates };
    }
}

/// <summary>
/// Represents a polyline geometry with one or more paths.
/// </summary>
public sealed record PolylineGeometry : Geometry
{
    public IReadOnlyList<CoordinateSequence> Paths { get; init; } = Array.Empty<CoordinateSequence>();

    public static PolylineGeometry Create(IReadOnlyList<CoordinateSequence> paths)
    {
        return new PolylineGeometry { Paths = paths };
    }
}

/// <summary>
/// Represents a polygon geometry with one or more rings (first is exterior, rest are holes).
/// </summary>
public sealed record PolygonGeometry : Geometry
{
    public IReadOnlyList<CoordinateSequence> Rings { get; init; } = Array.Empty<CoordinateSequence>();

    public static PolygonGeometry Create(IReadOnlyList<CoordinateSequence> rings)
    {
        return new PolygonGeometry { Rings = rings };
    }
}

/// <summary>
/// Represents a collection of polygons.
/// </summary>
public sealed record MultiPolygonGeometry : Geometry
{
    public IReadOnlyList<PolygonGeometry> Polygons { get; init; } = Array.Empty<PolygonGeometry>();

    public static MultiPolygonGeometry Create(IReadOnlyList<PolygonGeometry> polygons)
    {
        return new MultiPolygonGeometry { Polygons = polygons };
    }
}