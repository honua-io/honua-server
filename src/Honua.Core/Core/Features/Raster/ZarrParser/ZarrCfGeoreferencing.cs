// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

internal enum ZarrCfAxis
{
    None,
    X,
    Y,
}

internal readonly record struct ZarrCfArrayHints
{
    public ZarrCfAxis Axis { get; init; }

    // Units are degrees (geographic), so the axis implies EPSG:4326 when no _CRS is declared.
    public bool Degrees { get; init; }

    public int Srid { get; init; }
}

// One store read: where it lives, the arrays found, and the CF hints from their attributes.
internal sealed class ZarrCfScan
{
    public required ICloudRangeReader Reader { get; init; }

    public required string Bucket { get; init; }

    public required string RootPath { get; init; }

    public List<ZarrArrayMetadata> Arrays { get; } = [];

    public Dictionary<string, ZarrCfArrayHints> Hints { get; } = new(StringComparer.Ordinal);
}

// Georeferences a CF-convention Zarr store (GDAL, xarray) that carries no Honua root manifest:
// 1-D coordinate variables named after the primary array's dimensions give the spatial
// dimensions, extent and row order; a _CRS attribute or degree units give the SRID.
internal static class ZarrCfGeoreferencing
{
    internal const int MaxCoordinateLength = 1_000_000;

    // Regular-spacing tolerance: a fraction of the step, or float32 rounding at the axis's
    // magnitude (about 7 significant digits), whichever is larger.
    private const double SpacingTolerance = 1e-4;
    private const double Float32Precision = 1e-6;

    private static readonly string[] RootGeoreferencingKeys = ["crs_wkid", "crs", "extent", "x_dimension", "y_dimension"];

    internal static bool DeclaresGeoreferencing(JsonElement? root)
        => root is { ValueKind: JsonValueKind.Object } attrs &&
           RootGeoreferencingKeys.Any(key => attrs.TryGetProperty(key, out _));

    // The highest-rank data array (first on ties), so scalar grid-mapping variables such as
    // spatial_ref and 2-D bounds arrays never displace the gridded variable.
    internal static string? ResolveDefaultPrimary(IReadOnlyList<ZarrArrayMetadata> arrays)
        => arrays.Where(array => !IsCoordinateVariable(array)).OrderByDescending(array => array.Shape.Length).FirstOrDefault()?.Name
           ?? (arrays.Count > 0 ? arrays[0].Name : null);

