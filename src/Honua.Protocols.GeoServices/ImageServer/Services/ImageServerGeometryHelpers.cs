// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Minimal Esri-JSON geometry parsing shared by the ImageServer compute operations
/// (<c>computeHistograms</c>, <c>computeStatisticsHistograms</c>, <c>getSamples</c>).
/// These helpers extract only the pieces the raster pipeline consumes: the bounding
/// envelope used to scope raster selection, the sample points for pixel sampling, and
/// the spatial reference wkid. They intentionally do not build a full geometry model —
/// AOI clipping reuses the shared raster selection / identify path keyed on the envelope
/// and vertices rather than a parallel raster reader.
/// </summary>
internal static class ImageServerGeometryHelpers
{
    /// <summary>Axis-aligned bounding box of an Esri geometry.</summary>
    internal readonly record struct GeometryEnvelope(double XMin, double YMin, double XMax, double YMax, int? Srid);

    /// <summary>A single sample point with its spatial reference.</summary>
    internal readonly record struct SamplePoint(double X, double Y, int? Srid);

    /// <summary>
    /// Computes the bounding envelope of an Esri-JSON geometry. Supports envelope,
    /// point, multipoint, polyline (paths) and polygon (rings) geometry types.
    /// </summary>
    internal static bool TryGetEnvelope(string? geometryJson, out GeometryEnvelope envelope, out string? error)
    {
        envelope = default;
        error = null;

        if (string.IsNullOrWhiteSpace(geometryJson))
        {
            error = "geometry is required.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(geometryJson);
        }
        catch (JsonException)
        {
            error = "geometry must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "geometry must be a JSON object.";
                return false;
            }

            var srid = ReadWkid(root);

            // Envelope form.
            if (TryReadNumber(root, "xmin", out var xmin) &&
                TryReadNumber(root, "ymin", out var ymin) &&
                TryReadNumber(root, "xmax", out var xmax) &&
                TryReadNumber(root, "ymax", out var ymax))
            {
                envelope = Normalize(xmin, ymin, xmax, ymax, srid);
                return true;
            }

            // Point form.
            if (TryReadNumber(root, "x", out var px) && TryReadNumber(root, "y", out var py))
            {
                envelope = new GeometryEnvelope(px, py, px, py, srid);
                return true;
            }

            // Multipoint / polyline / polygon vertex collections.
            var coordinates = CollectCoordinates(root);
            if (coordinates.Count > 0)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (x, y) in coordinates)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }

                envelope = Normalize(minX, minY, maxX, maxY, srid);
                return true;
            }

            error = "geometry must be an envelope, point, multipoint, polyline, or polygon.";
            return false;
        }
    }

    /// <summary>
    /// Extracts the sample points from an Esri-JSON geometry for <c>getSamples</c>.
    /// A point yields one sample; a multipoint, polyline, or polygon yields its vertices.
    /// </summary>
    internal static bool TryGetSamplePoints(
        string? geometryJson,
        out IReadOnlyList<SamplePoint> points,
        out string? error)
    {
        points = Array.Empty<SamplePoint>();
        error = null;

        if (string.IsNullOrWhiteSpace(geometryJson))
        {
            error = "geometry is required.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(geometryJson);
        }
        catch (JsonException)
        {
            error = "geometry must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "geometry must be a JSON object.";
                return false;
            }

            var srid = ReadWkid(root);

            if (TryReadNumber(root, "x", out var px) && TryReadNumber(root, "y", out var py))
            {
                points = new[] { new SamplePoint(px, py, srid) };
                return true;
            }

            var coordinates = CollectCoordinates(root);
            if (coordinates.Count > 0)
            {
                var list = new List<SamplePoint>(coordinates.Count);
                foreach (var (x, y) in coordinates)
                {
                    list.Add(new SamplePoint(x, y, srid));
                }

                points = list;
                return true;
            }

            // Envelope geometries have no discrete vertices to sample; sample the centre.
            if (TryReadNumber(root, "xmin", out var xmin) &&
                TryReadNumber(root, "ymin", out var ymin) &&
                TryReadNumber(root, "xmax", out var xmax) &&
                TryReadNumber(root, "ymax", out var ymax))
            {
                points = new[]
                {
                    new SamplePoint((xmin + xmax) / 2.0, (ymin + ymax) / 2.0, srid),
                };
                return true;
            }

            error = "geometry must be a point, multipoint, polyline, polygon, or envelope.";
            return false;
        }
    }

    private static GeometryEnvelope Normalize(double xmin, double ymin, double xmax, double ymax, int? srid)
        => new(Math.Min(xmin, xmax), Math.Min(ymin, ymax), Math.Max(xmin, xmax), Math.Max(ymin, ymax), srid);

    private static List<(double X, double Y)> CollectCoordinates(JsonElement root)
    {
        var result = new List<(double, double)>();

        // points: [[x,y], ...]
        if (root.TryGetProperty("points", out var pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
        {
            AppendCoordinatePairs(pointsElement, result);
        }

        // paths: [[[x,y], ...], ...] and rings: [[[x,y], ...], ...]
        foreach (var name in new[] { "paths", "rings" })
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in element.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Array)
                    {
                        AppendCoordinatePairs(part, result);
                    }
                }
            }
        }

        return result;
    }

    private static void AppendCoordinatePairs(JsonElement array, List<(double, double)> result)
    {
        foreach (var pair in array.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array &&
                pair.GetArrayLength() >= 2)
            {
                var x = pair[0];
                var y = pair[1];
                if (x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number)
                {
                    result.Add((x.GetDouble(), y.GetDouble()));
                }
            }
        }
    }

    private static bool TryReadNumber(JsonElement element, string property, out double value)
    {
        value = 0;
        if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.TryGetDouble(out value);
        }

        return false;
    }

    private static int? ReadWkid(JsonElement root)
    {
        if (!root.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sr.TryGetProperty("latestWkid", out var latest) &&
            latest.ValueKind == JsonValueKind.Number && latest.TryGetInt32(out var latestWkid))
        {
            return latestWkid;
        }

        if (sr.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number && wkid.TryGetInt32(out var wkidValue))
        {
            return wkidValue;
        }

        return null;
    }

    internal static int? TryParseSrid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        // sr may also be passed as a spatialReference JSON object.
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                return ReadWkid(document.RootElement) is { } wkid ? wkid : ReadWkidDirect(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static int? ReadWkidDirect(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number && wkid.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }
}
