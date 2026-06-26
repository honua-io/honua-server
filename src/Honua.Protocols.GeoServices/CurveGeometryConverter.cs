// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Converts Esri "true-curve" geometry JSON (<c>curvePaths</c> / <c>curveRings</c>) to and from a
/// neutral internal curve model, and densifies curve segments into linear vertices so the rest of
/// the pipeline can store and process curves as ordinary linear geometry (#1877).
/// </summary>
/// <remarks>
/// <para>
/// Esri true-curve arrays interleave plain vertices (<c>[x, y(, z)(, m)]</c>) with curve-segment
/// objects keyed by segment type:
/// </para>
/// <list type="bullet">
///   <item><c>{"c": [[endX, endY], [centerX, centerY]]}</c> — circular arc. <b>Densified.</b></item>
///   <item><c>{"b": [[endX, endY], [c1X, c1Y], [c2X, c2Y]]}</c> — cubic Bézier. <b>Densified.</b></item>
///   <item><c>{"a": [...]}</c> — elliptic arc. <b>Not densified</b> (rejected with a clear message);
///     an honest elliptic-arc densifier requires the full Esri parameterization
///     (center, rotation, isMinor, isCounterClockwise, semi-axis ratio) and is out of scope.</item>
/// </list>
/// <para>
/// <b>Storage-linearization limitation:</b> the densified vertices are what gets stored. NTS/WKB
/// cannot represent a true curve, so once a curve is densified it cannot be losslessly re-curved on
/// output. The round-trip this converter guarantees is at the JSON model level
/// (<see cref="Parse"/> → <see cref="Serialize"/>), which proves the curve definition survives a
/// parse+serialize cycle; it does NOT promise curve re-emission from stored linear geometry.
/// </para>
/// </remarks>
internal static class CurveGeometryConverter
{
    /// <summary>
    /// Maximum number of densified vertices generated for a single curve segment. A defensive bound
    /// against pathological inputs (e.g. a near-zero-radius arc requesting a huge sweep).
    /// </summary>
    internal const int MaxVerticesPerSegment = 512;

    /// <summary>
    /// Target maximum angular step (radians) used when densifying a circular arc. ~3 degrees keeps
    /// the chord error small for typical map-scale radii while bounding vertex counts.
    /// </summary>
    private const double ArcAngularStep = Math.PI / 60.0;

    /// <summary>
    /// Number of straight segments a cubic Bézier is sampled into. Fixed sampling keeps the helper
    /// allocation-light and deterministic; 32 segments is visually smooth at map scale.
    /// </summary>
    internal const int BezierSegments = 32;

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
    /// vertices; densified (interpolated) points are emitted as 2D, so a curve carrying Z/M densifies
    /// to a mixed-dimension path whose hasZ/hasM is taken from <paramref name="geometry"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a segment object uses an unsupported key (e.g. elliptic arc <c>a</c>) or is malformed.
    /// </exception>
    public static GeoServicesGeometry Densify(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.CurvePaths is { Length: > 0 } curvePaths)
        {
            var paths = new double[curvePaths.Length][][];
            for (var i = 0; i < curvePaths.Length; i++)
            {
                paths[i] = DensifyPart(curvePaths[i]);
            }

            return CloneWithLinear(geometry, paths: paths, rings: null);
        }

        if (geometry.CurveRings is { Length: > 0 } curveRings)
        {
            var rings = new double[curveRings.Length][][];
            for (var i = 0; i < curveRings.Length; i++)
            {
                rings[i] = DensifyPart(curveRings[i]);
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
    /// Densifies a single path/ring (array of vertices and/or curve segments) into a flat list of
    /// linear vertices. The "current position" walks forward as segments are consumed, so each curve
    /// segment is densified from the previous vertex to its declared end point.
    /// </summary>
    private static double[][] DensifyPart(JsonElement[] part)
    {
        var output = new List<double[]>(part.Length);
        double[]? current = null;

        foreach (var element in part)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    var vertex = ReadVertex(element);
                    output.Add(vertex);
                    current = vertex;
                    break;

                case JsonValueKind.Object:
                    if (current is null)
                    {
                        throw new ArgumentException(
                            "True-curve segment cannot be the first element of a path/ring; it must follow a start vertex.");
                    }

                    DensifySegment(element, current, output, out current);
                    break;

                default:
                    throw new ArgumentException(
                        $"Invalid true-curve element of kind '{element.ValueKind}'; expected a vertex array or a segment object.");
            }
        }

        return output.ToArray();
    }

    private static void DensifySegment(
        JsonElement segment,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (segment.TryGetProperty("c", out var circular))
        {
            DensifyCircularArc(circular, start, output, out end);
            return;
        }

        if (segment.TryGetProperty("b", out var bezier))
        {
            DensifyCubicBezier(bezier, start, output, out end);
            return;
        }

        if (segment.TryGetProperty("a", out _))
        {
            // Elliptic arcs ("a") carry a richer parameterization (center, minor/major flags,
            // rotation, semi-axis ratio) that we deliberately do not approximate. Rejecting is the
            // honest behavior rather than silently producing a wrong shape.
            throw new ArgumentException(
                "Elliptic-arc ('a') true-curve segments are not supported. Supported segment types: circular arc ('c'), cubic Bézier ('b').");
        }

        throw new ArgumentException(
            "Unknown true-curve segment object. Supported segment types: circular arc ('c'), cubic Bézier ('b').");
    }

