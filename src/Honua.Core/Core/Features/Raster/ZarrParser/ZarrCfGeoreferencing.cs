// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.Multidimensional.Services;

namespace Honua.Core.Features.Raster.ZarrParser;

// Where a Zarr v2 store lives and its consolidated metadata, if it has one.
internal sealed record ZarrCfScan(ICloudRangeReader Reader, string Bucket, string RootPath)
{
    public JsonDocument? Consolidated { get; init; }

    public ZarrStoreMetadata Metadata { get; init; } = null!;
}

// Georeferences a CF-convention datacube (as written by GDAL, xarray or netCDF tools) that carries
// none of Honua's root attributes: the data variable is the first multi-dimensional array that is
// not a coordinate variable, its axes come from the coordinate variables' CF attributes, the extent
// is the coordinate cell edges, and the CRS comes from GDAL's _CRS attribute or the degree units.
internal static partial class ZarrCfGeoreferencing
{
    private const int GeographicSrid = 4326;

    public static async Task<ZarrStoreMetadata> ApplyAsync(ZarrCfScan location, ZarrStoreMetadata metadata, CancellationToken cancellationToken)
    {
        if (ResolvePrimary(metadata) is not { } primary)
        {
            return metadata;
        }

        var scan = location with { Metadata = metadata };
        var axes = await ReadAxesAsync(scan, primary, cancellationToken).ConfigureAwait(false);
        var described = metadata with { PrimaryVariable = primary.Name };
        if (axes.X is not { } x || axes.Y is not { } y)
        {
            return described;
        }

        var xValues = await ReadValuesAsync(scan, x.Array, cancellationToken).ConfigureAwait(false);
        var yValues = await ReadValuesAsync(scan, y.Array, cancellationToken).ConfigureAwait(false);
        var srid = ResolveSrid(await ReadAttributesAsync(scan, primary.Name, cancellationToken).ConfigureAwait(false), x, y);
        if (srid == 0 || CellEdges(xValues) is not { } xEdges || CellEdges(yValues) is not { } yEdges || !xEdges.Ascending)
        {
            return described;
        }

        return described with
        {
            Srid = srid,
            Extent = new() { XMin = xEdges.Min, YMin = yEdges.Min, XMax = xEdges.Max, YMax = yEdges.Max, Srid = srid },
            SpatialXDimension = x.Array.Name,
            SpatialYDimension = y.Array.Name,
            YAxisAscending = yEdges.Ascending,
            TemporalDimension = axes.T?.Array.Name,
            Temporal = axes.T is { } t ? await ResolveTemporalAsync(scan, t, cancellationToken).ConfigureAwait(false) : null,
        };
    }

    private static async Task<CfAxes> ReadAxesAsync(
        ZarrCfScan scan, ZarrArrayMetadata primary, CancellationToken cancellationToken)
    {
        var axes = new CfAxes();
        foreach (var dimension in primary.DimensionNames)
        {
            if (scan.Metadata.Arrays.FirstOrDefault(array => IsCoordinate(array) && array.Name == dimension) is not { } array)
            {
                continue;
            }

            var coordinate = new CfCoordinate(array, await ReadAttributesAsync(scan, array.Name, cancellationToken).ConfigureAwait(false));
            axes = coordinate.Axis switch
            {
                'X' => axes with { X = axes.X ?? coordinate },
                'Y' => axes with { Y = axes.Y ?? coordinate },
                'T' => axes with { T = axes.T ?? coordinate },
                _ => axes,
            };
        }

        return axes;
    }

    private static async Task<TemporalExtent?> ResolveTemporalAsync(
        ZarrCfScan scan, CfCoordinate time, CancellationToken cancellationToken)
    {
        var values = await ReadValuesAsync(scan, time.Array, cancellationToken).ConfigureAwait(false);
        if (values.Length == 0 || time.Units is not { } units
            || !GdalInfoCoverageEnricher.TryDecodeCfTime(units, values[0], values[^1], out var start, out var end))
        {
            return null;
        }

        return new TemporalExtent(start, end, values.Length);
    }

