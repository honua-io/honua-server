// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster.ZarrParser;

// Renders output RGBA using separable 1D axis transforms for identity / 4326<->3857 (zero 2D
// coordinate grid allocation), a bounded 33x33 bilinear control grid for general CRS warps, and
// an exact per-pixel fallback whenever geographic longitudes jump across the antimeridian/poles.
internal static class ZarrRasterSliceWarper
{
    private const int WarpControlGridSize = 33;

    public static Task<byte[]?> RenderSliceRgbaAsync(
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

    internal sealed class SliceRenderContext
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
}