    internal static void Collect(Dictionary<string, ZarrCfArrayHints> hints, string name, JsonElement? attrs)
    {
        if (attrs is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        var (axis, degrees) = ClassifyAxis(element);
        hints[name] = new ZarrCfArrayHints { Axis = axis, Degrees = degrees, Srid = ReadCrsSrid(element) };
    }

    internal static async Task<ZarrStoreMetadata> ApplyAsync(
        ZarrCfScan scan,
        ZarrStoreMetadata metadata,
        CancellationToken cancellationToken)
    {
        var primary = metadata.Arrays.FirstOrDefault(array => array.Name == metadata.PrimaryVariable);
        if (primary is not { Shape.Length: >= 2 }
            || FindCoordinate(scan, primary, ZarrCfAxis.X) is not { } xArray
            || FindCoordinate(scan, primary, ZarrCfAxis.Y) is not { } yArray)
        {
            return metadata;
        }

        var srid = ResolveSrid(scan.Hints, primary, (xArray, yArray));
        if (srid <= 0)
        {
            return metadata;
        }

        var x = await ReadEdgesAsync(scan, (metadata, xArray), cancellationToken).ConfigureAwait(false);
        var y = await ReadEdgesAsync(scan, (metadata, yArray), cancellationToken).ConfigureAwait(false);
        if (x is not { Ascending: true } || y is null)
        {
            // Descending X columns have no consumer support; leave the store ungeoreferenced.
            return metadata;
        }

        return metadata with
        {
            Srid = srid,
            Extent = new RasterExtent { XMin = x.Value.Low, YMin = y.Value.Low, XMax = x.Value.High, YMax = y.Value.High, Srid = srid },
            SpatialXDimension = xArray.Name,
            SpatialYDimension = yArray.Name,
            YAxisAscending = y.Value.Ascending,
        };
    }

    private static int ResolveSrid(
        Dictionary<string, ZarrCfArrayHints> hints,
        ZarrArrayMetadata primary,
        (ZarrArrayMetadata X, ZarrArrayMetadata Y) coordinates)
    {
        if (hints.TryGetValue(primary.Name, out var primaryHints) && primaryHints.Srid > 0)
        {
            return primaryHints.Srid;
        }

        return hints[coordinates.X.Name].Degrees && hints[coordinates.Y.Name].Degrees ? 4326 : 0;
    }

    private static ZarrArrayMetadata? FindCoordinate(ZarrCfScan scan, ZarrArrayMetadata primary, ZarrCfAxis axis)
    {
        var matches = scan.Arrays
            .Where(array => IsCoordinateVariable(array) &&
                            primary.DimensionNames.Contains(array.Name, StringComparer.Ordinal) &&
                            scan.Hints.TryGetValue(array.Name, out var hint) && hint.Axis == axis)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsCoordinateVariable(ZarrArrayMetadata array)
        => array.Shape.Length == 1 && array.DimensionNames.Length == 1 &&
           string.Equals(array.DimensionNames[0], array.Name, StringComparison.Ordinal);

    private static async Task<(double Low, double High, bool Ascending)?> ReadEdgesAsync(
        ZarrCfScan scan,
        (ZarrStoreMetadata Metadata, ZarrArrayMetadata Coordinate) target,
        CancellationToken cancellationToken)
    {
        var length = target.Coordinate.Shape[0];
        if (length < 2 || length > MaxCoordinateLength)
        {
            return null;
        }

        var values = await ReadValuesAsync(scan, target, cancellationToken).ConfigureAwait(false);
        return values is null ? null : TryResolveCellEdges(values);
    }

    private static async Task<double[]?> ReadValuesAsync(
        ZarrCfScan scan,
        (ZarrStoreMetadata Metadata, ZarrArrayMetadata Coordinate) target,
        CancellationToken cancellationToken)
    {
        var (metadata, coordinate) = target;
        ZarrSubsetResult subset;
        try
        {
            subset = await new ZarrSubsetReader().ReadSubsetAsync(
                scan.Reader,
                scan.Bucket,
                scan.RootPath,
                metadata,
                new ZarrSubsetRequest { Variable = coordinate.Name, Start = [0], Stop = [coordinate.Shape[0]] },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // An unreadable coordinate (unsupported codec or dtype) leaves the store
            // ungeoreferenced, as it was before CF detection; the data arrays still serve.
            return null;
        }

        return Decode(subset, coordinate.Shape[0]);
    }

    private static double[]? Decode(ZarrSubsetResult subset, int length)
    {
        var elementSize = ZarrSubsetReader.ResolveElementSize(subset.DataType);
        var values = new double[length];
        for (var i = 0; i < length; i++)
        {
            var slice = subset.Data.AsSpan(i * elementSize, elementSize);
            if (!ZarrValueDecoder.TryDecode(slice, subset.DataType, out values[i]) || !double.IsFinite(values[i]))
            {
                return null;
            }
        }

        return values;
    }

    // Coordinates are cell centres (CF); the extent is the outer cell edges, so extent / shape
    // equals the grid spacing every consumer derives. Irregular or non-monotonic axes are refused
    // because consumers map pixels linearly across the extent.
    internal static (double Low, double High, bool Ascending)? TryResolveCellEdges(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var step = (values[^1] - values[0]) / (values.Count - 1);
        if (!double.IsFinite(step) || !(Math.Abs(step) > 0))
        {
            return null;
        }

        var magnitude = Math.Max(Math.Abs(values[0]), Math.Abs(values[^1]));
        var tolerance = Math.Max(Math.Abs(step) * SpacingTolerance, magnitude * Float32Precision);
        for (var i = 1; i < values.Count; i++)
        {
            if (Math.Abs(values[i] - values[i - 1] - step) > tolerance)
            {
                return null;
            }
        }

        var half = Math.Abs(step) / 2;
        var low = Math.Min(values[0], values[^1]) - half;
        var high = Math.Max(values[0], values[^1]) + half;
        return (low, high, step > 0);
    }

    private static (ZarrCfAxis Axis, bool Degrees) ClassifyAxis(JsonElement attrs)
    {
        var standardName = ReadLower(attrs, "standard_name");
        var units = ReadLower(attrs, "units");
        var axis = ReadLower(attrs, "axis");

        var eastDegrees = units is "degrees_east" or "degree_east" or "degrees_e" or "degree_e" or "degreese" or "degreee";
        var northDegrees = units is "degrees_north" or "degree_north" or "degrees_n" or "degree_n" or "degreesn" or "degreen";
        if (eastDegrees || standardName is "longitude" or "projection_x_coordinate" or "grid_longitude" || axis == "x")
        {
            return (ZarrCfAxis.X, eastDegrees || standardName == "longitude");
        }

        if (northDegrees || standardName is "latitude" or "projection_y_coordinate" or "grid_latitude" || axis == "y")
        {
            return (ZarrCfAxis.Y, northDegrees || standardName == "latitude");
        }

        return (ZarrCfAxis.None, false);
    }

    private static string? ReadLower(JsonElement attrs, string property)
        => attrs.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant()
            : null;

    // GDAL writes `_CRS` as {"url": ".../EPSG/0/<code>", "projjson": {"id": {"authority", "code"}}, "wkt"}.
    private static int ReadCrsSrid(JsonElement attrs)
    {
        if (!attrs.TryGetProperty("_CRS", out var crs) || crs.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (crs.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
            ParseEpsgUrl(url.GetString()) is { } fromUrl)
        {
            return fromUrl;
        }

        return crs.TryGetProperty("projjson", out var projjson) && projjson.ValueKind == JsonValueKind.Object &&
               projjson.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Object
            ? ReadEpsgId(id)
            : 0;
    }

    private static int ReadEpsgId(JsonElement id)
    {
        var isEpsg = id.TryGetProperty("authority", out var authority) && authority.ValueKind == JsonValueKind.String &&
                     string.Equals(authority.GetString(), "EPSG", StringComparison.OrdinalIgnoreCase);
        return isEpsg && id.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
               code.TryGetInt32(out var srid) && srid > 0
            ? srid
            : 0;
    }

    private static int? ParseEpsgUrl(string? url)
    {
        const string marker = "/EPSG/0/";
        var index = url?.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return index >= 0 &&
               int.TryParse(url![(index + marker.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var srid) &&
               srid > 0
            ? srid
            : null;
    }
}
