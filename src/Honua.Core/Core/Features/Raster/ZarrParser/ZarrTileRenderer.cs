// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public static class ZarrTileRenderer
{
    public const int DefaultTileSize = 256;

    public static byte[] Render(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        int tileSize = DefaultTileSize,
        RasterColormap? colormap = null,
        double? fillValue = null)
        => Render(result, slice, tileSize, tileSize, colormap, fillValue);

    public static byte[] Render(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        int outputWidth,
        int outputHeight,
        RasterColormap? colormap = null,
        double? fillValue = null)
        => RenderWithTypedFill(result, slice, outputWidth, outputHeight, colormap, fillValue);

    public static byte[] RenderWithTypedFill(
        ZarrSubsetResult result, ZarrTileSlicePlan slice, int tileSize = DefaultTileSize,
        RasterColormap? colormap = null, object? fillValue = null)
        => RenderWithTypedFill(result, slice, tileSize, tileSize, colormap, fillValue);

    public static byte[] RenderWithTypedFill(
        ZarrSubsetResult result, ZarrTileSlicePlan slice, int outputWidth, int outputHeight,
        RasterColormap? colormap = null, object? fillValue = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);

        var layout = ResolveGridLayout(result, slice);
        var pixelByteCount = checked(outputWidth * outputHeight * 4);
        var valuesPool = ArrayPool<double>.Shared.Rent(layout.ValueCount);
        var pixelsPool = ArrayPool<byte>.Shared.Rent(pixelByteCount);
        try
        {
            var values = valuesPool.AsSpan(0, layout.ValueCount);
            DecodeBufferInto(result, fillValue, values);

            var grid = CreateDecodedGrid(values, layout);
            var target = new NearestRenderTarget
            {
                Pixels = pixelsPool.AsSpan(0, pixelByteCount),
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Colormap = colormap,
                Ramp = colormap is null ? ResolveRange(values) : default,
            };
            RenderNearestGrid(grid, slice, target);
            return PngEncoder.Encode(pixelsPool, outputWidth, outputHeight);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(valuesPool);
            ArrayPool<byte>.Shared.Return(pixelsPool);
        }
    }

    public static byte[] RenderRgbaWithTypedFill(
        ZarrSubsetResult result, ZarrTileSlicePlan slice, int outputWidth, int outputHeight,
        double[] sampleX, double[] sampleY, ResamplingAlgorithm resampling, RasterColormap? colormap,
        (double Min, double Max)? stretchRange, object? fillValue)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(sampleX);
        ArgumentNullException.ThrowIfNull(sampleY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        var outputPixelCount = checked(outputWidth * outputHeight);
        if (sampleX.Length != outputPixelCount || sampleY.Length != outputPixelCount)
            throw new ArgumentException("Sample coordinate buffers must match the output pixel count.", nameof(sampleX));

        var layout = ResolveGridLayout(result, slice);
        var valuesPool = ArrayPool<double>.Shared.Rent(layout.ValueCount);
        try
        {
            var values = valuesPool.AsSpan(0, layout.ValueCount);
            DecodeBufferInto(result, fillValue, values);
            var range = stretchRange ?? ResolveRange(values);
            var request = new RgbaSampleRequest
            {
                OutputPixelCount = outputPixelCount,
                SampleX = sampleX,
                SampleY = sampleY,
                Resampling = resampling,
                Colormap = colormap,
                Lo = range.Min,
                Hi = range.Max > range.Min ? range.Max : range.Min + 1,
            };
            return RenderSampledRgba(CreateDecodedGrid(values, layout), slice, request);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(valuesPool);
        }
    }

    private static GridLayout ResolveGridLayout(ZarrSubsetResult result, ZarrTileSlicePlan slice)
    {
        var height = result.Shape[slice.YDimensionIndex];
        var width = result.Shape[slice.XDimensionIndex];
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Zarr tile slice has no spatial extent.");
        }

        // Every non-spatial axis is length 1, so cell (y, x) maps to y*strideY + x*strideX.
        var (strideY, strideX) = ComputeSpatialStrides(result.Shape, slice.YDimensionIndex, slice.XDimensionIndex);
        var dtype = Normalize(result.DataType);
        var elementSize = ElementSize(dtype, result.DataType);
        return new GridLayout
        {
            Width = width,
            Height = height,
            StrideX = strideX,
            StrideY = strideY,
            ValueCount = result.Data.Length / elementSize,
        };
    }

    private static DecodedGrid CreateDecodedGrid(ReadOnlySpan<double> values, GridLayout layout)
        => new()
        {
            Values = values,
            Width = layout.Width,
            Height = layout.Height,
            StrideX = layout.StrideX,
            StrideY = layout.StrideY,
        };

    private static void RenderNearestGrid(DecodedGrid grid, ZarrTileSlicePlan slice, NearestRenderTarget target)
    {
        for (var py = 0; py < target.OutputHeight; py++)
        {
            // Output images are north-up; reverse the row lookup when source rows run south-to-north.
            var displayRow = grid.Height == 1 ? 0 : (int)((long)py * grid.Height / target.OutputHeight);
            var gy = slice.YAxisAscending ? grid.Height - 1 - displayRow : displayRow;
            if (gy >= grid.Height)
            {
                gy = grid.Height - 1;
            }

            for (var px = 0; px < target.OutputWidth; px++)
            {
                var gx = grid.Width == 1 ? 0 : (int)((long)px * grid.Width / target.OutputWidth);
                if (gx >= grid.Width)
                {
                    gx = grid.Width - 1;
                }

                var index = (gy * grid.StrideY) + (gx * grid.StrideX);
                var value = index >= 0 && index < grid.Values.Length ? grid.Values[index] : double.NaN;
                var offset = ((py * target.OutputWidth) + px) * 4;
                WritePixel(target.Pixels.Slice(offset, 4), value, target.Colormap, target.Ramp);
            }
        }
    }

    private static byte[] RenderSampledRgba(DecodedGrid grid, ZarrTileSlicePlan slice, RgbaSampleRequest request)
    {
        var pixels = new byte[checked(request.OutputPixelCount * 4)];
        for (var index = 0; index < request.SampleX.Length; index++)
        {
            var query = new SampleQuery
            {
                X = request.SampleX[index],
                Y = request.SampleY[index],
                Resampling = request.Resampling,
            };
            var value = Sample(grid, slice, query);
            WriteStretchedPixel(pixels.AsSpan(index * 4), value, request.Colormap, request.Lo, request.Hi);
        }

        return pixels;
    }

    // Samples the decoded value grid at a native (storage-CRS) coordinate using the requested
    // resampling. Missing (NaN / fill) centres short-circuit to NaN so they render transparent;
    // non-finite neighbours in an interpolation kernel fall back to the centre value so an edge
    // pixel never smears NoData into valid output.
    private static double Sample(DecodedGrid grid, ZarrTileSlicePlan slice, SampleQuery query)
    {
        // Fractional cell-centre position of the native coordinate within the subset grid.
        var fx = ((query.X - slice.GridXMin) / slice.CellWidth) - 0.5;
        var fy = slice.YAxisAscending
            ? ((query.Y - (slice.GridYMax - (grid.Height * slice.CellHeight))) / slice.CellHeight) - 0.5
            : ((slice.GridYMax - query.Y) / slice.CellHeight) - 0.5;

        var centre = grid.ValueAt((int)Math.Round(fx), (int)Math.Round(fy));
        if (query.Resampling == ResamplingAlgorithm.NearestNeighbor || double.IsNaN(centre))
        {
            return centre;
        }

        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        if (query.Resampling == ResamplingAlgorithm.Bilinear)
        {
            var v00 = Coalesce(grid.ValueAt(x0, y0), centre);
            var v10 = Coalesce(grid.ValueAt(x0 + 1, y0), centre);
            var v01 = Coalesce(grid.ValueAt(x0, y0 + 1), centre);
            var v11 = Coalesce(grid.ValueAt(x0 + 1, y0 + 1), centre);
            var top = v00 + ((v10 - v00) * tx);
            var bottom = v01 + ((v11 - v01) * tx);
            return top + ((bottom - top) * ty);
        }

        // Bicubic (and Lanczos, approximated as bicubic) via separable Catmull-Rom.
        var rowContext = new RowSampleContext { Tx = tx, Centre = centre };
        var c0 = grid.SampleRow(x0, y0 - 1, rowContext);
        var c1 = grid.SampleRow(x0, y0, rowContext);
        var c2 = grid.SampleRow(x0, y0 + 1, rowContext);
        var c3 = grid.SampleRow(x0, y0 + 2, rowContext);
        return CatmullRom(c0, c1, c2, c3, ty);
    }

    private static double Coalesce(double value, double fallback)
        => double.IsNaN(value) ? fallback : value;

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5 * (
            (2 * p1) +
            ((-p0 + p2) * t) +
            (((2 * p0) - (5 * p1) + (4 * p2) - p3) * t2) +
            ((-p0 + (3 * p1) - (3 * p2) + p3) * t3));
    }

    // Colours one pixel: NaN/fill -> transparent; otherwise normalise the value across the
    // display range, then either sample the colormap spread across its stop domain or emit a
    // grayscale ramp.
    private static void WriteStretchedPixel(
        Span<byte> pixel,
        double value,
        RasterColormap? colormap,
        double lo,
        double hi)
    {
        if (!double.IsFinite(value))
        {
            pixel[0] = 0;
            pixel[1] = 0;
            pixel[2] = 0;
            pixel[3] = 0;
            return;
        }

        var t = Math.Clamp((value - lo) / (hi - lo), 0.0, 1.0);

        if (colormap is not null && colormap.Entries.Count > 0)
        {
            var first = colormap.Entries[0].Value;
            var last = colormap.Entries[^1].Value;
            var colorValue = first + (t * (last - first));
            var (r, g, b, a) = SampleColormap(colormap, colorValue);
            pixel[0] = r;
            pixel[1] = g;
            pixel[2] = b;
            pixel[3] = a;
            return;
        }

        var gray = (byte)Math.Clamp((int)Math.Round(t * 255.0), 0, 255);
        pixel[0] = gray;
        pixel[1] = gray;
        pixel[2] = gray;
        pixel[3] = 255;
    }

    internal static (int StrideY, int StrideX) ComputeSpatialStrides(int[] shape, int yDim, int xDim)
    {
        var strideY = 1;
        for (var i = yDim + 1; i < shape.Length; i++)
        {
            strideY *= shape[i];
        }

        var strideX = 1;
        for (var i = xDim + 1; i < shape.Length; i++)
        {
            strideX *= shape[i];
        }

        return (strideY, strideX);
    }

    private static void DecodeBufferInto(ZarrSubsetResult result, object? fillValue, Span<double> destination)
    {
        var spec = new ZarrBufferDecodeSpec
        {
            NormalizedDtype = Normalize(result.DataType),
            FillValue = fillValue,
        };
        if (!ZarrValueDecoder.TryDecodeBufferWithFill(result.Data, spec, destination))
        {
            throw new InvalidOperationException($"Zarr dtype '{result.DataType}' is not supported for tile rendering.");
        }
    }

    private static (double Min, double Max) ResolveRange(ReadOnlySpan<double> values)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var value in values)
        {
            // Source-typed nodata was already masked before decoding.
            if (!double.IsFinite(value))
            {
                continue;
            }
            if (value < min)
            {
                min = value;
            }
            if (value > max)
            {
                max = value;
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return (0, 1);
        }
        return max > min ? (min, max) : (min, min + 1);
    }

    private static void WritePixel(
        Span<byte> pixel,
        double value,
        RasterColormap? colormap,
        (double Min, double Max) ramp)
    {
        // Source-typed nodata was already masked before decoding.
        if (!double.IsFinite(value))
        {
            pixel[0] = 0;
            pixel[1] = 0;
            pixel[2] = 0;
            pixel[3] = 0;
            return;
        }

        if (colormap is not null)
        {
            var (r, g, b, a) = SampleColormap(colormap, value);
            pixel[0] = r;
            pixel[1] = g;
            pixel[2] = b;
            pixel[3] = a;
            return;
        }

        var normalized = (value - ramp.Min) / (ramp.Max - ramp.Min);
        var gray = (byte)Math.Clamp((int)Math.Round(normalized * 255.0), 0, 255);
        pixel[0] = gray;
        pixel[1] = gray;
        pixel[2] = gray;
        pixel[3] = 255;
    }

    private static (byte R, byte G, byte B, byte A) SampleColormap(RasterColormap colormap, double value)
    {
        var entries = colormap.Entries;
        if (entries.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        if (value <= entries[0].Value)
        {
            var first = entries[0];
            return (first.Red, first.Green, first.Blue, first.Alpha);
        }

        var last = entries[^1];
        if (value >= last.Value)
        {
            return (last.Red, last.Green, last.Blue, last.Alpha);
        }

        for (var i = 1; i < entries.Count; i++)
        {
            var hi = entries[i];
            if (value > hi.Value)
            {
                continue;
            }

            var lo = entries[i - 1];
            var span = hi.Value - lo.Value;
            var t = span <= 0 ? 0 : (value - lo.Value) / span;
            return (
                Lerp(lo.Red, hi.Red, t),
                Lerp(lo.Green, hi.Green, t),
                Lerp(lo.Blue, hi.Blue, t),
                Lerp(lo.Alpha, hi.Alpha, t));
        }

        return (last.Red, last.Green, last.Blue, last.Alpha);
    }

    private static byte Lerp(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);

    private static int ElementSize(string normalized, string original) => normalized switch
    {
        "i1" or "u1" or "b1" => 1,
        "i2" or "u2" => 2,
        "f4" or "i4" or "u4" => 4,
        "f8" or "i8" or "u8" => 8,
        _ => throw new InvalidOperationException($"Zarr dtype '{original}' is not supported for tile rendering.")
    };

    internal static string Normalize(string dtype)
        => dtype.Length > 0 && dtype[0] is '<' or '|' or '=' ? dtype[1..] : dtype;

    private readonly record struct GridLayout
    {
        public required int Width { get; init; }

        public required int Height { get; init; }

        public required int StrideX { get; init; }

        public required int StrideY { get; init; }

        public required int ValueCount { get; init; }
    }

    private readonly ref struct DecodedGrid
    {
        public ReadOnlySpan<double> Values { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int StrideX { get; init; }

        public int StrideY { get; init; }

        public double ValueAt(int gx, int gy)
        {
            gx = Math.Clamp(gx, 0, Width - 1);
            gy = Math.Clamp(gy, 0, Height - 1);
            var idx = (gy * StrideY) + (gx * StrideX);
            var v = idx >= 0 && idx < Values.Length ? Values[idx] : double.NaN;
            return !double.IsFinite(v) ? double.NaN : v;
        }

        public double SampleRow(int x0, int gy, RowSampleContext context)
        {
            var r0 = Coalesce(ValueAt(x0 - 1, gy), context.Centre);
            var r1 = Coalesce(ValueAt(x0, gy), context.Centre);
            var r2 = Coalesce(ValueAt(x0 + 1, gy), context.Centre);
            var r3 = Coalesce(ValueAt(x0 + 2, gy), context.Centre);
            return CatmullRom(r0, r1, r2, r3, context.Tx);
        }
    }

    private readonly ref struct NearestRenderTarget
    {
        public Span<byte> Pixels { get; init; }

        public int OutputWidth { get; init; }

        public int OutputHeight { get; init; }

        public RasterColormap? Colormap { get; init; }

        public (double Min, double Max) Ramp { get; init; }
    }

    private sealed class RgbaSampleRequest
    {
        public required int OutputPixelCount { get; init; }

        public required double[] SampleX { get; init; }

        public required double[] SampleY { get; init; }

        public required ResamplingAlgorithm Resampling { get; init; }

        public RasterColormap? Colormap { get; init; }

        public required double Lo { get; init; }

        public required double Hi { get; init; }
    }

    private readonly record struct SampleQuery
    {
        public required double X { get; init; }

        public required double Y { get; init; }

        public required ResamplingAlgorithm Resampling { get; init; }
    }

    private readonly record struct RowSampleContext
    {
        public required double Tx { get; init; }

        public required double Centre { get; init; }
    }
}
