// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using GeoServicesGeometryConverter = Honua.Protocols.GeoServices.GeoServicesGeometryConverter;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerGeometryHelpers
{
    private static readonly string[] CompositeCoordinatePropertyNames = ["paths", "rings"];

    internal readonly record struct GeometryEnvelope(double XMin, double YMin, double XMax, double YMax, int? Srid);

    internal readonly record struct SamplePoint(double X, double Y, int? Srid);

    // An envelope or point leaves the polygon empty so the caller keeps the envelope clip, and a polygon that cannot be converted fails instead of falling back to that envelope.
    internal static bool TryCreatePolygonClipGeometry(
        string geometryJson,
        string? geometryType,
        out byte[]? clipWkb,
        out string? error)
    {
        clipWkb = null;
        error = null;

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

        bool hasRings;
        using (document)
        {
            var root = document.RootElement;
            hasRings = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("rings", out var rings) &&
                rings.ValueKind == JsonValueKind.Array &&
                rings.GetArrayLength() > 0;
        }

        var polygonRequested = string.Equals(geometryType, "esriGeometryPolygon", StringComparison.OrdinalIgnoreCase);
        if (!hasRings && !polygonRequested)
        {
            return true;
        }

        if (!hasRings)
        {
            error = "geometryType esriGeometryPolygon requires rings.";
            return false;
        }

        GeoServicesGeometry? geometry;
        try
        {
            geometry = JsonSerializer.Deserialize(geometryJson, FeatureServerJsonContext.Default.GeoServicesGeometry);
        }
        catch (JsonException)
        {
            error = "geometry must be valid JSON.";
            return false;
        }

        if (geometry?.Rings is not { Length: > 0 })
        {
            error = "geometryType esriGeometryPolygon requires rings.";
            return false;
        }

        try
        {
            // Drop envelope fields so a polygon that also carries xmin/ymin is not
            // reduced to its bounding rectangle by the shared converter.
            clipWkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(new GeoServicesGeometry
            {
                Rings = geometry.Rings,
                SpatialReference = geometry.SpatialReference,
                HasZ = geometry.HasZ,
                HasM = geometry.HasM
            });
            return true;
        }
        catch (ArgumentException)
        {
            error = "Invalid geometry.";
            return false;
        }
    }

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

    internal static bool TryGetSamplePoints(
        string? geometryJson,
        out IReadOnlyList<SamplePoint> points,
        out string? error)
        => TryGetSamplePoints(geometryJson, null, int.MaxValue, out points, out error);

    internal static bool TryGetSamplePoints(
        string? geometryJson,
        double? sampleDistance,
        int maxPointCount,
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

            var coordinates = sampleDistance is > 0
                ? CollectCoordinatesAtDistance(root, sampleDistance.Value, maxPointCount)
                : CollectCoordinates(root);
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

    private static List<(double X, double Y)> CollectCoordinatesAtDistance(
        JsonElement root,
        double sampleDistance,
        int maxPointCount)
    {
        // sampleDistance applies along each path/ring independently. Multipoints retain
        // their explicit locations and never acquire synthetic links between parts.
        if (root.TryGetProperty("points", out var pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
        {
            var explicitPoints = new List<(double, double)>();
            AppendCoordinatePairs(pointsElement, explicitPoints);
            return explicitPoints.Take(maxPointCount).ToList();
        }

        var result = new List<(double, double)>();
        foreach (var propertyName in CompositeCoordinatePropertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var partElement in parts.EnumerateArray())
            {
                var part = new List<(double X, double Y)>();
                if (partElement.ValueKind == JsonValueKind.Array)
                {
                    AppendCoordinatePairs(partElement, part);
                }

                if (part.Count > 0 && result.Count < maxPointCount)
                {
                    result.Add(part[0]);
                }

                var distanceToNextSample = sampleDistance;
                for (var i = 1; i < part.Count && result.Count < maxPointCount; i++)
                {
                    var start = part[i - 1];
                    var end = part[i];
                    var dx = end.X - start.X;
                    var dy = end.Y - start.Y;
                    var length = Math.Sqrt((dx * dx) + (dy * dy));
                    if (length <= 0)
                    {
                        continue;
                    }

                    var distanceAlongSegment = 0d;
                    while (distanceToNextSample <= length - distanceAlongSegment && result.Count < maxPointCount)
                    {
                        distanceAlongSegment += distanceToNextSample;
                        var fraction = distanceAlongSegment / length;
                        result.Add((start.X + (dx * fraction), start.Y + (dy * fraction)));
                        distanceToNextSample = sampleDistance;
                    }

                    distanceToNextSample -= length - distanceAlongSegment;
                }
            }
        }

        return result;
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
        var propertyIndex = 0;
        while (propertyIndex < CompositeCoordinatePropertyNames.Length)
        {
            if (root.TryGetProperty(CompositeCoordinatePropertyNames[propertyIndex], out var element) &&
                element.ValueKind == JsonValueKind.Array)
            {
                var partIndex = 0;
                while (partIndex < element.GetArrayLength())
                {
                    var part = element[partIndex];
                    if (part.ValueKind == JsonValueKind.Array)
                    {
                        AppendCoordinatePairs(part, result);
                    }

                    partIndex++;
                }
            }

            propertyIndex++;
        }

        return result;
    }

    private static void AppendCoordinatePairs(JsonElement array, List<(double, double)> result)
    {
        var pairIndex = 0;
        while (pairIndex < array.GetArrayLength())
        {
            var pair = array[pairIndex];
            if (pair.ValueKind == JsonValueKind.Array &&
                pair.GetArrayLength() >= 2 &&
                pair[0].ValueKind == JsonValueKind.Number &&
                pair[1].ValueKind == JsonValueKind.Number)
            {
                result.Add((pair[0].GetDouble(), pair[1].GetDouble()));
            }

            pairIndex++;
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
