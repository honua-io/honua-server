// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Services;

/// <summary>
/// Enriches the structural metadata from <see cref="GdalMultidimensionalMetadataMapper"/>
/// with the spatial extent, cell resolution, and temporal/vertical bounds that
/// <c>gdalmdiminfo</c> cannot supply (it emits no coordinate values). The values
/// come from a classic <c>gdalinfo -json</c> pass over the same source — the
/// convert-time enrichment recorded in ADR-0039 "Path Selection (#1756)".
/// </summary>
/// <remarks>
/// Extent and resolution are read from the GDAL geotransform / corner
/// coordinates of the 2D view. Non-spatial axes are enumerated from
/// <c>NETCDF_DIM_EXTRA</c> and classified by their CF <c>units</c>: an axis whose
/// units contain "since" is the temporal axis (decoded against the CF epoch);
/// any other extra axis is treated as vertical.
/// </remarks>
public static class GdalInfoCoverageEnricher
{
    /// <summary>
    /// Returns <paramref name="baseMetadata"/> with extent, resolution, and
    /// temporal/vertical extents merged in from a <c>gdalinfo -json</c> document.
    /// Fields that cannot be resolved are left at their base values.
    /// </summary>
    public static MultidimensionalCoverageMetadata Enrich(
        MultidimensionalCoverageMetadata baseMetadata,
        string gdalInfoJson)
    {
        ArgumentNullException.ThrowIfNull(baseMetadata);
        if (string.IsNullOrWhiteSpace(gdalInfoJson))
        {
            return baseMetadata;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(gdalInfoJson);
        }
        catch (JsonException)
        {
            return baseMetadata;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return baseMetadata;
            }

            var resolution = ReadResolution(root) ?? baseMetadata.Resolution;
            var extent = ReadExtent(root, baseMetadata.Srid) ?? baseMetadata.Extent;
            var (temporal, vertical) = ReadAxes(root);

            return baseMetadata with
            {
                Extent = extent,
                Resolution = resolution,
                Temporal = temporal ?? baseMetadata.Temporal,
                Vertical = vertical ?? baseMetadata.Vertical,
            };
        }
    }

    private static (double X, double Y)? ReadResolution(JsonElement root)
    {
        if (!root.TryGetProperty("geoTransform", out var gt) || gt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<double>();
        // Not a simple filter: TryGetDouble's out-param is the value being collected, so
        // `.Where(...)` can't express this without duplicating the TryGetDouble call.
        foreach (var entry in gt.EnumerateArray())
        {
            if (entry.TryGetDouble(out var v))
            {
                values.Add(v);
            }
        }

        // geoTransform = [originX, pixelWidth, rowRotation, originY, colRotation, pixelHeight]
        return values.Count == 6 ? (Math.Abs(values[1]), Math.Abs(values[5])) : null;
    }

    private static RasterExtent? ReadExtent(JsonElement root, int srid)
    {
        if (!root.TryGetProperty("cornerCoordinates", out var corners) || corners.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryReadPoint(corners, "upperLeft", out var ulx, out var uly) ||
            !TryReadPoint(corners, "lowerRight", out var lrx, out var lry))
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = Math.Min(ulx, lrx),
            YMin = Math.Min(uly, lry),
            XMax = Math.Max(ulx, lrx),
            YMax = Math.Max(uly, lry),
            Srid = srid == 0 ? null : srid,
        };
    }

    private static (TemporalExtent? Temporal, VerticalExtent? Vertical) ReadAxes(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("", out var defaultDomain) || defaultDomain.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var extraDims = ParseBraceList(GetString(defaultDomain, "NETCDF_DIM_EXTRA"));
        if (extraDims.Count == 0)
        {
            return (null, null);
        }

        TemporalExtent? temporal = null;
        VerticalExtent? vertical = null;

        foreach (var dim in extraDims)
        {
            var units = GetString(defaultDomain, $"{dim}#units");
            var values = ParseBraceValues(GetString(defaultDomain, $"NETCDF_DIM_{dim}_VALUES"));
            if (values.Count == 0)
            {
                continue;
            }

            var min = values.Min();
            var max = values.Max();

            if (units is not null && units.Contains(" since ", StringComparison.OrdinalIgnoreCase))
            {
                if (temporal is null && TryDecodeCfTime(units, min, max, out var start, out var end))
                {
                    temporal = new TemporalExtent(start, end, values.Count);
                }
            }
            else if (vertical is null)
            {
                vertical = new VerticalExtent(min, max, values.Count, units ?? string.Empty);
            }
        }

        return (temporal, vertical);
    }

    private static bool TryDecodeCfTime(string units, double min, double max, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;

        var sinceIndex = units.IndexOf(" since ", StringComparison.OrdinalIgnoreCase);
        if (sinceIndex < 0)
        {
            return false;
        }

        var unit = units[..sinceIndex].Trim();
        var epochText = units[(sinceIndex + " since ".Length)..].Trim();

        if (!TryParseEpoch(epochText, out var epoch) || !TryUnitToSeconds(unit, out var secondsPerUnit))
        {
            return false;
        }

        start = epoch.AddSeconds(min * secondsPerUnit);
        end = epoch.AddSeconds(max * secondsPerUnit);
        return true;
    }

    private static bool TryParseEpoch(string text, out DateTimeOffset epoch)
    {
        // CF epochs look like "2026-01-01 00:00:00", "2026-01-01", or ISO-8601.
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out epoch);
    }

    private static bool TryUnitToSeconds(string unit, out double secondsPerUnit)
    {
        secondsPerUnit = unit.ToLowerInvariant() switch
        {
            "second" or "seconds" or "sec" or "secs" or "s" => 1d,
            "minute" or "minutes" or "min" or "mins" => 60d,
            "hour" or "hours" or "hr" or "hrs" or "h" => 3600d,
            "day" or "days" or "d" => 86400d,
            _ => 0d,
        };
        return secondsPerUnit > 0d;
    }

    private static bool TryReadPoint(JsonElement corners, string name, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (!corners.TryGetProperty(name, out var point) || point.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var coords = new List<double>();
        // Not a simple filter: TryGetDouble's out-param is the value being collected, so
        // `.Where(...)` can't express this without duplicating the TryGetDouble call.
        foreach (var entry in point.EnumerateArray())
        {
            if (entry.TryGetDouble(out var v))
            {
                coords.Add(v);
            }
        }

        if (coords.Count < 2)
        {
            return false;
        }

        x = coords[0];
        y = coords[1];
        return true;
    }

    private static List<string> ParseBraceList(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var part in value.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(part);
        }

        return result;
    }

    private static List<double> ParseBraceValues(string? value)
    {
        var result = new List<double>();
        // Not a simple filter: TryParse's out-param is the value being collected, so
        // `.Where(...)` can't express this without duplicating the TryParse call.
        foreach (var part in ParseBraceList(value))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                result.Add(v);
            }
        }

        return result;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
