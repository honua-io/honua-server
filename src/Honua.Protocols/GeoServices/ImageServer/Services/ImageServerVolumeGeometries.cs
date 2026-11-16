// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerVolumeGeometries
{
    private static readonly WKBWriter WkbWriter = new();

    internal readonly record struct ParsedAoi(byte[] ClipGeometry, int? ClipSrid);

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

                if (!ImageServerEsriPolygonReader.TryRead(element, "geometry", out var geometry, out var geometryError))
                {
                    error = $"geometry {ordinal}: {geometryError}";
                    return false;
                }

                var srid = ImageServerGeometryHelpers.ReadWkid(element) ?? defaultSrid;
                parsed.Add(new ParsedAoi(WkbWriter.Write(geometry), srid));
            }

            areas = parsed;
            return true;
        }
    }
}
