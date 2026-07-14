// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Parses the ImageServer <c>calculateVolume</c> <c>geometries</c> request parameter into the
/// area-of-interest clip geometries the volume integration consumes. Each area of interest is an
/// Esri polygon (rings) or envelope, converted to a WKB clip for the shared raster analytics path
/// (the same clip primitive <c>computeClassStatistics</c> / <c>computeStatisticsHistograms</c>
/// use). See ADR-0064.
/// </summary>
internal static class ImageServerVolumeGeometries
{
    private static readonly WKBWriter WkbWriter = new();

    /// <summary>A parsed area-of-interest: its clip geometry (WKB) and optional spatial reference.</summary>
    internal readonly record struct ParsedAoi(byte[] ClipGeometry, int? ClipSrid);

    /// <summary>
    /// Parses the <c>geometries</c> parameter. The value may be a JSON array of geometry objects
    /// (<c>[ { ... }, { ... } ]</c>) or a wrapper object carrying a <c>geometries</c> array
    /// (<c>{ "geometryType": "...", "geometries": [ ... ] }</c>). <paramref name="geometryType"/>
    /// must be <c>esriGeometryPolygon</c> or <c>esriGeometryEnvelope</c>. Each geometry's optional
    /// <c>spatialReference.wkid</c> wins over <paramref name="defaultSrid"/>.
    /// </summary>
    internal static bool TryParse(
        string? geometriesJson,
        string? geometryType,
        int? defaultSrid,
        out IReadOnlyList<ParsedAoi> areas,
        out string? error)
    {
        areas = Array.Empty<ParsedAoi>();
        error = null;

        if (string.IsNullOrWhiteSpace(geometriesJson))
        {
            error = "geometries parameter is required.";
            return false;
        }

        var normalizedType = geometryType?.ToLowerInvariant();
        if (normalizedType is not null and not "esrigeometrypolygon" and not "esrigeometryenvelope")
        {
            error = "geometryType must be esriGeometryPolygon or esriGeometryEnvelope.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(geometriesJson);
        }
        catch (JsonException)
        {
            error = "geometries must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("geometries", out var wrapped) &&
                     wrapped.ValueKind == JsonValueKind.Array)
            {
                array = wrapped;
            }
            else
            {
                error = "geometries must be a JSON array of geometries (or an object with a geometries array).";
                return false;
            }

            if (array.GetArrayLength() == 0)
            {
                error = "geometries must contain at least one geometry.";
                return false;
            }

            var parsed = new List<ParsedAoi>(array.GetArrayLength());
            var ordinal = 0;
            foreach (var element in array.EnumerateArray())
            {
                ordinal++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = $"geometry {ordinal} must be a JSON object.";
                    return false;
                }

                if (!TryReadGeometry(element, out var geometry, out var geometryError))
                {
                    error = $"geometry {ordinal}: {geometryError}";
                    return false;
                }

                var srid = ReadWkid(element) ?? defaultSrid;
                parsed.Add(new ParsedAoi(WkbWriter.Write(geometry), srid));
            }

            areas = parsed;
            return true;
        }
    }

    private static bool TryReadGeometry(JsonElement geometry, out Geometry result, out string? error)
    {
        result = null!;
        error = null;
        var factory = new GeometryFactory();

        // Envelope form.
        if (TryReadNumber(geometry, "xmin", out var xmin) &&
            TryReadNumber(geometry, "ymin", out var ymin) &&
            TryReadNumber(geometry, "xmax", out var xmax) &&
            TryReadNumber(geometry, "ymax", out var ymax))
        {
            var envelope = new Envelope(Math.Min(xmin, xmax), Math.Max(xmin, xmax), Math.Min(ymin, ymax), Math.Max(ymin, ymax));
            result = factory.ToGeometry(envelope);
            return true;
        }

        // Polygon form (rings). The first ring is the exterior shell; subsequent rings are holes.
        if (geometry.TryGetProperty("rings", out var ringsElement) && ringsElement.ValueKind == JsonValueKind.Array)
        {
            var rings = new List<LinearRing>();
            foreach (var ringElement in ringsElement.EnumerateArray())
            {
                if (!TryReadRing(ringElement, factory, out var ring, out var ringError))
                {
                    error = ringError;
                    return false;
                }

                rings.Add(ring);
            }

            if (rings.Count == 0)
            {
                error = "polygon geometry must contain at least one ring.";
                return false;
            }

            var holes = rings.Count > 1 ? rings.Skip(1).ToArray() : null;
            result = factory.CreatePolygon(rings[0], holes);
            return true;
        }

        error = "geometry must be an Esri polygon (rings) or envelope (xmin/ymin/xmax/ymax).";
        return false;
    }

    private static bool TryReadRing(JsonElement ringElement, GeometryFactory factory, out LinearRing ring, out string? error)
    {
        ring = null!;
        error = null;

        if (ringElement.ValueKind != JsonValueKind.Array)
        {
            error = "each polygon ring must be an array of coordinate pairs.";
            return false;
        }

        var coordinates = new List<Coordinate>(ringElement.GetArrayLength());
        foreach (var pair in ringElement.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2 ||
                pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number)
            {
                error = "each polygon vertex must be a [x, y] number pair.";
                return false;
            }

            coordinates.Add(new Coordinate(pair[0].GetDouble(), pair[1].GetDouble()));
        }

        // Esri rings are closed; defensively close an open ring so NTS accepts it.
        if (coordinates.Count >= 3 && !coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0].Copy());
        }

        if (coordinates.Count < 4)
        {
            error = "each polygon ring must have at least three distinct vertices.";
            return false;
        }

        try
        {
            ring = factory.CreateLinearRing(coordinates.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            error = "polygon ring is not a valid closed ring.";
            return false;
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

    private static int? ReadWkid(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
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
}
