// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters;

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

    public static void EnsureGeometryTextSize(string geometryText, string description)
    {
        if (Encoding.UTF8.GetByteCount(geometryText) > FilterParserGuard.MaxGeometryTextBytes)
        {
            throw new ArgumentException(
                $"{description} exceeds the maximum geometry size of {FilterParserGuard.MaxGeometryTextBytes} bytes.");
        }
    }
}
