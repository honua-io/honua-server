// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Quantization transform derived from an Esri <c>quantizationParameters</c> request.
/// Maps world coordinates onto an integer grid whose cell size is the requested
/// tolerance, anchored at the transform origin.
/// </summary>
internal sealed record QuantizationTransform(
    string OriginPosition,
    double ScaleX,
    double ScaleY,
    double TranslateX,
    double TranslateY)
{
    /// <summary>Esri default origin: integers grow right/down from the extent's upper-left.</summary>
    public const string UpperLeft = "upperLeft";

    /// <summary>Integers grow right/up from the extent's lower-left.</summary>
    public const string LowerLeft = "lowerLeft";

    public long QuantizeX(double x)
        => (long)Math.Round((x - TranslateX) / ScaleX, MidpointRounding.AwayFromZero);

    public long QuantizeY(double y)
        => string.Equals(OriginPosition, LowerLeft, StringComparison.Ordinal)
            ? (long)Math.Round((y - TranslateY) / ScaleY, MidpointRounding.AwayFromZero)
            : (long)Math.Round((TranslateY - y) / ScaleY, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Parses Esri <c>quantizationParameters</c> and applies the resulting transform to
/// GeoServices geometries (integer grid coordinates, delta-encoded per ring/path).
/// </summary>
internal static class FeatureQuantizer
{
    /// <summary>
    /// Parses the <c>quantizationParameters</c> JSON document. Returns <c>false</c> with an
    /// error when the document is malformed or unsupported; sets <paramref name="transform"/>
    /// to <c>null</c> (and returns <c>true</c>) when no quantization was requested.
    /// </summary>
    public static bool TryParse(string? raw, out QuantizationTransform? transform, out string? error)
    {
        transform = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "quantizationParameters must be a valid JSON object.";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "quantizationParameters must be a JSON object.";
            return false;
        }

        // Only the default "view" mode is supported; "edit" mode has different semantics.
        if (root.TryGetProperty("mode", out var modeElement) &&
            modeElement.ValueKind == JsonValueKind.String &&
            !string.Equals(modeElement.GetString(), "view", StringComparison.OrdinalIgnoreCase))
        {
            error = "quantizationParameters mode must be 'view'.";
            return false;
        }

        var originPosition = QuantizationTransform.UpperLeft;
        if (root.TryGetProperty("originPosition", out var originElement) &&
            originElement.ValueKind == JsonValueKind.String)
        {
            var origin = originElement.GetString();
            if (string.Equals(origin, QuantizationTransform.LowerLeft, StringComparison.OrdinalIgnoreCase))
            {
                originPosition = QuantizationTransform.LowerLeft;
            }
            else if (!string.Equals(origin, QuantizationTransform.UpperLeft, StringComparison.OrdinalIgnoreCase))
            {
                error = "quantizationParameters originPosition must be 'upperLeft' or 'lowerLeft'.";
                return false;
            }
        }

        if (!TryGetPositiveDouble(root, "tolerance", out var tolerance))
        {
            error = "quantizationParameters requires a positive numeric tolerance.";
            return false;
        }

        if (!root.TryGetProperty("extent", out var extentElement) ||
            extentElement.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(extentElement, "xmin", out var xmin) ||
            !TryGetDouble(extentElement, "ymin", out var ymin) ||
            !TryGetDouble(extentElement, "ymax", out var ymax))
        {
            error = "quantizationParameters requires an extent with numeric xmin/ymin/xmax/ymax.";
            return false;
        }

        // upperLeft anchors at (xmin, ymax); lowerLeft anchors at (xmin, ymin).
        var translateY = string.Equals(originPosition, QuantizationTransform.LowerLeft, StringComparison.Ordinal) ? ymin : ymax;
        transform = new QuantizationTransform(originPosition, tolerance, tolerance, xmin, translateY);
        return true;
    }

    /// <summary>
    /// Returns a copy of <paramref name="geometry"/> with its coordinates quantized to the
    /// integer grid and delta-encoded per ring/path. Only the X/Y ordinates are quantized;
    /// envelope geometries and null geometries are returned unchanged.
    /// </summary>
    public static GeoServicesGeometry Quantize(GeoServicesGeometry geometry, QuantizationTransform transform)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(transform);

        if (geometry.Rings is { } rings)
        {
            return CloneWith(geometry, rings: QuantizeRingSet(rings, transform));
        }

        if (geometry.Paths is { } paths)
        {
            return CloneWith(geometry, paths: QuantizeRingSet(paths, transform));
        }

        if (geometry.Points is { } points)
        {
            return CloneWith(geometry, points: QuantizeRing(points, transform));
        }

        if (geometry.X is { } x && geometry.Y is { } y)
        {
            return CloneWith(geometry, x: transform.QuantizeX(x), y: transform.QuantizeY(y));
        }

        return geometry;
    }

    private static double[][][] QuantizeRingSet(double[][][] ringSet, QuantizationTransform transform)
    {
        var result = new double[ringSet.Length][][];
        for (var i = 0; i < ringSet.Length; i++)
        {
            result[i] = QuantizeRing(ringSet[i], transform);
        }

        return result;
    }

    private static double[][] QuantizeRing(double[][] ring, QuantizationTransform transform)
    {
        var result = new double[ring.Length][];
        long previousX = 0;
        long previousY = 0;
        for (var i = 0; i < ring.Length; i++)
        {
            var point = ring[i];
            if (point.Length < 2)
            {
                result[i] = point;
                continue;
            }

            var quantizedX = transform.QuantizeX(point[0]);
            var quantizedY = transform.QuantizeY(point[1]);
            var deltaX = i == 0 ? quantizedX : quantizedX - previousX;
            var deltaY = i == 0 ? quantizedY : quantizedY - previousY;
            previousX = quantizedX;
            previousY = quantizedY;

            // Preserve any additional ordinates (Z/M) unchanged after the quantized X/Y.
            var encoded = new double[point.Length];
            encoded[0] = deltaX;
            encoded[1] = deltaY;
            for (var ordinate = 2; ordinate < point.Length; ordinate++)
            {
                encoded[ordinate] = point[ordinate];
            }

            result[i] = encoded;
        }

        return result;
    }

    private static GeoServicesGeometry CloneWith(
        GeoServicesGeometry source,
        double? x = null,
        double? y = null,
        double[][]? points = null,
        double[][][]? paths = null,
        double[][][]? rings = null)
        => new()
        {
            HasZ = source.HasZ,
            HasM = source.HasM,
            X = x ?? (points is null && paths is null && rings is null ? source.X : null),
            Y = y ?? (points is null && paths is null && rings is null ? source.Y : null),
            Z = source.Z,
            M = source.M,
            Xmin = source.Xmin,
            Ymin = source.Ymin,
            Xmax = source.Xmax,
            Ymax = source.Ymax,
            Points = points ?? source.Points,
            Paths = paths ?? source.Paths,
            Rings = rings ?? source.Rings,
            SpatialReference = source.SpatialReference,
        };

    private static bool TryGetPositiveDouble(JsonElement element, string property, out double value)
        => TryGetDouble(element, property, out value) && value > 0;

    private static bool TryGetDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var child))
        {
            return false;
        }

        return child.ValueKind switch
        {
            JsonValueKind.Number when child.TryGetDouble(out value) => true,
            JsonValueKind.String when double.TryParse(child.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) => true,
            _ => false,
        };
    }
}
