// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Api.Coverages.Handlers;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

// GeoTIFF responses follow OGC API - Coverages subsetting: spatial axes are trimmed in storage-CRS
// coordinates (the domainset axis labels), unlike the CoverageJSON path whose spatial subsets are
// grid indices. A GeoTIFF is one 2D slice, so every other axis resolves to a single position: the
// time axis through datetime (first step by default) and at most one further axis through subset.
internal sealed partial class ZarrCoverageService
{
    internal const string GeoTiffContentType = "image/tiff";
    internal const string GeoTiffLinkType = "image/tiff; application=geotiff";

    private const int MaxGeoTiffAxisPixels = 8192;

    // A downscaled GeoTIFF (a client's overview level) reads more native cells than it writes, so
    // the read window has its own cap, as the shared raster-slice reader does.
    private const long MaxGeoTiffReadBytes = 64L * 1024L * 1024L;

    private static readonly ImmutableHashSet<string> GeoTiffQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f", "subset", "properties", "datetime", "token", "scale-size", "scaleSize");

    private async Task<IResult> GetGeoTiffCoverageAsync(
        HttpContext context,
        ZarrCoverageTarget target,
        CancellationToken cancellationToken)
    {
        var metadata = RequireMetadata(target.Registration);
        var (request, requestError) = ParseGeoTiffRequest(context, metadata);
        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, requestError!);
        }

        var window = ResolveGeoTiffWindow(metadata, request);
        var (slice, output, planError) = PlanGeoTiff(metadata, request, window);
        if (slice is null || output is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, planError!);
        }

        var (renderLease, capacityError) = await OgcCoveragesHandler.TryAcquireCoverageRenderLeaseAsync(
                context,
                (output.Width, output.Height),
                cancellationToken)
            .ConfigureAwait(false);
        if (capacityError is not null)
        {
            return capacityError;
        }

        await using (renderLease)
        {
            return await ReadAndRenderGeoTiffAsync(context, (target, slice, output), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<IResult> ReadAndRenderGeoTiffAsync(
        HttpContext context,
        (ZarrCoverageTarget Target, ZarrTileSlicePlan Slice, ZarrGeoTiffOutput Output) plan,
        CancellationToken cancellationToken)
    {
        var subset = await ReadSubsetAsync(context, (plan.Target, plan.Slice.Plan.Request), cancellationToken)
            .ConfigureAwait(false);
        if (subset.Error is not null)
        {
            return subset.Error;
        }

        var payload = ZarrGeoTiffRenderer.Render(subset.Result!, plan.Slice, plan.Output);
        WriteGeoTiffHeaders(context, plan.Output.Extent);
        ZarrCoverageLog.CoverageServed(_logger, plan.Target.CollectionId, subset.Result!.Variable, payload.Length);
        return Results.Bytes(payload, GeoTiffContentType);
    }

    private static (int Width, int Height) ResolvePlannedSubsetRenderSize(ZarrSubsetRequest request)
    {
        long cells = 1;
        var rank = Math.Min(request.Start.Length, request.Stop.Length);
        for (var i = 0; i < rank; i++)
        {
            cells = Math.Min(int.MaxValue, cells * Math.Max(1, request.Stop[i] - request.Start[i]));
        }

        return ((int)Math.Max(1, cells), 1);
    }

    private static (ZarrGeoTiffCoverageRequest? Request, string? Error) ParseGeoTiffRequest(
        HttpContext context,
        ZarrStoreMetadata metadata)
    {
        var unsupported = context.Request.Query.Keys.FirstOrDefault(parameter => !GeoTiffQueryParameters.Contains(parameter));
        if (unsupported is not null)
        {
            return (null, $"Parameter '{unsupported}' is not supported for GeoTIFF output of a Zarr-backed coverage collection.");
        }

        if (!TryResolveVariable(context, out var variable, out var variableError))
        {
            return (null, variableError);
        }

        var datetime = ParseGeoTiffDateTime(context, metadata);
        var scaleSize = ParseGeoTiffScaleSize(context, metadata);
        if (datetime.Error is not null || scaleSize.Error is not null)
        {
            return (null, datetime.Error ?? scaleSize.Error);
        }

        ZarrGeoTiffCoverageRequest? request = new() { Variable = variable, DateTime = datetime.Value, ScaleSize = scaleSize.Value };
        string? error = null;
        foreach (var segment in context.Request.Query["subset"]
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .SelectMany(static value => SplitTopLevel(value!)))
        {
            (request, error) = ApplyGeoTiffSubset(metadata, segment, request!);
            if (request is null)
            {
                break;
            }
        }

        return (request, error);
    }

    private static (ZarrGeoTiffCoverageRequest? Request, string? Error) ApplyGeoTiffSubset(
        ZarrStoreMetadata metadata,
        string segment,
        ZarrGeoTiffCoverageRequest request)
    {
        var trimmed = segment.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        var axisName = open > 0 ? trimmed[..open].Trim() : trimmed;
        var axis = ResolveGeoTiffSpatialAxis(metadata, axisName);
        if (axis != ZarrSpatialAxis.None)
        {
            var range = ParseCoordinateRange(trimmed, open);
            var existing = axis == ZarrSpatialAxis.X ? request.X : request.Y;
            if (range is null || existing is not null)
            {
                return (null, $"Spatial subset '{trimmed}' must be one {axisName}(low:high) trim in storage-CRS coordinates with low < high.");
            }

            return (axis == ZarrSpatialAxis.X ? request with { X = range } : request with { Y = range }, null);
        }

        if (metadata.TemporalDimension is { } time && string.Equals(time, axisName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"Select the time axis '{time}' of a GeoTIFF coverage with the datetime parameter.");
        }

        var axisError = ValidateSliceAxis(metadata, request.Variable, axisName);
        if (axisError is not null)
        {
            return (null, axisError);
        }

        var (index, error) = ResolveSingleIndex(metadata, trimmed);
        if (index is null || request.SliceIndex is not null)
        {
            return (null, error ?? "A GeoTIFF coverage is one 2D slice; subset at most one non-spatial, non-temporal axis.");
        }

        return (request with { SliceIndex = index }, null);
    }

    // The planner carries one index for the single non-spatial, non-temporal axis of the variable.
    private static string? ValidateSliceAxis(ZarrStoreMetadata metadata, string? variable, string axisName)
    {
        var array = metadata.Arrays.FirstOrDefault(candidate => string.Equals(candidate.Name, variable, StringComparison.Ordinal))
            ?? ResolvePrimaryArray(metadata);
        var sliceAxes = array.DimensionNames
            .Where(name => !IsXDimension(metadata, name) && !IsYDimension(metadata, name) &&
                !string.Equals(name, metadata.TemporalDimension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!sliceAxes.Contains(axisName, StringComparer.OrdinalIgnoreCase))
        {
            return $"Variable '{array.Name}' has no non-spatial, non-temporal axis '{axisName}' to subset.";
        }

        return sliceAxes.Length == 1
            ? null
            : $"Variable '{array.Name}' has more than one non-spatial, non-temporal axis; GeoTIFF output supports one.";
    }

    private static (int? Index, string? Error) ResolveSingleIndex(ZarrStoreMetadata metadata, string segment)
    {
        ZarrCoverageDimensionSubset? subset;
        string? error;
        var resolved = TryMatchCoordinateAxis(metadata, segment, out var coordinateAxis)
            ? TryResolveCoordinateSubset(coordinateAxis!, segment, out subset, out error)
            : TryParseSubsetSegment(segment, out subset, out error);
        if (!resolved)
        {
            return (null, error);
        }

        return subset!.Stop - subset.Start == 1
            ? (subset.Start, null)
            : (null, $"Subset '{segment.Trim()}' must select a single position for GeoTIFF output, which is one 2D slice.");
    }

    // Lon/Lat labels are CRS84 elsewhere in this API, so they address only EPSG:4326 stores here.
    private static ZarrSpatialAxis ResolveGeoTiffSpatialAxis(ZarrStoreMetadata metadata, string axis)
    {
        var lower = axis.ToLowerInvariant();
        var geographic = metadata.Srid == 4326;
        if (string.Equals(metadata.SpatialXDimension, axis, StringComparison.OrdinalIgnoreCase) ||
            lower == "x" || (geographic && lower is "lon" or "long" or "longitude"))
        {
            return ZarrSpatialAxis.X;
        }

        return string.Equals(metadata.SpatialYDimension, axis, StringComparison.OrdinalIgnoreCase) ||
            lower == "y" || (geographic && lower is "lat" or "latitude")
            ? ZarrSpatialAxis.Y
            : ZarrSpatialAxis.None;
    }

    private static (double Low, double High)? ParseCoordinateRange(string segment, int open)
    {
        if (open <= 0 || !segment.EndsWith(')'))
        {
            return null;
        }

        var limits = segment[(open + 1)..^1].Split(':', StringSplitOptions.TrimEntries);
        return limits.Length == 2 &&
            TryParseCoordinate(limits[0], out var low) &&
            TryParseCoordinate(limits[1], out var high) &&
            low < high
            ? (low, high)
            : null;
    }

    private static ((DateTimeOffset? Start, DateTimeOffset? End)? Value, string? Error) ParseGeoTiffDateTime(
        HttpContext context,
        ZarrStoreMetadata metadata)
    {
        var value = OgcCommonUtilities.GetQueryValue(context.Request, "datetime");
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        if (metadata.TemporalDimension is null || metadata.Temporal is null)
        {
            return (null, "The datetime parameter is not supported for this coverage collection because it does not declare a time axis.");
        }

        return OgcTemporalFilterParser.TryParseRange(value, out var start, out var end, out var parseError)
            ? ((start, end), null)
            : (null, parseError ?? "Invalid datetime parameter.");
    }

    private static ((int Width, int Height)? Value, string? Error) ParseGeoTiffScaleSize(
        HttpContext context,
        ZarrStoreMetadata metadata)
    {
        var values = context.Request.Query["scale-size"].Concat(context.Request.Query["scaleSize"])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (values.Length == 0)
        {
            return (null, null);
        }

        var sizes = values.Length == 1
            ? values[0]!.Split(',', StringSplitOptions.TrimEntries).Select(axis => ParseAxisSize(metadata, axis)).ToArray()
            : [];
        var width = sizes.Where(static size => size.Axis == ZarrSpatialAxis.X).Select(static size => size.Size).ToArray();
        var height = sizes.Where(static size => size.Axis == ZarrSpatialAxis.Y).Select(static size => size.Size).ToArray();
        return sizes.Length == 2 && width.Length == 1 && height.Length == 1
            ? ((width[0], height[0]), null)
            : (null, $"scale-size must give both spatial axes once, such as x(256),y(256), each between 1 and {MaxGeoTiffAxisPixels}.");
    }

    private static (ZarrSpatialAxis Axis, int Size) ParseAxisSize(ZarrStoreMetadata metadata, string value)
    {
        var open = value.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !value.EndsWith(')') ||
            !int.TryParse(value[(open + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
            size is <= 0 or > MaxGeoTiffAxisPixels)
        {
            return (ZarrSpatialAxis.None, 0);
        }

        return (ResolveGeoTiffSpatialAxis(metadata, value[..open].Trim()), size);
    }

    // With scale-size the output grid is exactly the requested trim, so a tiled client receives the
    // pixel geometry it asked for (uncovered pixels carry nodata). Without it the trim is clipped to
    // the coverage extent and served at the native cell size.
    private static RasterExtent ResolveGeoTiffWindow(ZarrStoreMetadata metadata, ZarrGeoTiffCoverageRequest request)
    {
        var extent = metadata.Extent;
        var x = request.X ?? (extent.XMin, extent.XMax);
        var y = request.Y ?? (extent.YMin, extent.YMax);
        if (request.ScaleSize is null)
        {
            x = (Math.Max(x.Low, extent.XMin), Math.Min(x.High, extent.XMax));
            y = (Math.Max(y.Low, extent.YMin), Math.Min(y.High, extent.YMax));
        }

        return new RasterExtent { XMin = x.Low, YMin = y.Low, XMax = x.High, YMax = y.High, Srid = metadata.Srid };
    }

    private static (ZarrTileSlicePlan? Slice, ZarrGeoTiffOutput? Output, string? Error) PlanGeoTiff(
        ZarrStoreMetadata metadata,
        ZarrGeoTiffCoverageRequest request,
        RasterExtent window)
    {
        if (!ZarrTileSlicePlanner.TryPlan(
                metadata,
                request.Variable,
                new ZarrTileBounds(window.XMin, window.YMin, window.XMax, window.YMax),
                request.DateTime,
                request.SliceIndex,
                MaxGeoTiffReadBytes,
                out var slice,
                out var planError))
        {
            return (null, null, planError);
        }

        var (width, height) = request.ScaleSize ?? (
            NativePixels(window.XMax - window.XMin, slice!.CellWidth),
            NativePixels(window.YMax - window.YMin, slice!.CellHeight));
        long bytes;
        try
        {
            bytes = ZarrGeoTiffRenderer.MeasureOutputBytes(slice!, width, height);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, ex.Message);
        }

        return width > MaxGeoTiffAxisPixels || height > MaxGeoTiffAxisPixels || bytes > MaxCoverageOutputBytes
            ? (null, null, $"The GeoTIFF output would exceed {MaxGeoTiffAxisPixels} pixels per axis or {MaxCoverageOutputBytes} bytes; narrow the subset or supply scale-size.")
            : (slice, new ZarrGeoTiffOutput { Extent = window, Width = width, Height = height }, null);
    }

    private static int NativePixels(double span, double cellSize)
        => (int)Math.Clamp(Math.Round(span / cellSize), 1, int.MaxValue);

    private static void WriteGeoTiffHeaders(HttpContext context, RasterExtent extent)
    {
        context.Response.Headers.Append(
            "Content-Bbox",
            string.Join(",", FormatDouble(extent.XMin), FormatDouble(extent.YMin), FormatDouble(extent.XMax), FormatDouble(extent.YMax)));
        if (extent.Srid is { } srid and > 0)
        {
            var contentCrs = srid == 4326
                ? SpatialReferenceHelpers.Crs84Uri
                : CreateEpsgUri(srid);
            context.Response.Headers.Append("Content-Crs", $"<{contentCrs}>");
        }
    }
}
