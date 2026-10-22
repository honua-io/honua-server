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
/// coordinates of the 2D view. Non-spatial axes come from either the classic
/// NetCDF metadata (<c>NETCDF_DIM_EXTRA</c>) or, for a derived Zarr opened
/// without the netCDF driver, per-band <c>DIM_&lt;axis&gt;_VALUE</c> /
/// <c>DIM_&lt;axis&gt;_UNIT</c> items. An axis whose units contain "since" is
/// temporal (decoded against the CF epoch); any other extra axis is vertical.
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
            var fromBands = ReadBandAxes(root);
            var yAxisAscending = ReadYAxisAscending(root);

            return baseMetadata with
            {
                Extent = extent,
                Resolution = resolution,
                Temporal = temporal ?? fromBands.Temporal ?? baseMetadata.Temporal,
                Vertical = vertical ?? fromBands.Vertical ?? baseMetadata.Vertical,
                YAxisAscending = yAxisAscending ?? baseMetadata.YAxisAscending,
                HasYAxisOrientation = yAxisAscending.HasValue || baseMetadata.HasYAxisOrientation,
                Statistics = ReadStatistics(root) ?? baseMetadata.Statistics,
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
        values.AddRange(gt.EnumerateArray()
            .Select(entry => entry.TryGetDouble(out var value) ? (double?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value.GetValueOrDefault()));

        // geoTransform = [originX, pixelWidth, rowRotation, originY, colRotation, pixelHeight]
        return values.Count == 6 ? (Math.Abs(values[1]), Math.Abs(values[5])) : null;
    }

    private static bool? ReadYAxisAscending(JsonElement root)
    {
        if (!root.TryGetProperty("geoTransform", out var gt) || gt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = gt.EnumerateArray().ToArray();
        if (values.Length != 6 || !values[5].TryGetDouble(out var pixelHeight) || Math.Abs(pixelHeight) < double.Epsilon)
        {
            return null;
        }

        // GDAL uses a positive row height for a south-up (ascending-Y) array.
        return pixelHeight > 0;
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

    private static (TemporalExtent? Temporal, VerticalExtent? Vertical) ReadBandAxes(JsonElement root)
    {
        var axes = new Dictionary<string, AxisSamples>(StringComparer.Ordinal);
        CollectDimensionMetadata(root, axes);
        if (root.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array)
        {
            foreach (var band in bands.EnumerateArray())
            {
                CollectDimensionMetadata(band, axes);
            }
        }

        TemporalExtent? temporal = null;
        VerticalExtent? vertical = null;
        foreach (var axis in axes)
        {
            var samples = axis.Value;
            if (samples.Unit is not null && samples.Unit.Contains(" since ", StringComparison.OrdinalIgnoreCase))
            {
                if (temporal is null &&
                    samples.Numeric.Count > 0 &&
                    TryDecodeCfTime(samples.Unit, samples.Numeric.Min(), samples.Numeric.Max(), out var start, out var end))
                {
                    temporal = new TemporalExtent(start, end, samples.Numeric.Count);
                }

                continue;
            }

            if (samples.Dates.Count > 0 && temporal is null)
            {
                var ordered = samples.Dates.Order().ToArray();
                temporal = new TemporalExtent(ordered[0], ordered[^1], ordered.Length);
                continue;
            }

            if (vertical is null &&
                samples.Numeric.Count > 0 &&
                !IsGeographicUnit(samples.Unit))
            {
                vertical = new VerticalExtent(samples.Numeric.Min(), samples.Numeric.Max(), samples.Numeric.Count, samples.Unit ?? string.Empty);
            }
        }

        return (temporal, vertical);
    }

    private static void CollectDimensionMetadata(JsonElement owner, Dictionary<string, AxisSamples> axes)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("", out var domain) ||
            domain.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in domain.EnumerateObject())
        {
            if (!property.Name.StartsWith("DIM_", StringComparison.Ordinal))
            {
                continue;
            }

            var isValue = property.Name.EndsWith("_VALUE", StringComparison.Ordinal);
            var isUnit = property.Name.EndsWith("_UNIT", StringComparison.Ordinal);
            if (!isValue && !isUnit)
            {
                continue;
            }

            var suffixLength = isValue ? "_VALUE".Length : "_UNIT".Length;
            var axisName = property.Name["DIM_".Length..^suffixLength];
            if (axisName.Length == 0)
            {
                continue;
            }

            if (!axes.TryGetValue(axisName, out var samples))
            {
                samples = new AxisSamples();
                axes[axisName] = samples;
            }

            if (isUnit)
            {
                samples.Unit ??= property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                continue;
            }

            if (TryReadDouble(property.Value, out var number))
            {
                samples.Numeric.Add(number);
            }
            else if (property.Value.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(
                         property.Value.GetString(),
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                         out var instant))
            {
                samples.Dates.Add(instant);
            }
        }
    }

    private static CoverageSampleStatistics? ReadStatistics(JsonElement root)
    {
        if (!root.TryGetProperty("bands", out var bands) || bands.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double? minimum = null;
        double? maximum = null;
        double meanSum = 0;
        var meanCount = 0;
        double? onlyStandardDeviation = null;
        var standardDeviationCount = 0;
        foreach (var band in bands.EnumerateArray())
        {
            if (band.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (ReadBandStat(band, "minimum", "STATISTICS_MINIMUM") is double bandMin)
            {
                minimum = minimum is null ? bandMin : Math.Min(minimum.Value, bandMin);
            }

            if (ReadBandStat(band, "maximum", "STATISTICS_MAXIMUM") is double bandMax)
            {
                maximum = maximum is null ? bandMax : Math.Max(maximum.Value, bandMax);
            }

            if (ReadBandStat(band, "mean", "STATISTICS_MEAN") is double bandMean)
            {
                meanSum += bandMean;
                meanCount++;
            }

            if (ReadBandStat(band, "stdDev", "STATISTICS_STDDEV") is double bandStd)
            {
                onlyStandardDeviation = bandStd;
                standardDeviationCount++;
            }
        }

        if (minimum is null || maximum is null)
        {
            return null;
        }

        return new CoverageSampleStatistics(
            minimum.Value,
            maximum.Value,
            meanCount > 0 ? meanSum / meanCount : null,
            standardDeviationCount == 1 ? onlyStandardDeviation : null);
    }

    private static double? ReadBandStat(JsonElement band, string property, string metadataKey)
    {
        if (band.TryGetProperty(property, out var direct) && TryReadDouble(direct, out var directValue))
        {
            return directValue;
        }

        if (band.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("", out var domain) &&
            domain.ValueKind == JsonValueKind.Object &&
            domain.TryGetProperty(metadataKey, out var stored) &&
            TryReadDouble(stored, out var storedValue))
        {
            return storedValue;
        }

        return null;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return double.IsFinite(value);
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return double.IsFinite(value);
        }

        value = 0;
        return false;
    }

    private static bool IsGeographicUnit(string? unit)
        => unit is not null && unit.Trim().ToLowerInvariant() is "degrees_east" or "degrees_north" or "degree" or "degrees";

    private sealed class AxisSamples
    {
        public HashSet<double> Numeric { get; } = [];

        public HashSet<DateTimeOffset> Dates { get; } = [];

        public string? Unit { get; set; }
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
        coords.AddRange(point.EnumerateArray()
            .Select(entry => entry.TryGetDouble(out var value) ? (double?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value.GetValueOrDefault()));

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
        result.AddRange(ParseBraceList(value)
            .Select(part => double.TryParse(
                part,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                    ? (double?)parsed
                    : null)
            .Where(parsed => parsed.HasValue)
            .Select(parsed => parsed.GetValueOrDefault()));

        return result;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
