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
///   <item><c>{"c": [[endX, endY], [interiorX, interiorY]]}</c> — circular arc. <b>Densified.</b></item>
///   <item><c>{"b": [[endX, endY], [c1X, c1Y], [c2X, c2Y]]}</c> — cubic Bézier. <b>Densified.</b></item>
///   <item><c>{"a": [...]}</c> — circular/elliptic arc defined by center, minor/clockwise flags,
///     rotation, semi-major axis, and axis ratio. <b>Densified.</b></item>
/// </list>
/// <para>
/// <b>Storage-linearization limitation:</b> the densified vertices are what gets stored. NTS/WKB
/// cannot represent a true curve, so once a curve is densified it cannot be losslessly re-curved on
/// output. The round-trip this converter guarantees is at the JSON model level
/// (<see cref="Parse"/> → <see cref="Serialize"/>), which proves the curve definition survives a
/// parse+serialize cycle; it does NOT promise curve re-emission from stored linear geometry.
/// </para>
/// </remarks>
public static class CurveGeometryConverter
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
    /// vertices and linearly interpolated by curve parameter for every generated vertex.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a segment object uses an unsupported key or is malformed.
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

        if (segment.TryGetProperty("a", out var elliptic))
        {
            DensifyEllipticArc(elliptic, start, output, out end);
            return;
        }

        throw new ArgumentException(
            "Unknown true-curve segment object. Supported segment types: circular arc ('c'), elliptic arc ('a'), cubic Bézier ('b').");
    }

    /// <summary>
    /// Densifies a circular arc segment <c>{"c": [[endX, endY], [interiorX, interiorY]]}</c>.
    /// Esri's second vertex is a point on the arc, not its center. The circumcircle is reconstructed
    /// from start/interior/end, and the sweep containing the interior point is sampled in bounded
    /// angular steps.
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
                "Circular-arc ('c') segment must be [[endX, endY], [interiorX, interiorY]].");
        }

        var endPoint = ReadVertex(c[0]);
        var interior = ReadVertex(c[1]);
        end = NormalizeEndpoint(start, endPoint);

        var ax = interior[0] - start[0];
        var ay = interior[1] - start[1];
        var bx = endPoint[0] - start[0];
        var by = endPoint[1] - start[1];
        var determinant = 2.0 * ((ax * by) - (ay * bx));
        var scaleSquared = Math.Max(1.0, Math.Max((ax * ax) + (ay * ay), (bx * bx) + (by * by)));

        if (Math.Abs(determinant) <= 1e-12 * scaleSquared)
        {
            AppendDegenerateArc(start, interior, end, output);
            return;
        }

        var aSquared = (ax * ax) + (ay * ay);
        var bSquared = (bx * bx) + (by * by);
        var cx = start[0] + (((by * aSquared) - (ay * bSquared)) / determinant);
        var cy = start[1] + (((ax * bSquared) - (bx * aSquared)) / determinant);
        var radius = Math.Sqrt(
            ((start[0] - cx) * (start[0] - cx)) +
            ((start[1] - cy) * (start[1] - cy)));

        if (!double.IsFinite(radius) || radius <= double.Epsilon)
        {
            AppendDegenerateArc(start, interior, end, output);
            return;
        }

        var startAngle = Math.Atan2(start[1] - cy, start[0] - cx);
        var interiorAngle = Math.Atan2(interior[1] - cy, interior[0] - cx);
        var endAngle = Math.Atan2(endPoint[1] - cy, endPoint[0] - cx);
        var counterClockwiseSweep = NormalizePositive(endAngle - startAngle);
        var counterClockwiseInterior = NormalizePositive(interiorAngle - startAngle);
        var sweep = counterClockwiseInterior <= counterClockwiseSweep + 1e-12
            ? counterClockwiseSweep
            : counterClockwiseSweep - (2.0 * Math.PI);
        var interiorSweep = sweep >= 0
            ? counterClockwiseInterior
            : -NormalizePositive(startAngle - interiorAngle);

        DensifyCircularArcThroughInterior(
            start,
            interior,
            end,
            cx,
            cy,
            radius,
            startAngle,
            interiorSweep,
            sweep,
            output);
    }

    private static void DensifyCircularArcThroughInterior(
        double[] start,
        double[] interior,
        double[] end,
        double cx,
        double cy,
        double radius,
        double startAngle,
        double interiorSweep,
        double totalSweep,
        List<double[]> output)
    {
        var totalMagnitude = Math.Abs(totalSweep);
        if (totalMagnitude <= double.Epsilon)
        {
            output.Add(end);
            return;
        }

        var firstMagnitude = Math.Abs(interiorSweep);
        var secondMagnitude = Math.Max(0.0, totalMagnitude - firstMagnitude);
        var firstSteps = Math.Max(1, (int)Math.Ceiling(firstMagnitude / ArcAngularStep));
        var secondSteps = Math.Max(1, (int)Math.Ceiling(secondMagnitude / ArcAngularStep));
        ScaleStepCounts(ref firstSteps, ref secondSteps);

        for (var i = 1; i <= firstSteps; i++)
        {
            var local = (double)i / firstSteps;
            var global = firstMagnitude * local / totalMagnitude;
            var angle = startAngle + (interiorSweep * local);
            var x = i == firstSteps ? interior[0] : cx + (radius * Math.Cos(angle));
            var y = i == firstSteps ? interior[1] : cy + (radius * Math.Sin(angle));
            output.Add(CreateInterpolatedVertex(x, y, start, end, global));
        }

        var remainingSweep = totalSweep - interiorSweep;
        for (var i = 1; i <= secondSteps; i++)
        {
            var local = (double)i / secondSteps;
            var global = (firstMagnitude + (secondMagnitude * local)) / totalMagnitude;
            if (i == secondSteps)
            {
                output.Add(end);
                continue;
            }

            var angle = startAngle + interiorSweep + (remainingSweep * local);
            output.Add(CreateInterpolatedVertex(
                cx + (radius * Math.Cos(angle)),
                cy + (radius * Math.Sin(angle)),
                start,
                end,
                global));
        }
    }

    private static void ScaleStepCounts(ref int firstSteps, ref int secondSteps)
    {
        var total = firstSteps + secondSteps;
        if (total <= MaxVerticesPerSegment)
        {
            return;
        }

        var firstFraction = (double)firstSteps / total;
        firstSteps = Math.Max(1, (int)Math.Round(MaxVerticesPerSegment * firstFraction));
        secondSteps = Math.Max(1, MaxVerticesPerSegment - firstSteps);
    }

    private static void AppendDegenerateArc(
        double[] start,
        double[] interior,
        double[] end,
        List<double[]> output)
    {
        var firstLength = Math.Sqrt(
            ((interior[0] - start[0]) * (interior[0] - start[0])) +
            ((interior[1] - start[1]) * (interior[1] - start[1])));
        var secondLength = Math.Sqrt(
            ((end[0] - interior[0]) * (end[0] - interior[0])) +
            ((end[1] - interior[1]) * (end[1] - interior[1])));
        var totalLength = firstLength + secondLength;

        if (firstLength > double.Epsilon)
        {
            var parameter = totalLength > double.Epsilon ? firstLength / totalLength : 0.5;
            output.Add(CreateInterpolatedVertex(interior[0], interior[1], start, end, parameter));
        }

        output.Add(end);
    }

    private static void DensifyEllipticArc(
        JsonElement a,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 4)
        {
            throw new ArgumentException(
                "Elliptic-arc ('a') segment must contain an end point, center point, minor flag, and clockwise flag.");
        }

        var endPoint = ReadVertex(a[0]);
        var center = ReadVertex(a[1]);
        end = NormalizeEndpoint(start, endPoint);
        var isMinor = ReadFlag(a[2], "minor");
        var isClockwise = ReadFlag(a[3], "clockwise");

        // Four elements are Esri's center-form circular arc and seven elements carry the
        // complete elliptic-arc definition. Five/six-element probe payloads are accepted as
        // the circular form because they do not contain enough metadata to define an ellipse.
        var rotation = 0.0;
        var semiMajor = Math.Sqrt(
            ((start[0] - center[0]) * (start[0] - center[0])) +
            ((start[1] - center[1]) * (start[1] - center[1])));
        var ratio = 1.0;
        if (a.GetArrayLength() >= 7)
        {
            rotation = a[4].GetDouble();
            semiMajor = a[5].GetDouble();
            ratio = a[6].GetDouble();
        }

        if (!double.IsFinite(rotation) || !double.IsFinite(semiMajor) || semiMajor <= 0 ||
            !double.IsFinite(ratio) || ratio <= 0)
        {
            throw new ArgumentException(
                "Elliptic-arc ('a') rotation, semi-major axis, and axis ratio must be finite positive values.");
        }

        var semiMinor = semiMajor * ratio;
        var startAngle = GetEllipseParameter(start[0], start[1], center, rotation, semiMajor, semiMinor);
        var endAngle = GetEllipseParameter(endPoint[0], endPoint[1], center, rotation, semiMajor, semiMinor);
        var sweep = SelectEllipticSweep(start, endPoint, startAngle, endAngle, isMinor, isClockwise);
        var steps = Math.Clamp(
            (int)Math.Ceiling(Math.Abs(sweep) / ArcAngularStep),
            1,
            MaxVerticesPerSegment);
        var cosRotation = Math.Cos(rotation);
        var sinRotation = Math.Sin(rotation);

        for (var i = 1; i < steps; i++)
        {
            var parameter = (double)i / steps;
            var angle = startAngle + (sweep * parameter);
            var majorComponent = semiMajor * Math.Cos(angle);
            var minorComponent = semiMinor * Math.Sin(angle);
            var x = center[0] + (majorComponent * cosRotation) - (minorComponent * sinRotation);
            var y = center[1] + (majorComponent * sinRotation) + (minorComponent * cosRotation);
            output.Add(CreateInterpolatedVertex(x, y, start, end, parameter));
        }

        output.Add(end);
    }

    private static bool ReadFlag(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value is 0 or 1)
        {
            return value == 1;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        throw new ArgumentException($"Elliptic-arc ('a') {name} flag must be 0, 1, true, or false.");
    }

    private static double GetEllipseParameter(
        double x,
        double y,
        double[] center,
        double rotation,
        double semiMajor,
        double semiMinor)
    {
        var dx = x - center[0];
        var dy = y - center[1];
        var cosRotation = Math.Cos(rotation);
        var sinRotation = Math.Sin(rotation);
        var normalizedMajor = ((dx * cosRotation) + (dy * sinRotation)) / semiMajor;
        var normalizedMinor = ((-dx * sinRotation) + (dy * cosRotation)) / semiMinor;
        return Math.Atan2(normalizedMinor, normalizedMajor);
    }

    private static double SelectEllipticSweep(
        double[] start,
        double[] end,
        double startAngle,
        double endAngle,
        bool isMinor,
        bool isClockwise)
    {
        if (SameXY(start, end))
        {
            return isMinor ? 0.0 : isClockwise ? -2.0 * Math.PI : 2.0 * Math.PI;
        }

        var counterClockwise = NormalizePositive(endAngle - startAngle);
        var clockwise = counterClockwise - (2.0 * Math.PI);
        var directed = isClockwise ? clockwise : counterClockwise;

        // At exactly pi the two arcs have the same length, so both Esri minor/major flags are
        // geometrically valid. Preserve the declared orientation without rejecting either flag.
        if (Math.Abs(Math.Abs(directed) - Math.PI) <= 1e-12)
        {
            return directed;
        }

        // Other valid Esri inputs carry mutually consistent minor and clockwise flags. Keep the
        // declared orientation and let the minor flag serve as a consistency check without
        // changing the endpoint.
        var expectedMinor = Math.Abs(directed) <= Math.PI + 1e-12;
        if (expectedMinor != isMinor)
        {
            throw new ArgumentException(
                "Elliptic-arc ('a') minor and clockwise flags are inconsistent with its endpoints.");
        }

        return directed;
    }

    private static double NormalizePositive(double angle)
    {
        var normalized = angle % (2.0 * Math.PI);
        return normalized < 0 ? normalized + (2.0 * Math.PI) : normalized;
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
            output.Add(CreateInterpolatedVertex(x, y, start, p3, t));
        }

        end = NormalizeEndpoint(start, p3);
        output.Add(end);
    }

    private static double[] NormalizeEndpoint(double[] start, double[] end)
        => CreateInterpolatedVertex(end[0], end[1], start, end, 1.0);

    private static double[] CreateInterpolatedVertex(
        double x,
        double y,
        double[] start,
        double[] end,
        double parameter)
    {
        var length = Math.Max(start.Length, end.Length);
        if (length <= 2)
        {
            return [x, y];
        }

        var vertex = new double[length];
        vertex[0] = x;
        vertex[1] = y;
        for (var ordinate = 2; ordinate < length; ordinate++)
        {
            var hasStart = ordinate < start.Length;
            var hasEnd = ordinate < end.Length;
            vertex[ordinate] = (hasStart, hasEnd) switch
            {
                (true, true) => start[ordinate] + ((end[ordinate] - start[ordinate]) * parameter),
                (true, false) => start[ordinate],
                (false, true) => end[ordinate],
                _ => double.NaN
            };
        }

        return vertex;
    }

    private static bool SameXY(double[] first, double[] second)
        => first[0].Equals(second[0]) && first[1].Equals(second[1]);

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
