// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Shared guardrails for filter-parser hostile-input handling.
/// </summary>
internal static class FilterParserGuard
{
    internal const int MaxExpressionDepth = FilterExpressionNormalizer.MaxExpressionDepth;
    private const int MaxGeometryTextBytes = 5 * 1024 * 1024;
    private const int MaxGeometryVertices = 50_000;

    // Caps applied to un-structured parser input so that a single oversized token
    // cannot pin down the heap or slow down comparison loops.
    internal const int MaxStringLiteralLength = 64 * 1024;       // 64 KiB per literal
    internal const int MaxIdentifierLength = 256;                // conservative for all backends
    internal const int MaxInListSize = 10_000;                   // 10k elements per IN(...) list

    public static void EnsureExpressionDepth(int depth)
    {
        if (depth > MaxExpressionDepth)
        {
            throw new ArgumentException($"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
        }
    }

    // Rejects NaN and ±Infinity from numeric literals. `double.TryParse(... Float ...)`
    // silently returns Infinity for exponent overflow (e.g. "1e999") and accepts the
    // literals "NaN"/"Infinity" by default on most culture settings — both of which
    // produce nonsense downstream when used in comparisons or geometry coords.
    public static void EnsureFiniteNumber(double value, string description)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                $"{description} must be a finite number (received {value}).");
        }
    }

    public static void EnsureStringLiteralLength(int length, string description)
    {
        if (length > MaxStringLiteralLength)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum string literal length of {MaxStringLiteralLength} characters.");
        }
    }

    public static void EnsureIdentifierLength(int length, string description)
    {
        if (length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum identifier length of {MaxIdentifierLength} characters.");
        }
    }

    public static void EnsureInListSize(int count, string description)
    {
        if (count > MaxInListSize)
        {
            throw new ArgumentException(
                $"{description} IN-list exceeds the maximum of {MaxInListSize} values.");
        }
    }

    public static Geometry ParseGeoJsonGeometry(string geoJson, string description, int? srid = null)
    {
        EnsureGeometryTextSize(geoJson, description);
        var geometry = new GeoJsonReader().Read<Geometry>(geoJson)
            ?? throw new ArgumentException($"{description} could not be parsed.");
        ApplyGeometryGuards(geometry, description, srid);
        return geometry;
    }

    public static Geometry ParseWktGeometry(string wkt, string description, int? srid = null)
    {
        EnsureGeometryTextSize(wkt, description);
        var geometry = new WKTReader().Read(wkt)
            ?? throw new ArgumentException($"{description} could not be parsed.");
        ApplyGeometryGuards(geometry, description, srid);
        return geometry;
    }

    public static void EnsureCoordinateCount(int coordinateCount, string description)
    {
        if (coordinateCount > MaxGeometryVertices)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum geometry complexity of {MaxGeometryVertices} vertices.");
        }
    }

    private static void ApplyGeometryGuards(Geometry geometry, string description, int? srid)
    {
        if (srid.HasValue && srid.Value > 0)
        {
            geometry.SRID = srid.Value;
        }

        if (!geometry.IsEmpty)
        {
            EnsureCoordinateCount(geometry.NumPoints, description);
        }
    }

    private static void EnsureGeometryTextSize(string geometryText, string description)
    {
        if (Encoding.UTF8.GetByteCount(geometryText) > MaxGeometryTextBytes)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum geometry size of {MaxGeometryTextBytes} bytes.");
        }
    }
}
