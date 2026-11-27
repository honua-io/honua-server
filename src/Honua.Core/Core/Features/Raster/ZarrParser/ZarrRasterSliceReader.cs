// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster.ZarrParser;

public sealed class ZarrRasterSliceReader : IZarrRasterSliceReader
{
    private const int MaxOutputDimension = 4096;
    private const int MaxSelections = 8;
    private const int MaxIdentifierLength = 128;
    private const int WarpControlGridSize = 33;
    private const long MaxOutputPixels = 16L * 1024L * 1024L;
    private const long MaxSubsetBytes = 64L * 1024L * 1024L;
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Raster.Zarr");

    private readonly IZarrStore _zarrStore;
    private readonly ZarrSubsetReader _subsetReader;
    private readonly IReadOnlyList<ICloudRangeReader> _rangeReaders;

    public ZarrRasterSliceReader(
        IZarrStore zarrStore,
        ZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        ArgumentNullException.ThrowIfNull(rangeReaders);
        _rangeReaders = rangeReaders.ToArray();
    }

    public async Task<ZarrRasterSliceReadResult> ReadAsync(
        int layerId,
        ZarrRasterSliceReadRequest request,
        ICoordinateTransformService? coordinateTransform = null,
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
        var nativeSrid = metadata.Srid;
        var inputSrid = request.InputSrid ?? nativeSrid;
        var outputSrid = request.OutputSrid ?? inputSrid;

        // Reprojection is required when the request window is expressed in a non-native CRS,
        // or the caller asked for a non-native output CRS. Without a transform service the
        // reader can only serve the native grid, so a reprojection request is rejected the
        // same way a non-native input SR always was.
        var needsReprojection = (inputSrid != nativeSrid && request.Bounds is not null) || outputSrid != nativeSrid;
        if (needsReprojection && coordinateTransform is null)
        {
            return Finish(
                ZarrRasterSliceReadStatus.InvalidSelection,
                request.Selections.Count,
                error: $"Slice bounds must use the coverage spatial reference EPSG:{nativeSrid}; reprojection is not supported.",
                activity: activity);
        }

        RasterExtent outputExtent;
        RasterExtent nativeReadBounds;
        if (!needsReprojection)
        {
            var requestedBounds = request.Bounds ?? metadata.Extent;
            if (!Intersects(requestedBounds, metadata.Extent))
            {
                return Finish(
                    ZarrRasterSliceReadStatus.OutsideCoverage,
                    request.Selections.Count,
                    error: "The requested bounds do not intersect the coverage extent.",
                    activity: activity);
            }

            // Clamp an over-extent trim to the intersection with the coverage extent instead
            // of requiring full containment. This matches the plain IRasterStore GetCoverage
            // path (over-extent trims answer the intersection, exercised by CITE) and lets a
            // client that echoes the DescribeCoverage-advertised extent — which can round a
            // hair outside the Zarr metadata extent — read the intersection rather than 404.
            nativeReadBounds = Clamp(requestedBounds, metadata.Extent);
            outputExtent = nativeReadBounds;
        }
        else
        {
            var requestWindow = request.Bounds ?? metadata.Extent;
            var windowSrid = request.Bounds is not null ? inputSrid : nativeSrid;
            var projectedOutput = await TransformExtentAsync(
                    coordinateTransform!, requestWindow, windowSrid, outputSrid, cancellationToken)
                .ConfigureAwait(false);
            if (projectedOutput is not { } computedOutput)
            {
                return Finish(
                    ZarrRasterSliceReadStatus.InvalidSelection,
                    request.Selections.Count,
                    error: $"The requested output spatial reference EPSG:{outputSrid} could not be resolved for reprojection.",
                    activity: activity);
            }

            outputExtent = computedOutput;
            var projectedNative = await TransformExtentAsync(
                    coordinateTransform!, outputExtent, outputSrid, nativeSrid, cancellationToken)
                .ConfigureAwait(false);
            if (projectedNative is not { } computedNative)
            {
                return Finish(
                    ZarrRasterSliceReadStatus.InvalidSelection,
                    request.Selections.Count,
                    error: $"The requested window could not be reprojected into the coverage spatial reference EPSG:{nativeSrid}.",
                    activity: activity);
            }

            if (!Intersects(computedNative, metadata.Extent))
            {
                return Finish(
                    ZarrRasterSliceReadStatus.OutsideCoverage,
                    request.Selections.Count,
                    error: "The requested bounds do not intersect the coverage extent.",
                    activity: activity);
            }

            nativeReadBounds = Clamp(computedNative, metadata.Extent);
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
        activity?.SetTag("honua.output.srid", outputSrid);
        activity?.SetTag("honua.output.resampling", request.Resampling.ToString());
        activity?.SetTag(
            "honua.slice.dimensions",
            string.Join(',', request.Selections.Select(static selection => selection.Dimension)));

        var tileBounds = new ZarrTileBounds(
            nativeReadBounds.XMin, nativeReadBounds.YMin, nativeReadBounds.XMax, nativeReadBounds.YMax);
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
                    new ZarrSubsetReadParameters
                    {
                        Reader = resolution.RangeReader!,
                        Bucket = registration.Bucket,
                        RootPath = registration.RootPath,
                        Metadata = metadata,
                        Request = slice!.Plan.Request,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var fillValue = slice.Plan.Array.FillValue;
            var stretchRange = ResolveStretchRange(request.Stretch);
            var rgba = await RenderSliceRgbaAsync(
                    new SliceRenderContext
                    {
                        Subset = subset,
                        Slice = slice,
                        OutputExtent = outputExtent,
                        OutputSrid = outputSrid,
                        NativeSrid = nativeSrid,
                        Request = request,
                        StretchRange = stretchRange,
                        FillValue = fillValue,
                    },
                    coordinateTransform,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rgba is null)
            {
                return Finish(
                    ZarrRasterSliceReadStatus.InvalidSelection,
                    request.Selections.Count,
                    variable,
                    error: $"The slice could not be reprojected from EPSG:{outputSrid} to the coverage spatial reference EPSG:{nativeSrid}.",
                    activity: activity);
            }
            var png = PngEncoder.Encode(rgba, request.OutputWidth, request.OutputHeight);
            var raster = new RasterResult
            {
                Data = png,
                ContentType = "image/png",
                Width = request.OutputWidth,
                Height = request.OutputHeight,
                Srid = outputSrid,
                Extent = outputExtent,
                BandCount = 1,
                PixelType = subset.DataType,
                GeoTransform =
                [
                    outputExtent.XMin,
                    (outputExtent.XMax - outputExtent.XMin) / request.OutputWidth,
                    0,
                    outputExtent.YMax,
                    0,
                    -(outputExtent.YMax - outputExtent.YMin) / request.OutputHeight,
                ],
            };

            activity?.SetTag("honua.output.bytes", png.Length);
            return Finish(
                ZarrRasterSliceReadStatus.Success,
                request.Selections.Count,
                variable,
                raster,
                activity: activity,
                rgba: rgba);
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

    // Reprojects an extent between spatial references, returning it unchanged when the
    // source and target SRIDs match (identity) so the native path never touches the
    // authoritative transform service. Returns null when the transform cannot be performed.
    private static async Task<RasterExtent?> TransformExtentAsync(
        ICoordinateTransformService transform,
        RasterExtent extent,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        if (fromSrid == toSrid)
        {
            return extent with { Srid = toSrid };
        }

        var result = await transform
            .TransformExtentAsync(extent.XMin, extent.YMin, extent.XMax, extent.YMax, fromSrid, toSrid, cancellationToken)
            .ConfigureAwait(false);
        if (result is not { } bounds)
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = bounds.MinX,
            YMin = bounds.MinY,
            XMax = bounds.MaxX,
            YMax = bounds.MaxY,
            Srid = toSrid,
        };
    }

    // Renders output RGBA using separable 1D axis transforms for identity / 4326<->3857 (zero 2D
    // coordinate grid allocation) and a bounded 33x33 bilinear control grid for general CRS warps.
    private static Task<byte[]?> RenderSliceRgbaAsync(
        SliceRenderContext context,
        ICoordinateTransformService? coordinateTransform,
        CancellationToken cancellationToken)
    {
        if (IsIdentityOrMercatorAlias(context.OutputSrid, context.NativeSrid)
            || IsSeparable4326And3857(context.OutputSrid, context.NativeSrid))
        {
            return Task.FromResult<byte[]?>(RenderSeparableSliceRgba(context));
        }

        return RenderControlGridSliceRgbaAsync(context, coordinateTransform!, cancellationToken);
    }

    private static byte[] RenderSeparableSliceRgba(SliceRenderContext context)
    {
        var axes = BuildSeparableAxes(context.OutputExtent, context.Request.OutputWidth, context.Request.OutputHeight);
        TransformSeparableAxesIfNeeded(axes, context.OutputSrid, context.NativeSrid);
        return ZarrTileRenderer.RenderSeparableRgbaWithTypedFill(
            context.Subset,
            context.Slice,
            new ZarrTileRenderer.SeparableRenderRequest
            {
                AxisX = axes.AxisX,
                AxisY = axes.AxisY,
                Resampling = context.Request.Resampling,
                Colormap = context.Request.Colormap,
                StretchRange = context.StretchRange,
                FillValue = context.FillValue,
            });
    }

    private static async Task<byte[]?> RenderControlGridSliceRgbaAsync(
        SliceRenderContext context,
        ICoordinateTransformService coordinateTransform,
        CancellationToken cancellationToken)
    {
        var spec = new ControlGridSpec
        {
            Width = context.Request.OutputWidth,
            Height = context.Request.OutputHeight,
            Cols = Math.Min(context.Request.OutputWidth, WarpControlGridSize),
            Rows = Math.Min(context.Request.OutputHeight, WarpControlGridSize),
        };
        var (controlX, controlY) = BuildControlGrid(context.OutputExtent, spec);
        var warped = await coordinateTransform
            .TransformPointsAsync(controlX, controlY, context.OutputSrid, context.NativeSrid, cancellationToken)
            .ConfigureAwait(false);
        if (!warped)
        {
            return null;
        }

        // Antimeridian or polar discontinuities in geographic longitude cannot be bilinearly
        // interpolated between control points; fall back to exact per-pixel coordinate transforms.
        if (context.NativeSrid == 4326 && HasLongitudeDiscontinuity(controlX, spec))
        {
            return await RenderExactTransformedSliceRgbaAsync(context, coordinateTransform, cancellationToken)
                .ConfigureAwait(false);
        }

        return ZarrTileRenderer.RenderControlGridRgbaWithTypedFill(
            context.Subset,
            context.Slice,
            CreateControlGridRenderRequest(context, spec, (controlX, controlY)));
    }

    private static ZarrTileRenderer.ControlGridRenderRequest CreateControlGridRenderRequest(
        SliceRenderContext context,
        ControlGridSpec spec,
        (double[] ControlX, double[] ControlY) controls)
        => new()
        {
            OutputWidth = spec.Width,
            OutputHeight = spec.Height,
            ControlCols = spec.Cols,
            ControlRows = spec.Rows,
            ControlX = controls.ControlX,
            ControlY = controls.ControlY,
            Resampling = context.Request.Resampling,
            Colormap = context.Request.Colormap,
            StretchRange = context.StretchRange,
            FillValue = context.FillValue,
        };

    private static async Task<byte[]?> RenderExactTransformedSliceRgbaAsync(
        SliceRenderContext context,
        ICoordinateTransformService coordinateTransform,
        CancellationToken cancellationToken)
    {
        var width = context.Request.OutputWidth;
        var height = context.Request.OutputHeight;
        var (sampleX, sampleY) = BuildSampleGrid(context.OutputExtent, width, height);
        var warped = await coordinateTransform
            .TransformPointsAsync(sampleX, sampleY, context.OutputSrid, context.NativeSrid, cancellationToken)
            .ConfigureAwait(false);
        if (!warped)
        {
            return null;
        }

        return ZarrTileRenderer.RenderRgbaWithTypedFill(
            context.Subset,
            context.Slice,
            width,
            height,
            sampleX,
            sampleY,
            context.Request.Resampling,
            context.Request.Colormap,
            context.StretchRange,
            context.FillValue);
    }

    private static bool HasLongitudeDiscontinuity(double[] controlX, ControlGridSpec spec)
    {
        for (var r = 0; r < spec.Rows; r++)
        {
            if (RowHasLongitudeDiscontinuity(controlX, r, spec))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RowHasLongitudeDiscontinuity(double[] controlX, int row, ControlGridSpec spec)
    {
        var rowOffset = row * spec.Cols;
        var hasNextRow = row + 1 < spec.Rows;
        for (var c = 0; c < spec.Cols; c++)
        {
            var current = controlX[rowOffset + c];
            if (c + 1 < spec.Cols && IsLongitudeJump(current, controlX[rowOffset + c + 1]))
            {
                return true;
            }

            if (hasNextRow && IsLongitudeJump(current, controlX[rowOffset + spec.Cols + c]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLongitudeJump(double first, double second)
        => double.IsFinite(first) && double.IsFinite(second) && Math.Abs(first - second) >= 180.0;

    private static (double[] SampleX, double[] SampleY) BuildSampleGrid(RasterExtent extent, int width, int height)
    {
        var sampleX = new double[checked(width * height)];
        var sampleY = new double[sampleX.Length];
        var spanX = extent.XMax - extent.XMin;
        var spanY = extent.YMax - extent.YMin;

        for (var py = 0; py < height; py++)
        {
            var wy = extent.YMax - (((py + 0.5) / height) * spanY);
            var rowOffset = py * width;
            for (var px = 0; px < width; px++)
            {
                var idx = rowOffset + px;
                sampleX[idx] = extent.XMin + (((px + 0.5) / width) * spanX);
                sampleY[idx] = wy;
            }
        }

        return (sampleX, sampleY);
    }

    private static bool IsIdentityOrMercatorAlias(int fromSrid, int toSrid)
        => fromSrid == toSrid
           || (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid));

    private static bool IsSeparable4326And3857(int fromSrid, int toSrid)
        => (fromSrid == 4326 && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
           || (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && toSrid == 4326);

    private static SeparableAxes BuildSeparableAxes(RasterExtent extent, int width, int height)
    {
        var axisX = new double[width];
        var axisY = new double[height];
        var spanX = extent.XMax - extent.XMin;
        var spanY = extent.YMax - extent.YMin;
        for (var px = 0; px < width; px++)
        {
            axisX[px] = extent.XMin + (((px + 0.5) / width) * spanX);
        }

        for (var py = 0; py < height; py++)
        {
            axisY[py] = extent.YMax - (((py + 0.5) / height) * spanY);
        }

        return new SeparableAxes(axisX, axisY);
    }

    private static void TransformSeparableAxesIfNeeded(SeparableAxes axes, int fromSrid, int toSrid)
    {
        if (IsIdentityOrMercatorAlias(fromSrid, toSrid))
        {
            return;
        }

        if (fromSrid == 4326)
        {
            for (var i = 0; i < axes.AxisX.Length; i++)
            {
                axes.AxisX[i] = WebMercatorMath.LonLatToWebMercator(axes.AxisX[i], 0).X;
            }

            for (var i = 0; i < axes.AxisY.Length; i++)
            {
                axes.AxisY[i] = WebMercatorMath.LonLatToWebMercator(0, axes.AxisY[i]).Y;
            }

            return;
        }

        for (var i = 0; i < axes.AxisX.Length; i++)
        {
            axes.AxisX[i] = WebMercatorMath.WebMercatorToLonLat(axes.AxisX[i], 0).Lon;
        }

        for (var i = 0; i < axes.AxisY.Length; i++)
        {
            axes.AxisY[i] = WebMercatorMath.WebMercatorToLonLat(0, axes.AxisY[i]).Lat;
        }
    }

    // Builds a coarse control grid whose corners sit on the exact first and last output pixel centres.
    private static (double[] ControlX, double[] ControlY) BuildControlGrid(RasterExtent extent, ControlGridSpec spec)
    {
        var xs = new double[checked(spec.Cols * spec.Rows)];
        var ys = new double[xs.Length];
        var spanX = extent.XMax - extent.XMin;
        var spanY = extent.YMax - extent.YMin;
        var stepX = spec.Cols > 1 ? (double)(spec.Width - 1) / (spec.Cols - 1) : 0.0;
        var stepY = spec.Rows > 1 ? (double)(spec.Height - 1) / (spec.Rows - 1) : 0.0;

        for (var r = 0; r < spec.Rows; r++)
        {
            var cy = extent.YMax - ((((r * stepY) + 0.5) / spec.Height) * spanY);
            var rowOffset = r * spec.Cols;
            for (var c = 0; c < spec.Cols; c++)
            {
                var index = rowOffset + c;
                xs[index] = extent.XMin + ((((c * stepX) + 0.5) / spec.Width) * spanX);
                ys[index] = cy;
            }
        }

        return (xs, ys);
    }

    private readonly record struct SeparableAxes(double[] AxisX, double[] AxisY);

    private readonly record struct ControlGridSpec
    {
        public required int Width { get; init; }

        public required int Height { get; init; }

        public required int Cols { get; init; }

        public required int Rows { get; init; }
    }

    private sealed class SliceRenderContext
    {
        public required ZarrSubsetResult Subset { get; init; }

        public required ZarrTileSlicePlan Slice { get; init; }

        public required RasterExtent OutputExtent { get; init; }

        public required int OutputSrid { get; init; }

        public required int NativeSrid { get; init; }

        public required ZarrRasterSliceReadRequest Request { get; init; }

        public (double Min, double Max)? StretchRange { get; init; }

        public object? FillValue { get; init; }
    }

    // Derives an explicit display range from a rendering-rule stretch. Explicit per-band
    // statistics (Esri Statistics on the first selected band) are honoured directly; other
    // stretch types fall back to the renderer's auto min/max ramp (null), which equals the
    // MinMax stretch over the slice.
    private static (double Min, double Max)? ResolveStretchRange(RasterStretch? stretch)
    {
        if (stretch is not { } value)
        {
            return null;
        }

        if (value.StatisticsMin is { Length: > 0 } mins &&
            value.StatisticsMax is { Length: > 0 } maxes &&
            double.IsFinite(mins[0]) && double.IsFinite(maxes[0]) && maxes[0] > mins[0])
        {
            return (mins[0], maxes[0]);
        }

        return null;
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

        foreach (var requested in selections)
        {
            var selection = requested with
            {
                Dimension = ZarrDimensionSliceResolver.ResolveAdvertisedDimensionName(metadata, requested.Dimension)
            };
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

    // Intersect the requested bounds with the coverage extent. Callers guarantee a
    // non-empty overlap (via Intersects), so the clamped envelope keeps positive width
    // and height.
    private static RasterExtent Clamp(RasterExtent bounds, RasterExtent extent)
        => bounds with
        {
            XMin = Math.Max(bounds.XMin, extent.XMin),
            YMin = Math.Max(bounds.YMin, extent.YMin),
            XMax = Math.Min(bounds.XMax, extent.XMax),
            YMax = Math.Min(bounds.YMax, extent.YMax),
        };

    private static ZarrRasterSliceReadResult Finish(
        ZarrRasterSliceReadStatus status,
        int dimensionCount,
        string? variable = null,
        RasterResult? raster = null,
        string? error = null,
        Activity? activity = null,
        byte[]? rgba = null)
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

        return new ZarrRasterSliceReadResult(status, raster, variable, dimensionCount, error, rgba);
    }
}