    private static int ResolveSrid(JsonElement? primaryAttributes, CfCoordinate x, CfCoordinate y)
    {
        if (primaryAttributes is { ValueKind: JsonValueKind.Object } attributes
            && attributes.TryGetProperty("_CRS", out var crs) && crs.ValueKind == JsonValueKind.Object
            && crs.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            && EpsgUrl().Match(url.GetString() ?? string.Empty) is { Success: true } match)
        {
            return int.Parse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        return x.Units == "degrees_east" && y.Units == "degrees_north" ? GeographicSrid : 0;
    }

    // Coordinates are cell centres; the extent is their outer cell edges. A single cell has no
    // spacing to derive its size from, and a non-monotonic axis is not a regular grid.
    private static CfEdges? CellEdges(double[] centres)
    {
        if (centres.Length < 2)
        {
            return null;
        }

        var step = (centres[^1] - centres[0]) / (centres.Length - 1);
        var monotonic = centres.Zip(centres.Skip(1), (previous, next) => next - previous).All(delta => delta * step > 0);
        if (!monotonic || !double.IsFinite(step))
        {
            return null;
        }

        var low = Math.Min(centres[0], centres[^1]) - (Math.Abs(step) / 2);
        var high = Math.Max(centres[0], centres[^1]) + (Math.Abs(step) / 2);
        return new CfEdges(low, high, step);
    }

    private static async Task<double[]> ReadValuesAsync(
        ZarrCfScan scan, ZarrArrayMetadata array, CancellationToken cancellationToken)
    {
        var subset = await new ZarrSubsetReader().ReadSubsetAsync(scan.Reader, scan.Bucket, scan.RootPath, scan.Metadata,
            new ZarrSubsetRequest { Variable = array.Name, Start = [0], Stop = [array.Shape[0]] }, cancellationToken).ConfigureAwait(false);
        var size = ZarrSubsetReader.ResolveElementSize(subset.DataType);
        var values = new double[subset.Data.Length / size];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ZarrValueDecoder.TryDecode(subset.Data.AsSpan(i * size, size), subset.DataType, out var value) ? value : double.NaN;
        }

        return values;
    }

    private static async Task<JsonElement?> ReadAttributesAsync(ZarrCfScan scan, string arrayName, CancellationToken cancellationToken)
    {
        if (scan.Consolidated?.RootElement.TryGetProperty("metadata", out var consolidated) == true
            && consolidated.ValueKind == JsonValueKind.Object
            && consolidated.TryGetProperty(arrayName + "/.zattrs", out var attributes))
        {
            return attributes.Clone();
        }

        var key = string.IsNullOrEmpty(scan.RootPath) ? arrayName + "/.zattrs" : $"{scan.RootPath}/{arrayName}/.zattrs";
        using var document = await ZarrMetadataExtractor.TryReadJsonDocumentAsync(scan.Reader, scan.Bucket, key, cancellationToken)
            .ConfigureAwait(false);
        return document?.RootElement.Clone();
    }

    // Keep a declared multi-dimensional primary; otherwise take the first data variable.
    private static ZarrArrayMetadata? ResolvePrimary(ZarrStoreMetadata metadata)
    {
        var declared = metadata.Arrays.FirstOrDefault(array => array.Name == metadata.PrimaryVariable);
        return declared is { Shape.Length: >= 2 } && !IsCoordinate(declared)
            ? declared
            : metadata.Arrays.FirstOrDefault(array => array.Shape.Length >= 2 && !IsCoordinate(array));
    }

    private static bool IsCoordinate(ZarrArrayMetadata array)
        => array.Shape.Length == 1 && array.DimensionNames.Length == 1 && array.DimensionNames[0] == array.Name;

    [GeneratedRegex(@"/EPSG/0/(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EpsgUrl();

    private sealed record CfAxes(CfCoordinate? X = null, CfCoordinate? Y = null, CfCoordinate? T = null);

    private readonly record struct CfEdges(double Min, double Max, double Step)
    {
        public bool Ascending => Step > 0;
    }

    private sealed record CfCoordinate(ZarrArrayMetadata Array, JsonElement? Attributes)
    {
        public string? Units => Attribute("units");

        // CF axis identification: the axis attribute, then standard_name, units, and conventional names.
        public char? Axis => Attribute("axis")?.ToUpperInvariant() switch
        {
            "X" => 'X',
            "Y" => 'Y',
            "T" => 'T',
            _ => (Attribute("standard_name"), Units, Array.Name.ToLowerInvariant()) switch
            {
                ("longitude" or "projection_x_coordinate", _, _) or (_, "degrees_east", _) or (_, _, "lon" or "longitude" or "x") => 'X',
                ("latitude" or "projection_y_coordinate", _, _) or (_, "degrees_north", _) or (_, _, "lat" or "latitude" or "y") => 'Y',
                ("time", _, _) => 'T',
                _ when Units?.Contains(" since ", StringComparison.OrdinalIgnoreCase) == true => 'T',
                _ => null,
            },
        };

        private string? Attribute(string name)
            => Attributes is { ValueKind: JsonValueKind.Object } attributes
                && attributes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
    }
}
