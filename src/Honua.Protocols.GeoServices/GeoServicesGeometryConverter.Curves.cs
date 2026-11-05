// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Geometries;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

internal static partial class GeoServicesGeometryConverter
{
    public static bool HasTrueCurves(GeoServicesGeometry geometry)
        => geometry.CurvePaths is { Length: > 0 } || geometry.CurveRings is { Length: > 0 };

    public static GeoServicesGeometry DensifyCurves(GeoServicesGeometry geometry)
        => DensifyCurves(geometry, DefaultMaxCurveVertices);

    public static GeoServicesGeometry DensifyCurves(GeoServicesGeometry geometry, int maxVertices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegative(maxVertices);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = maxVertices;

        if (geometry.CurvePaths is { Length: > 0 } curvePaths)
        {
            var paths = new double[curvePaths.Length][][];
            for (var i = 0; i < curvePaths.Length; i++)
            {
                paths[i] = CurveGeometryConverter.Densify(curvePaths[i], remaining, cancellationToken);
                remaining -= paths[i].Length;
            }

            return CloneWithLinear(geometry, paths: paths, rings: null);
        }

        if (geometry.CurveRings is { Length: > 0 } curveRings)
        {
            var rings = new double[curveRings.Length][][];
            for (var i = 0; i < curveRings.Length; i++)
            {
                rings[i] = CurveGeometryConverter.Densify(curveRings[i], remaining, cancellationToken);
                remaining -= rings[i].Length;
            }

            return CloneWithLinear(geometry, paths: null, rings: rings);
        }

        return geometry;
    }

    private static GeoServicesGeometry CloneWithLinear(
        GeoServicesGeometry source,
        double[][][]? paths,
        double[][][]? rings)
        => new()
        {
            HasZ = source.HasZ,
            HasM = source.HasM,
            Paths = paths,
            Rings = rings,
            SpatialReference = source.SpatialReference,
        };

    public static GeoServicesGeometry ParseCurveGeometry(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry)
            ?? throw new ArgumentException("Invalid true-curve geometry JSON.");
    }

    public static string SerializeCurveGeometry(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
    }
}