    /// <summary>
    /// Densifies a circular arc segment <c>{"c": [[endX, endY], [centerX, centerY]]}</c>. The arc is
    /// reconstructed from the start vertex, the declared end vertex, and the center; the swept angle
    /// is walked from the start angle to the end angle in steps bounded by <see cref="ArcAngularStep"/>.
    /// The Esri convention is that <c>c</c> sweeps from the previous vertex to the end vertex along the
    /// shorter direction implied by the center; we follow the signed delta and normalize it to the
    /// shortest sweep.
    /// </summary>
    private static void DensifyCircularArc(
        JsonElement c,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2)
        {
            throw new ArgumentException(
                "Circular-arc ('c') segment must be [[endX, endY], [centerX, centerY]].");
        }

        var endPoint = ReadVertex(c[0]);
        var center = ReadVertex(c[1]);
        end = endPoint;

        var cx = center[0];
        var cy = center[1];
        var radius = Math.Sqrt(((start[0] - cx) * (start[0] - cx)) + ((start[1] - cy) * (start[1] - cy)));

        if (radius <= double.Epsilon)
        {
            // Degenerate radius: emit only the end vertex.
            output.Add(endPoint);
            return;
        }

        var startAngle = Math.Atan2(start[1] - cy, start[0] - cx);
        var endAngle = Math.Atan2(endPoint[1] - cy, endPoint[0] - cx);

        var sweep = endAngle - startAngle;
        // Normalize to (-PI, PI] so we walk the shorter arc; a full-circle arc (start == end) keeps
        // a zero sweep and emits just the end vertex, which matches Esri's degenerate handling.
        while (sweep <= -Math.PI)
        {
            sweep += 2.0 * Math.PI;
        }

        while (sweep > Math.PI)
        {
            sweep -= 2.0 * Math.PI;
        }

        var steps = (int)Math.Ceiling(Math.Abs(sweep) / ArcAngularStep);
        steps = Math.Clamp(steps, 1, MaxVerticesPerSegment);

        // Emit intermediate vertices (exclusive of the start which is already in the output, and the
        // end which we append exactly to avoid floating-point drift away from the declared endpoint).
        for (var i = 1; i < steps; i++)
        {
            var angle = startAngle + (sweep * i / steps);
            output.Add([cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle))]);
        }

        output.Add(endPoint);
    }

    /// <summary>
    /// Densifies a cubic Bézier segment <c>{"b": [[endX, endY], [c1X, c1Y], [c2X, c2Y]]}</c> using
    /// parametric sampling of the De Casteljau cubic from the start vertex (P0) through the two
    /// control points (P1, P2) to the end vertex (P3). Emits <see cref="BezierSegments"/> straight
    /// chords (bounded by <see cref="MaxVerticesPerSegment"/>).
    /// </summary>
    private static void DensifyCubicBezier(
        JsonElement b,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() < 3)
        {
            throw new ArgumentException(
                "Cubic-Bézier ('b') segment must be [[endX, endY], [ctrl1X, ctrl1Y], [ctrl2X, ctrl2Y]].");
        }

        var p3 = ReadVertex(b[0]);
        var p1 = ReadVertex(b[1]);
        var p2 = ReadVertex(b[2]);
        end = p3;

        var x0 = start[0];
        var y0 = start[1];

        var steps = Math.Clamp(BezierSegments, 1, MaxVerticesPerSegment);
        for (var i = 1; i < steps; i++)
        {
            var t = (double)i / steps;
            var u = 1.0 - t;
            var w0 = u * u * u;
            var w1 = 3.0 * u * u * t;
            var w2 = 3.0 * u * t * t;
            var w3 = t * t * t;

            var x = (w0 * x0) + (w1 * p1[0]) + (w2 * p2[0]) + (w3 * p3[0]);
            var y = (w0 * y0) + (w1 * p1[1]) + (w2 * p2[1]) + (w3 * p3[1]);
            output.Add([x, y]);
        }

        output.Add(p3);
    }

    private static double[] ReadVertex(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 2)
        {
            throw new ArgumentException("True-curve vertex must be a numeric array of at least [x, y].");
        }

        var length = element.GetArrayLength();
        var coords = new double[length];
        for (var i = 0; i < length; i++)
        {
            coords[i] = element[i].GetDouble();
        }

        return coords;
    }

    /// <summary>
    /// Parses true-curve geometry JSON into the neutral <see cref="GeoServicesGeometry"/> model
    /// (curve arrays preserved as raw <see cref="JsonElement"/>). The complement of
    /// <see cref="Serialize"/>; the pair proves the curve definition survives a parse+serialize cycle.
    /// </summary>
    public static GeoServicesGeometry Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry)
            ?? throw new ArgumentException("Invalid true-curve geometry JSON.");
    }

    /// <summary>
    /// Serializes a curve-bearing geometry model back to GeoServices JSON, echoing the original
    /// <c>curvePaths</c>/<c>curveRings</c> definition (the complement of <see cref="Parse"/>).
    /// </summary>
    public static string Serialize(GeoServicesGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
    }
}
