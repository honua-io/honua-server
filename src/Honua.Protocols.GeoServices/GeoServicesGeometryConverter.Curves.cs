// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Geometries;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

internal static partial class GeoServicesGeometryConverter
{
    /// <summary>
    /// True when the supplied geometry carries a true-curve representation
    /// (<c>curvePaths</c> or <c>curveRings</c>).
    /// </summary>
    public static bool HasTrueCurves(GeoServicesGeometry geometry)
        => geometry.CurvePaths is { Length: > 0 } || geometry.CurveRings is { Length: > 0 };

    /// <summary>
    /// Densifies a geometry's <c>curvePaths</c>/<c>curveRings</c> into linear
    /// <see cref="GeoServicesGeometry.Paths"/>/<see cref="GeoServicesGeometry.Rings"/>, returning a new
    /// geometry the linear pipeline can convert to WKB. Vertex Z/M ordinates are preserved on plain
    /// vertices and linearly interpolated by curve parameter for every generated vertex.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a segment object uses an unsupported key or is malformed.
    /// </exception>
    public static GeoServicesGeometry DensifyCurves(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.CurvePaths is { Length: > 0 } curvePaths)
        {
            var paths = new double[curvePaths.Length][][];
            for (var i = 0; i < curvePaths.Length; i++)
            {
                paths[i] = CurveGeometryConverter.Densify(curvePaths[i]);
            }

            return CloneWithLinear(geometry, paths: paths, rings: null);
        }

        if (geometry.CurveRings is { Length: > 0 } curveRings)
        {
            var rings = new double[curveRings.Length][][];
            for (var i = 0; i < curveRings.Length; i++)
            {
                rings[i] = CurveGeometryConverter.Densify(curveRings[i]);
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

    /// <summary>
    /// Parses true-curve geometry JSON into the GeoServices <see cref="GeoServicesGeometry"/> model
    /// (curve arrays preserved as raw <see cref="JsonElement"/>). The complement of
    /// <see cref="SerializeCurveGeometry"/>; the pair proves the curve definition survives a parse+serialize cycle.
    /// </summary>
    public static GeoServicesGeometry ParseCurveGeometry(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry)
            ?? throw new ArgumentException("Invalid true-curve geometry JSON.");
    }

    /// <summary>
    /// Serializes a curve-bearing geometry model back to GeoServices JSON, echoing the original
    /// <c>curvePaths</c>/<c>curveRings</c> definition (the complement of <see cref="ParseCurveGeometry"/>).
    /// </summary>
    public static string SerializeCurveGeometry(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
    }
}
