// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geometry.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Converts Esri true-curve geometry JSON to and from the GeoServices model and
/// delegates protocol-neutral curve densification to the shared geometry pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Esri true-curve arrays interleave plain vertices with circular-arc (<c>c</c>),
/// cubic-Bezier (<c>b</c>), or elliptic-arc (<c>a</c>) segment objects. Circular and
/// cubic-Bezier segments are densified; elliptic arcs are rejected with a clear error.
/// </para>
/// <para>
/// The densified vertices are what gets stored. NTS/WKB cannot represent a true curve,
/// so stored linear geometry cannot later be losslessly re-curved. The JSON model-level
/// <see cref="Parse"/> and <see cref="Serialize"/> pair preserves the original curve definition.
/// </para>
/// </remarks>
public static class CurveGeometryConverter
{
    /// <summary>
    /// Number of straight segments used to sample a cubic Bezier curve.
    /// </summary>
    internal const int BezierSegments = EsriTrueCurveDensifier.BezierSegments;

    /// <summary>
    /// Returns whether the supplied geometry carries <c>curvePaths</c> or <c>curveRings</c>.
    /// </summary>
    /// <param name="geometry">The GeoServices geometry.</param>
    /// <returns><see langword="true"/> when true-curve members are present.</returns>
    public static bool HasTrueCurves(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry.CurvePaths is { Length: > 0 } || geometry.CurveRings is { Length: > 0 };
    }

    /// <summary>
    /// Densifies a geometry's <c>curvePaths</c> or <c>curveRings</c> into linear paths or rings.
    /// </summary>
    /// <param name="geometry">The curve-bearing GeoServices geometry.</param>
    /// <returns>A new linear geometry, or the original geometry when it has no true curves.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a segment is malformed or uses an unsupported segment type.
    /// </exception>
    public static GeoServicesGeometry Densify(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.CurvePaths is { Length: > 0 } curvePaths)
        {
            var paths = new double[curvePaths.Length][][];
            for (var i = 0; i < curvePaths.Length; i++)
            {
                paths[i] = EsriTrueCurveDensifier.DensifyPart(curvePaths[i]);
            }

            return CloneWithLinear(geometry, paths: paths, rings: null);
        }

        if (geometry.CurveRings is { Length: > 0 } curveRings)
        {
            var rings = new double[curveRings.Length][][];
            for (var i = 0; i < curveRings.Length; i++)
            {
                rings[i] = EsriTrueCurveDensifier.DensifyPart(curveRings[i]);
            }

            return CloneWithLinear(geometry, paths: null, rings: rings);
        }

        return geometry;
    }

    /// <summary>
    /// Parses true-curve geometry JSON into the GeoServices geometry model.
    /// </summary>
    /// <param name="json">The Esri geometry JSON.</param>
    /// <returns>The parsed geometry.</returns>
    public static GeoServicesGeometry Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry)
            ?? throw new ArgumentException("Invalid true-curve geometry JSON.", nameof(json));
    }

    /// <summary>
    /// Serializes a curve-bearing geometry model to GeoServices JSON.
    /// </summary>
    /// <param name="geometry">The geometry to serialize.</param>
    /// <returns>The GeoServices JSON representation.</returns>
    public static string Serialize(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
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
}
