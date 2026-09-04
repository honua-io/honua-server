// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Geometry.Services;

/// <summary>
/// Densifies the protocol-neutral JSON representation of Esri true-curve path and
/// ring parts into linear coordinate arrays.
/// </summary>
public static class EsriTrueCurveDensifier
{
    private const int MaxVerticesPerSegment = 512;
    private const double ArcAngularStep = Math.PI / 60.0;

    /// <summary>
    /// Number of straight segments used to sample a cubic Bezier curve.
    /// </summary>
    public const int BezierSegments = 32;

    /// <summary>
    /// Densifies one path or ring whose elements are plain coordinate arrays or
    /// Esri curve-segment objects.
    /// </summary>
    /// <param name="part">The ordered vertices and curve segments.</param>
    /// <returns>The equivalent bounded sequence of linear vertices.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a curve segment is malformed or uses an unsupported segment type.
    /// </exception>
    public static double[][] DensifyPart(IReadOnlyList<JsonElement> part)
    {
        ArgumentNullException.ThrowIfNull(part);

        var output = new List<double[]>(part.Count);
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
                            "True-curve segment cannot be the first element of a path/ring; it must follow a start vertex.",
                            nameof(part));
                    }

                    DensifySegment(element, current, output, out current);
                    break;

                default:
                    throw new ArgumentException(
                        $"Invalid true-curve element of kind '{element.ValueKind}'; expected a vertex array or a segment object.",
                        nameof(part));
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
            throw new ArgumentException(
                "Elliptic-arc ('a') true-curve segments are not supported. Supported segment types: circular arc ('c'), cubic Bezier ('b').",
                nameof(segment));
        }

        throw new ArgumentException(
            "Unknown true-curve segment object. Supported segment types: circular arc ('c'), cubic Bezier ('b').",
            nameof(segment));
    }

    private static void DensifyCircularArc(
        JsonElement value,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
        {
            throw new ArgumentException(
                "Circular-arc ('c') segment must be [[endX, endY], [centerX, centerY]].",
                nameof(value));
        }

        var endPoint = ReadVertex(value[0]);
        var center = ReadVertex(value[1]);
        end = endPoint;

        var cx = center[0];
        var cy = center[1];
        var radius = Math.Sqrt(((start[0] - cx) * (start[0] - cx)) +
            ((start[1] - cy) * (start[1] - cy)));

        if (radius <= double.Epsilon)
        {
            output.Add(endPoint);
            return;
        }

        var startAngle = Math.Atan2(start[1] - cy, start[0] - cx);
        var endAngle = Math.Atan2(endPoint[1] - cy, endPoint[0] - cx);
        var sweep = endAngle - startAngle;
        while (sweep <= -Math.PI)
        {
            sweep += 2.0 * Math.PI;
        }

        while (sweep > Math.PI)
        {
            sweep -= 2.0 * Math.PI;
        }

        var steps = Math.Clamp(
            (int)Math.Ceiling(Math.Abs(sweep) / ArcAngularStep),
            1,
            MaxVerticesPerSegment);
        for (var i = 1; i < steps; i++)
        {
            var angle = startAngle + (sweep * i / steps);
            output.Add([cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle))]);
        }

        output.Add(endPoint);
    }

    private static void DensifyCubicBezier(
        JsonElement value,
        double[] start,
        List<double[]> output,
        out double[] end)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 3)
        {
            throw new ArgumentException(
                "Cubic-Bezier ('b') segment must be [[endX, endY], [ctrl1X, ctrl1Y], [ctrl2X, ctrl2Y]].",
                nameof(value));
        }

        var p3 = ReadVertex(value[0]);
        var p1 = ReadVertex(value[1]);
        var p2 = ReadVertex(value[2]);
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

            output.Add([
                (w0 * x0) + (w1 * p1[0]) + (w2 * p2[0]) + (w3 * p3[0]),
                (w0 * y0) + (w1 * p1[1]) + (w2 * p2[1]) + (w3 * p3[1])
            ]);
        }

        output.Add(p3);
    }

    private static double[] ReadVertex(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 2)
            throw new ArgumentException("True-curve vertex must be a numeric array of at least [x, y].", nameof(element));

        var coordinates = new double[element.GetArrayLength()];
        for (var i = 0; i < coordinates.Length; i++)
        {
            coordinates[i] = element[i].GetDouble();
        }

        return coordinates;
    }
}
