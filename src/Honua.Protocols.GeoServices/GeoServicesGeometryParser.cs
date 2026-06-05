// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Parsing;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Parses GeoServices geometry parameters from query strings into structured geometry objects.
/// </summary>
internal static class GeoServicesGeometryParser
{
    private static readonly char[] _coordinateSeparators = { ',', ' ' };

    // The Esri geometryType enum (esriGeometryType). An unknown value must be rejected
    // rather than silently coerced to an envelope based on coordinate count (#1462).
    private static readonly FrozenSet<string> _knownGeometryTypes = new[]
    {
        "esrigeometrypoint",
        "esrigeometrymultipoint",
        "esrigeometrypolyline",
        "esrigeometrypolygon",
        "esrigeometryenvelope"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static bool TryParseGeoServicesGeometry(
        string? geometryText,
        string? geometryType,
        out GeoServicesGeometry? geometry,
        out string? error)
    {
        geometry = null;
        error = null;

        // Validate geometryType against the Esri esriGeometryType enum up-front. An unknown
        // value (e.g. esriGeometryBogus) must produce a 400 instead of being silently coerced
        // to an envelope by coordinate count (#1462).
        if (!string.IsNullOrWhiteSpace(geometryType) &&
            !_knownGeometryTypes.Contains(geometryType.Trim()))
        {
            error = $"Unsupported geometryType '{geometryType.Trim()}'. Supported values: " +
                "esriGeometryPoint, esriGeometryMultipoint, esriGeometryPolyline, " +
                "esriGeometryPolygon, esriGeometryEnvelope.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(geometryText))
        {
            return true;
        }

        var trimmed = geometryText.Trim();
        if (trimmed.StartsWith('{'))
        {
            if (TryDeserializeGeometry(trimmed, out geometry, out error))
            {
                return true;
            }

            if (trimmed.Contains('\'') && !trimmed.Contains('"'))
            {
                var normalized = trimmed.Replace('\'', '"');
                if (TryDeserializeGeometry(normalized, out geometry, out error))
                {
                    return true;
                }
            }

            error = "Invalid geometry JSON.";
            return false;
        }

        Span<double> coordinates = stackalloc double[4];
        if (!TryParseCoordinateList(trimmed.AsSpan(), coordinates, out var coordinateCount, out error))
        {
            return false;
        }

        var normalizedType = geometryType?.Trim().ToLowerInvariant();
        if (normalizedType == "esrigeometryenvelope" || coordinateCount == 4)
        {
            geometry = new GeoServicesGeometry
            {
                Xmin = coordinates[0],
                Ymin = coordinates[1],
                Xmax = coordinates[2],
                Ymax = coordinates[3]
            };
            return true;
        }

        if (normalizedType == "esrigeometrypoint" || coordinateCount == 2)
        {
            geometry = new GeoServicesGeometry
            {
                X = coordinates[0],
                Y = coordinates[1]
            };
            return true;
        }

        error = "Geometry coordinate list must contain 2 values (point) or 4 values (envelope).";
        return false;
    }

    internal static bool TryDeserializeGeometry(string json, out GeoServicesGeometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        try
        {
            geometry = JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry);
            if (geometry == null)
            {
                error = "Geometry JSON could not be parsed.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Invalid geometry JSON.";
            return false;
        }
    }

    internal static bool TryParseCoordinateList(ReadOnlySpan<char> value, Span<double> coordinates, out int coordinateCount, out string? error)
    {
        if (!value.TryParseDoubles(coordinates, _coordinateSeparators, out coordinateCount, out error))
        {
            if (coordinateCount == 0 && error == "Value list is empty.")
            {
                error = "Geometry coordinate list is empty.";
            }

            return false;
        }

        return true;
    }
}
