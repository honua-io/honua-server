// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Geometries;

namespace Honua.Core.Features.FileImport.Services;

public enum ImportGeometrySizeStatus
{
    WithinLimits = 0,

    TooManyVertices = 1,

    TooManyRings = 2,
}

public readonly record struct ImportGeometrySizeResult(ImportGeometrySizeStatus Status, string? Message)
{
    public bool IsWithinLimits => Status == ImportGeometrySizeStatus.WithinLimits;
}

public static class ImportGeometrySizeGuard
{
    public static ImportGeometrySizeResult Check(NtsGeometry? geometry, int maxVertices, int maxRings)
    {
        if (geometry is null)
        {
            return new ImportGeometrySizeResult(ImportGeometrySizeStatus.WithinLimits, null);
        }

        if (maxVertices > 0)
        {
            // NumPoints walks the coordinate sequences' counts without copying coordinates.
            var vertexCount = geometry.NumPoints;
            if (vertexCount > maxVertices)
            {
                return new ImportGeometrySizeResult(
                    ImportGeometrySizeStatus.TooManyVertices,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Geometry vertex count ({0:N0}) exceeds the import limit ({1:N0}). "
                        + "Explode multipart features (e.g. ogr2ogr -explodecollections) or simplify the geometry before importing.",
                        vertexCount,
                        maxVertices));
            }
        }

        if (maxRings > 0)
        {
            var ringCount = CountRings(geometry);
            if (ringCount > maxRings)
            {
                return new ImportGeometrySizeResult(
                    ImportGeometrySizeStatus.TooManyRings,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Geometry ring count ({0:N0}) exceeds the import limit ({1:N0}). "
                        + "Explode multipart features or simplify the geometry before importing.",
                        ringCount,
                        maxRings));
            }
        }

        return new ImportGeometrySizeResult(ImportGeometrySizeStatus.WithinLimits, null);
    }

    public static int CountRings(NtsGeometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => SumRings(multiPolygon),
            GeometryCollection collection => SumRings(collection),
            _ => 0,
        };
    }

    private static int SumRings(GeometryCollection collection)
    {
        var total = 0;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            total += CountRings(collection.GetGeometryN(i));
        }

        return total;
    }
}
