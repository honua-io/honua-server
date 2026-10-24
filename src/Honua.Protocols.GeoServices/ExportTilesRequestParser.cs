// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Shared exportTiles level and package parsing for MapServer and ImageServer.
/// </summary>
internal static class ExportTilesRequestParser
{
    /// <summary>
    /// Resolves whether the request asked for an Esri tile package (TPK).
    /// Accepts <c>tilePackage=true</c> or <c>storageFormat=tpk</c> for the
    /// exploded-cache package; <c>zip</c>/empty selects the flat ZIP archive.
    /// The proprietary compact (TPKX) form is rejected with a 400.
    /// </summary>
    internal static bool TryResolveExportTilesPackage(
        string? tilePackageValue,
        string? storageFormatValue,
        out bool tilePackage,
        out string? error)
    {
        tilePackage = false;
        error = null;

        if (!string.IsNullOrWhiteSpace(tilePackageValue) &&
            bool.TryParse(tilePackageValue.Trim(), out var parsedFlag))
        {
            tilePackage = parsedFlag;
        }

        if (string.IsNullOrWhiteSpace(storageFormatValue))
        {
            return true;
        }

        switch (storageFormatValue.Trim().ToLowerInvariant())
        {
            case "tpk":
            case "esritpk":
            case "exploded":
                tilePackage = true;
                return true;
            case "zip":
            case "":
                return true;
            case "tpkx":
            case "compact":
            case "compactv2":
                error = "storageFormat 'tpkx'/'compact' (Esri compact bundle cache) is not supported. Use storageFormat=tpk for an exploded tile package or storageFormat=zip for a flat archive.";
                return false;
            default:
                error = $"storageFormat '{storageFormatValue}' is not supported. Use tpk or zip.";
                return false;
        }
    }

    internal static bool TryParseExportTileLevels(
        string? levelsValue,
        string? minZoomValue,
        string? maxZoomValue,
        TileLimits limits,
        out int[] levels,
        out int minZoom,
        out int maxZoom,
        out string? error)
    {
        levels = [];
        minZoom = limits.MinTileZoom;
        maxZoom = limits.MinTileZoom;
        error = null;

        var parsed = new SortedSet<int>();
        if (!string.IsNullOrWhiteSpace(levelsValue))
        {
            foreach (var rangeParts in levelsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(token => token.Split('-', StringSplitOptions.TrimEntries)))
            {
                if (rangeParts.Length == 1)
                {
                    if (!TryAddExportTilesLevel(rangeParts[0], limits, parsed, out error))
                    {
                        return false;
                    }
                }
                else if (rangeParts.Length == 2)
                {
                    if (!int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                        !int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
                        start > end)
                    {
                        error = "Invalid levels range.";
                        return false;
                    }

                    for (var level = start; level <= end; level++)
                    {
                        if (!TryAddExportTilesLevel(level.ToString(CultureInfo.InvariantCulture), limits, parsed, out error))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    error = "Invalid levels parameter.";
                    return false;
                }
            }
        }

        if (parsed.Count == 0)
        {
            if (!TryParseOptionalZoom(minZoomValue, limits.MinTileZoom, limits, out minZoom, out error) ||
                !TryParseOptionalZoom(maxZoomValue, minZoom, limits, out maxZoom, out error))
            {
                return false;
            }

            if (minZoom > maxZoom)
            {
                error = "minZoom must be less than or equal to maxZoom.";
                return false;
            }

            for (var level = minZoom; level <= maxZoom; level++)
            {
                parsed.Add(level);
            }
        }

        levels = [.. parsed];
        minZoom = levels[0];
        maxZoom = levels[^1];
        return true;
    }

    private static bool TryAddExportTilesLevel(
        string value,
        TileLimits limits,
        SortedSet<int> levels,
        out string? error)
    {
        error = null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            error = "Invalid levels parameter.";
            return false;
        }

        if (level < limits.MinTileZoom || level > limits.MaxTileZoom)
        {
            error = $"Tile level {level} is outside supported range ({limits.MinTileZoom}-{limits.MaxTileZoom}).";
            return false;
        }

        levels.Add(level);
        return true;
    }

    private static bool TryParseOptionalZoom(
        string? value,
        int defaultValue,
        TileLimits limits,
        out int zoom,
        out string? error)
    {
        zoom = defaultValue;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out zoom))
        {
            error = "Invalid zoom parameter.";
            return false;
        }

        if (zoom < limits.MinTileZoom || zoom > limits.MaxTileZoom)
        {
            error = $"Zoom level {zoom} is outside supported range ({limits.MinTileZoom}-{limits.MaxTileZoom}).";
            return false;
        }

        return true;
    }
}
