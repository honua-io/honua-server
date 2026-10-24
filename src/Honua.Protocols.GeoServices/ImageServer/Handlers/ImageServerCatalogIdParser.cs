// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Parses ImageServer <c>rasterIds</c> and <c>bandIds</c> lists shared by the
/// statistics handlers.
/// </summary>
internal static class ImageServerCatalogIdParser
{
    /// <summary>
    /// Parses the Esri <c>rasterIds</c> parameter as a list of raster catalog object IDs (long).
    /// Accepts CSV (e.g. <c>1,3</c>) or JSON array (e.g. <c>[1,3]</c>) form.
    /// </summary>
    internal static bool TryParseRasterIds(string? raw, out long[]? rasterIds, out string? error)
    {
        rasterIds = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "rasterIds must be a JSON array of catalog object IDs.";
                    return false;
                }

                var values = new List<long>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var id) || id <= 0)
                    {
                        error = "rasterIds entries must be positive catalog object IDs.";
                        return false;
                    }

                    values.Add(id);
                }

                rasterIds = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (JsonException)
            {
                error = "rasterIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                error = "rasterIds entries must be positive catalog object IDs.";
                return false;
            }

            parsed.Add(id);
        }

        rasterIds = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }

    /// <summary>
    /// Parses the optional <c>bandIds</c> extension parameter as a list of 0-based band indices
    /// (the same base as <c>exportImage</c>, #4068) and returns them as 1-based raster-store bands.
    /// Accepts CSV or JSON array form.
    /// </summary>
    internal static bool TryParseBandIds(string? raw, out int[]? bands, out string? error)
    {
        bands = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "bandIds must be a JSON array of 0-based band indices.";
                    return false;
                }

                var values = new List<int>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    // int.MaxValue has no 1-based store band: shifting it would wrap negative.
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var band) || band is < 0 or int.MaxValue)
                    {
                        error = "bandIds entries must be non-negative 0-based band indices.";
                        return false;
                    }

                    values.Add(band + 1);
                }

                bands = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (JsonException)
            {
                error = "bandIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band) || band is < 0 or int.MaxValue)
            {
                error = "bandIds entries must be non-negative 0-based band indices.";
                return false;
            }

            parsed.Add(band + 1);
        }

        bands = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }
}
