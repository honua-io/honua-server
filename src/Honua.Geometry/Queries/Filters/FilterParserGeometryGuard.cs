// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Geometry-shaped guardrails for filter-parser hostile-input handling.
/// </summary>
/// <remarks>
/// Split out from <see cref="FilterParserGuard"/> when the NetTopologySuite
/// dependency was moved out of <c>Honua.Core</c> and into
/// <c>Honua.Geometry</c>. Numeric / string-length / depth caps remain on
/// <see cref="FilterParserGuard"/> so non-geometry parsers (the CQL2 / OData
/// lexers, the GeoServicesSQL parser) keep using them without dragging NTS
/// into Core.
/// </remarks>
internal static class FilterParserGeometryGuard
{
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
        if (coordinateCount > FilterParserGuard.MaxGeometryVertices)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum geometry complexity of {FilterParserGuard.MaxGeometryVertices} vertices.");
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
        if (Encoding.UTF8.GetByteCount(geometryText) > FilterParserGuard.MaxGeometryTextBytes)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum geometry size of {FilterParserGuard.MaxGeometryTextBytes} bytes.");
        }
    }
}
