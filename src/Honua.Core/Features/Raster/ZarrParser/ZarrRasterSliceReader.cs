// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Canonical native-CRS reader and grayscale PNG renderer for bounded 2D Zarr slices.
/// </summary>
public sealed class ZarrRasterSliceReader : IZarrRasterSliceReader
{
    private const int MaxOutputDimension = 4096;
    private const int MaxSelections = 8;
    private const int MaxIdentifierLength = 128;
    private const long MaxOutputPixels = 16L * 1024L * 1024L;
    private const long MaxSubsetBytes = 64L * 1024L * 1024L;
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Raster.Zarr");

    private readonly IZarrStore _zarrStore;
    private readonly IZarrSubsetReader _subsetReader;
    private readonly IReadOnlyList<ICloudRangeReader> _rangeReaders;

    /// <summary>Initializes the canonical 2D slice reader.</summary>
    public ZarrRasterSliceReader(
        IZarrStore zarrStore,
        IZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        ArgumentNullException.ThrowIfNull(rangeReaders);
        _rangeReaders = rangeReaders.ToArray();
    }

    /// <inheritdoc />
    public async Task<ZarrRasterSliceReadResult> ReadAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selections);

        using var activity = ActivitySource.StartActivity("zarr.raster-slice.read", ActivityKind.Internal);
        activity?.SetTag("honua.layer.id", layerId);
        activity?.SetTag("honua.slice.selection_count", request.Selections.Count);
        activity?.SetTag("honua.output.width", request.OutputWidth);
        activity?.SetTag("honua.output.height", request.OutputHeight);
        activity?.SetTag("honua.output.format", "png");

        if (!TryValidateRequest(request, out var validationError))
        {
            return Finish(
                ZarrRasterSliceReadStatus.InvalidSelection,
                request.Selections.Count,
                error: validationError,
                activity: activity);
        }

        var resolution = await ZarrServableRegistrationResolver
            .ResolveAsync(_zarrStore, _rangeReaders, layerId, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsSuccess)
        {
            return resolution.HasScannedRegistration
                ? Finish(
                    ZarrRasterSliceReadStatus.ReaderUnavailable,
                    request.Selections.Count,
                    error: "No scanned multidimensional coverage has a configured storage reader.",
                    activity: activity)
                : Finish(
                    ZarrRasterSliceReadStatus.RegistrationNotFound,
                    request.Selections.Count,
                    error: "No readable multidimensional coverage is registered for this layer.",
                    activity: activity);
        }

        var registration = resolution.Registration!;
        var metadata = registration.Metadata!;
        if (request.InputSrid is { } srid && metadata.Srid > 0 && srid != metadata.Srid)
        {
            return Finish(
                ZarrRasterSliceReadStatus.InvalidSelection,
                request.Selections.Count,
                error: $"Slice bounds must use the coverage spatial reference EPSG:{metadata.Srid}; reprojection is not supported.",
                activity: activity);
        }

        var bounds = request.Bounds ?? metadata.Extent;
        if (!Intersects(bounds, metadata.Extent))
        {
            return Finish(
                ZarrRasterSliceReadStatus.OutsideCoverage,
                request.Selections.Count,
                error: "The requested bounds do not intersect the coverage extent.",
                activity: activity);
        }

        if (!Contains(metadata.Extent, bounds))
        {
            return Finish(
                ZarrRasterSliceReadStatus.InvalidSelection,
                request.Selections.Count,
                error: "Native Zarr slice export requires bounds fully contained by the coverage extent.",
                activity: activity);
        }

        if (!TryResolveSelections(
                metadata,
                request.Selections,
                out var variable,
                out var datetime,
                out var verticalIndex,
                out var selectionError))
        {
            return Finish(
                ZarrRasterSliceReadStatus.InvalidSelection,
                request.Selections.Count,
                variable,
                error: selectionError,
                activity: activity);
        }

        activity?.SetTag("honua.slice.variable", variable);
        activity?.SetTag(
            "honua.slice.dimensions",
            string.Join(',', request.Selections.Select(static selection => selection.Dimension)));

        var tileBounds = new ZarrTileBounds(bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax);
        if (!ZarrTileSlicePlanner.TryPlan(
                metadata,
                variable,
                tileBounds,
                datetime,
                verticalIndex,
                MaxSubsetBytes,
                out var slice,
                out var planError))
        {
            var status = planError?.Contains("does not intersect", StringComparison.Ordinal) == true
                ? ZarrRasterSliceReadStatus.OutsideCoverage
                : ZarrRasterSliceReadStatus.InvalidSelection;
            return Finish(
                status,
                request.Selections.Count,
                variable,
                error: planError ?? "The coordinate-selected slice could not be planned.",
                activity: activity);
        }

        try
        {
            var subset = await _subsetReader.ReadSubsetAsync(
                    resolution.RangeReader!,
                    registration.Bucket,
                    registration.RootPath,
                    metadata,
                    slice!.Plan.Request,
                    cancellationToken)
                .ConfigureAwait(false);
            var fillValue = TryConvertFillValue(slice.Plan.Array.FillValue);
            var png = ZarrTileRenderer.Render(
                subset,
                slice,
                request.OutputWidth,
                request.OutputHeight,
                colormap: null,
                fillValue: fillValue);
            var raster = new RasterResult
            {
                Data = png,
                ContentType = "image/png",
                Width = request.OutputWidth,
                Height = request.OutputHeight,
                Srid = metadata.Srid,
                Extent = bounds,
                BandCount = 1,
                PixelType = subset.DataType,
                GeoTransform =
                [
                    bounds.XMin,
                    (bounds.XMax - bounds.XMin) / request.OutputWidth,
                    0,
                    bounds.YMax,
                    0,
                    -(bounds.YMax - bounds.YMin) / request.OutputHeight,
                ],
            };

            activity?.SetTag("honua.output.bytes", png.Length);
            return Finish(
                ZarrRasterSliceReadStatus.Success,
                request.Selections.Count,
                variable,
                raster,
                activity: activity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException or OverflowException)
        {
            activity?.AddException(ex);
            return Finish(
                ZarrRasterSliceReadStatus.ReadFailed,
                request.Selections.Count,
                variable,
                error: "The multidimensional slice could not be read and rendered from its backing store.",
                activity: activity);
        }
    }

    private static bool TryValidateRequest(ZarrRasterSliceReadRequest request, out string? error)
    {
        error = null;
        if (request.OutputWidth is <= 0 or > MaxOutputDimension ||
            request.OutputHeight is <= 0 or > MaxOutputDimension ||
            (long)request.OutputWidth * request.OutputHeight > MaxOutputPixels)
        {
            error = $"Slice output dimensions must be between 1 and {MaxOutputDimension} with at most {MaxOutputPixels} pixels.";
            return false;
        }

        if (request.Bounds is { } bounds &&
            (!double.IsFinite(bounds.XMin) || !double.IsFinite(bounds.YMin) ||
             !double.IsFinite(bounds.XMax) || !double.IsFinite(bounds.YMax) ||
             bounds.XMax <= bounds.XMin || bounds.YMax <= bounds.YMin))
        {
            error = "Slice bounds must contain finite xmin,ymin,xmax,ymax values with positive width and height.";
            return false;
        }

        if (request.Selections.Count is 0 or > MaxSelections)
        {
            error = $"A raster-slice read requires between 1 and {MaxSelections} dimension selections.";
            return false;
        }

        var dimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in request.Selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Dimension) ||
                selection.Dimension.Length > MaxIdentifierLength ||
                selection.Variable?.Length > MaxIdentifierLength ||
                !double.IsFinite(selection.Coordinate))
            {
                error = "Slice variable, dimension, and coordinate values are invalid.";
                return false;
            }

            if (!dimensions.Add(selection.Dimension))
            {
                error = $"Dimension '{selection.Dimension}' may be selected only once.";
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveSelections(
        ZarrStoreMetadata metadata,
        IReadOnlyList<ZarrPointSliceSelection> selections,
        out string? variable,
        out (DateTimeOffset? Start, DateTimeOffset? End)? datetime,
        out int? verticalIndex,
        out string? error)
    {
        variable = selections
            .Select(static selection => selection.Variable)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
        variable = string.IsNullOrWhiteSpace(variable)
            ? metadata.PrimaryVariable ?? metadata.Arrays.FirstOrDefault()?.Name
            : variable;
        datetime = null;
        verticalIndex = null;
        error = null;

        var resolvedVariable = variable;
        var array = metadata.Arrays.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, resolvedVariable, StringComparison.Ordinal));
        if (array is null)
        {
            error = $"Variable '{variable}' is not available on this multidimensional coverage.";
            return false;
        }

        var nonSpatialNonTemporalDimensions = array.DimensionNames.Count(name =>
            !IsSpatialDimension(metadata, name) &&
            !IsTemporalDimension(metadata, name));
        if (nonSpatialNonTemporalDimensions > 1)
        {
            error = "Native Zarr slice export currently supports at most one non-temporal coordinate axis.";
            return false;
        }

        foreach (var selection in selections)
        {
            if (!string.IsNullOrWhiteSpace(selection.Variable) &&
                !string.Equals(selection.Variable, variable, StringComparison.Ordinal))
            {
                error = "All slice selections must target the same variable.";
                return false;
            }

            if (!ZarrDimensionSliceResolver.TryFindArrayDimension(array, selection.Dimension, out _))
            {
                error = $"Variable '{variable}' does not vary along dimension '{selection.Dimension}'.";
                return false;
            }

            if (IsSpatialDimension(metadata, selection.Dimension))
            {
                error = "Spatial dimensions must be selected with bbox, not multidimensionalDefinition.";
                return false;
            }

            if (!ZarrDimensionSliceResolver.TryResolveSliceIndex(
                    metadata,
                    selection.Dimension,
                    selection.Coordinate,
                    out var sliceIndex,
                    out error))
            {
                return false;
            }

            if (IsTemporalDimension(metadata, selection.Dimension))
            {
                try
                {
                    var instant = DateTimeOffset.FromUnixTimeMilliseconds((long)selection.Coordinate);
                    datetime = (instant, instant);
                }
                catch (ArgumentOutOfRangeException)
                {
                    error = "The selected time coordinate is outside the supported instant range.";
                    return false;
                }
            }
            else
            {
                verticalIndex = sliceIndex;
            }
        }

        return true;
    }

    private static bool IsSpatialDimension(ZarrStoreMetadata metadata, string dimension)
        => string.Equals(metadata.SpatialXDimension, dimension, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(metadata.SpatialYDimension, dimension, StringComparison.OrdinalIgnoreCase) ||
           (metadata.SpatialXDimension is null && dimension.ToLowerInvariant() is "x" or "lon" or "longitude") ||
           (metadata.SpatialYDimension is null && dimension.ToLowerInvariant() is "y" or "lat" or "latitude");

    private static bool IsTemporalDimension(ZarrStoreMetadata metadata, string dimension)
        => metadata.TemporalDimension is not null &&
           string.Equals(metadata.TemporalDimension, dimension, StringComparison.OrdinalIgnoreCase);

    private static bool Intersects(RasterExtent left, RasterExtent right)
        => left.XMin < right.XMax && left.XMax > right.XMin &&
           left.YMin < right.YMax && left.YMax > right.YMin;

    private static bool Contains(RasterExtent outer, RasterExtent inner)
        => inner.XMin >= outer.XMin && inner.XMax <= outer.XMax &&
           inner.YMin >= outer.YMin && inner.YMax <= outer.YMax;

    private static double? TryConvertFillValue(object? fillValue)
    {
        if (fillValue is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(fillValue, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static ZarrRasterSliceReadResult Finish(
        ZarrRasterSliceReadStatus status,
        int dimensionCount,
        string? variable = null,
        RasterResult? raster = null,
        string? error = null,
        Activity? activity = null)
    {
        activity?.SetTag("honua.slice.status", status.ToString());
        if (status == ZarrRasterSliceReadStatus.Success)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        else if (status == ZarrRasterSliceReadStatus.ReadFailed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }

        return new ZarrRasterSliceReadResult(status, raster, variable, dimensionCount, error);
    }
}
