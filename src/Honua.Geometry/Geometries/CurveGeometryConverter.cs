// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Geometries;

public static class CurveGeometryConverter
{
    internal const int MaxVerticesPerSegment = 512;

    private const double ArcAngularStep = Math.PI / 60.0;

    internal const int BezierSegments = 32;

    public static double[][] Densify(JsonElement[] part)
        => Densify(part, int.MaxValue);

    public static double[][] Densify(JsonElement[] part, int maxVertices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfNegative(maxVertices);
        cancellationToken.ThrowIfCancellationRequested();

        var output = new VertexBuffer(Math.Min(part.Length, maxVertices), maxVertices, true, cancellationToken);
        ProcessPart(part, output, cancellationToken);
        return output.ToArray();
    }

    public static int CountDensifiedVertices(
        JsonElement[] part,
        int maxVertices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfNegative(maxVertices);
        cancellationToken.ThrowIfCancellationRequested();

        var output = new VertexBuffer(0, maxVertices, false, cancellationToken);
        ProcessPart(part, output, cancellationToken);
        return output.Count;
    }

    private static void ProcessPart(
        JsonElement[] part,
        VertexBuffer output,
        CancellationToken cancellationToken)
    {
        double[]? current = null;

        foreach (var element in part)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }

    private static void DensifySegment(
        JsonElement segment,
        double[] start,
        VertexBuffer output,
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

    private static void DensifyCircularArc(
        JsonElement c,
        double[] start,
        VertexBuffer output,
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
        VertexBuffer output)
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
        VertexBuffer output)
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
        VertexBuffer output,
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

    private static void DensifyCubicBezier(
        JsonElement b,
        double[] start,
        VertexBuffer output,
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
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 2 or > 4)
        {
            throw new ArgumentException("True-curve vertex must contain two to four finite numeric ordinates.");
        }

        var length = element.GetArrayLength();
        var coords = new double[length];
        for (var i = 0; i < length; i++)
        {
            if (element[i].ValueKind != JsonValueKind.Number || !element[i].TryGetDouble(out var coordinate) || !double.IsFinite(coordinate))
            {
                throw new ArgumentException("True-curve vertex must contain two to four finite numeric ordinates.");
            }
            coords[i] = coordinate;
        }

        return coords;
    }

    private sealed class VertexBuffer(
        int capacity,
        int maxVertices,
        bool collect,
        CancellationToken cancellationToken)
    {
        private readonly List<double[]>? _vertices = collect ? new(capacity) : null;

        public int Count { get; private set; }

        public void Add(double[] vertex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Count >= maxVertices)
            {
                throw new ArgumentException($"True-curve densification exceeds the remaining budget of {maxVertices} vertices.");
            }
            if (vertex.Any(static ordinate => !double.IsFinite(ordinate)))
            {
                throw new ArgumentException("True-curve densification produced a non-finite ordinate.");
            }
            Count++;
            _vertices?.Add(vertex);
        }

        public double[][] ToArray() => _vertices?.ToArray()
            ?? throw new InvalidOperationException("This vertex buffer does not collect coordinates.");
    }

}
