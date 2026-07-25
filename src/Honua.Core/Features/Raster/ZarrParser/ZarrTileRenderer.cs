// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Renders a single-band Zarr coverage slice to an RGBA PNG map tile. AOT-safe and
/// dependency-light: decodes the little-endian subset buffer into a 2D value grid,
/// maps each value to a colour (linear colormap interpolation, or a grayscale ramp
/// over the slice min/max when no colormap is supplied), nearest-samples the grid
/// into the requested tile pixel dimensions, and encodes a PNG with the managed
/// DEFLATE encoder. NaN / fill values render fully transparent.
/// </summary>
public static class ZarrTileRenderer
{
    /// <summary>Default tile edge length in pixels.</summary>
    public const int DefaultTileSize = 256;

    /// <summary>
    /// Renders a resolved tile slice to a PNG.
    /// </summary>
    /// <param name="result">Decoded subset payload from <see cref="Abstractions.IZarrSubsetReader"/>.</param>
    /// <param name="slice">Resolved slice plan describing the X/Y dimension positions.</param>
    /// <param name="tileSize">Output tile edge length in pixels.</param>
    /// <param name="colormap">Optional colormap; grayscale auto-ramp is used when null.</param>
    /// <param name="fillValue">Optional source fill value rendered transparent.</param>
    /// <returns>PNG-encoded RGBA tile.</returns>
    public static byte[] Render(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        int tileSize = DefaultTileSize,
        RasterColormap? colormap = null,
        double? fillValue = null)
        => Render(result, slice, tileSize, tileSize, colormap, fillValue);

    /// <summary>
    /// Renders a resolved slice to a rectangular PNG using bounded nearest-neighbor sampling.
    /// </summary>
    /// <param name="result">Decoded subset payload from <see cref="Abstractions.IZarrSubsetReader"/>.</param>
    /// <param name="slice">Resolved slice plan describing the X/Y dimension positions.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="colormap">Optional colormap; grayscale auto-ramp is used when null.</param>
    /// <param name="fillValue">Optional source fill value rendered transparent.</param>
    /// <returns>PNG-encoded RGBA image.</returns>
    public static byte[] Render(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        int outputWidth,
        int outputHeight,
        RasterColormap? colormap = null,
        double? fillValue = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);

        var height = result.Shape[slice.YDimensionIndex];
        var width = result.Shape[slice.XDimensionIndex];
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Zarr tile slice has no spatial extent.");
        }

        // The decoded buffer is row-major over the full subset shape. Every
        // non-spatial axis is length 1, so the linear position of grid cell (y, x)
        // is y*strideY + x*strideX where the strides are the products of the trailing
        // subset dimensions; computing them explicitly keeps the mapping correct
        // regardless of where the Y and X axes sit in the dimension order.
        var (strideY, strideX) = ComputeSpatialStrides(result.Shape, slice.YDimensionIndex, slice.XDimensionIndex);
        var values = DecodeBuffer(result);

        // Build the value->colour mapping. With a colormap, interpolate between stops;
        // otherwise auto-ramp grayscale over the finite value range.
        var ramp = colormap is null ? ResolveRange(values, fillValue) : default;

        var pixels = new byte[checked(outputWidth * outputHeight * 4)];
        for (var py = 0; py < outputHeight; py++)
        {
            // Nearest sample row from the grid (north-up: grid row 0 is the top).
            var gy = height == 1 ? 0 : (int)((long)py * height / outputHeight);
            if (gy >= height)
            {
                gy = height - 1;
            }

            for (var px = 0; px < outputWidth; px++)
            {
                var gx = width == 1 ? 0 : (int)((long)px * width / outputWidth);
                if (gx >= width)
                {
                    gx = width - 1;
                }

                var index = (gy * strideY) + (gx * strideX);
                var value = index >= 0 && index < values.Length ? values[index] : double.NaN;
                var offset = ((py * outputWidth) + px) * 4;
                WritePixel(pixels.AsSpan(offset), value, colormap, ramp, fillValue);
            }
        }

        return PngEncoder.Encode(pixels, outputWidth, outputHeight);
    }

    /// <summary>
    /// Renders a resolved slice into a row-major 8-bit RGBA pixel buffer, sampling the
    /// source grid at caller-supplied per-pixel coordinates in the storage (native) CRS.
    /// This is the transformed-export path: it supports reprojection (the caller warps the
    /// output-pixel centres into native coordinates), nearest / bilinear / bicubic
    /// resampling, an explicit display stretch range, and an optional pseudocolour colormap.
    /// NaN / fill values render fully transparent.
    /// </summary>
    /// <param name="result">Decoded subset payload from <see cref="Abstractions.IZarrSubsetReader"/>.</param>
    /// <param name="slice">Resolved slice plan describing the X/Y dimension positions and native grid geometry.</param>
    /// <param name="outputWidth">Output width in pixels.</param>
    /// <param name="outputHeight">Output height in pixels.</param>
    /// <param name="sampleX">Native-CRS X coordinate to sample for each output pixel (row-major, length <c>outputWidth*outputHeight</c>).</param>
    /// <param name="sampleY">Native-CRS Y coordinate to sample for each output pixel (row-major, same length as <paramref name="sampleX"/>).</param>
    /// <param name="resampling">Resampling algorithm used when sampling the source grid.</param>
    /// <param name="colormap">Optional colormap; grayscale ramp is used when null.</param>
    /// <param name="stretchRange">Optional explicit display range (low,high); auto slice range is used when null.</param>
    /// <param name="fillValue">Optional source fill value rendered transparent.</param>
    /// <returns>Row-major RGBA pixel buffer (4 bytes/pixel).</returns>
    public static byte[] RenderRgba(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        int outputWidth,
        int outputHeight,
        double[] sampleX,
        double[] sampleY,
        ResamplingAlgorithm resampling,
        RasterColormap? colormap,
        (double Min, double Max)? stretchRange,
        double? fillValue)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(sampleX);
        ArgumentNullException.ThrowIfNull(sampleY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        if (sampleX.Length != outputWidth * outputHeight || sampleY.Length != sampleX.Length)
        {
            throw new ArgumentException("Sample coordinate buffers must match the output pixel count.", nameof(sampleX));
        }

        var height = result.Shape[slice.YDimensionIndex];
        var width = result.Shape[slice.XDimensionIndex];
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Zarr tile slice has no spatial extent.");
        }

        var (strideY, strideX) = ComputeSpatialStrides(result.Shape, slice.YDimensionIndex, slice.XDimensionIndex);
        var values = DecodeBuffer(result);

        // With an explicit stretch the display range is caller-supplied; otherwise auto-ramp
        // grayscale (and, when a colormap is present, spread it across) the finite slice range.
        var range = stretchRange ?? ResolveRange(values, fillValue);
        var lo = range.Min;
        var hi = range.Max > range.Min ? range.Max : range.Min + 1;

        var pixels = new byte[checked(outputWidth * outputHeight * 4)];
        for (var index = 0; index < sampleX.Length; index++)
        {
            var value = Sample(
                values,
                width,
                height,
                strideX,
                strideY,
                slice,
                sampleX[index],
                sampleY[index],
                resampling,
                fillValue);
            WriteStretchedPixel(pixels.AsSpan(index * 4), value, colormap, lo, hi, fillValue);
        }

        return pixels;
    }

    // Samples the decoded value grid at a native (storage-CRS) coordinate using the requested
    // resampling. Missing (NaN / fill) centres short-circuit to NaN so they render transparent;
    // non-finite neighbours in an interpolation kernel fall back to the centre value so an edge
    // pixel never smears NoData into valid output.
    private static double Sample(
        double[] values,
        int width,
        int height,
        int strideX,
        int strideY,
        ZarrTileSlicePlan slice,
        double x,
        double y,
        ResamplingAlgorithm resampling,
        double? fillValue)
    {
        // Fractional cell-centre position of the native coordinate within the subset grid.
        var fx = ((x - slice.GridXMin) / slice.CellWidth) - 0.5;
        var fy = ((slice.GridYMax - y) / slice.CellHeight) - 0.5;

        double ValueAt(int gx, int gy)
        {
            gx = Math.Clamp(gx, 0, width - 1);
            gy = Math.Clamp(gy, 0, height - 1);
            var idx = (gy * strideY) + (gx * strideX);
            var v = idx >= 0 && idx < values.Length ? values[idx] : double.NaN;
            // Substitute a missing neighbour with the centre value so an interpolation kernel
            // never smears NoData into valid output; the centre-missing case short-circuits above.
            return !double.IsFinite(v) || (fillValue is { } fill && v.Equals(fill)) ? double.NaN : v;
        }

        var centre = ValueAt((int)Math.Round(fx), (int)Math.Round(fy));
        if (resampling == ResamplingAlgorithm.NearestNeighbor || double.IsNaN(centre))
        {
            return centre;
        }

        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        if (resampling == ResamplingAlgorithm.Bilinear)
        {
            var v00 = Coalesce(ValueAt(x0, y0), centre);
            var v10 = Coalesce(ValueAt(x0 + 1, y0), centre);
            var v01 = Coalesce(ValueAt(x0, y0 + 1), centre);
            var v11 = Coalesce(ValueAt(x0 + 1, y0 + 1), centre);
            var top = v00 + ((v10 - v00) * tx);
            var bottom = v01 + ((v11 - v01) * tx);
            return top + ((bottom - top) * ty);
        }

        // Bicubic (and Lanczos, approximated as bicubic) via separable Catmull-Rom.
        var c0 = SampleRow(ValueAt, x0, y0 - 1, tx, centre);
        var c1 = SampleRow(ValueAt, x0, y0, tx, centre);
        var c2 = SampleRow(ValueAt, x0, y0 + 1, tx, centre);
        var c3 = SampleRow(ValueAt, x0, y0 + 2, tx, centre);
        return CatmullRom(c0, c1, c2, c3, ty);
    }

    private static double SampleRow(Func<int, int, double> valueAt, int x0, int gy, double tx, double centre)
    {
        var r0 = Coalesce(valueAt(x0 - 1, gy), centre);
        var r1 = Coalesce(valueAt(x0, gy), centre);
        var r2 = Coalesce(valueAt(x0 + 1, gy), centre);
        var r3 = Coalesce(valueAt(x0 + 2, gy), centre);
        return CatmullRom(r0, r1, r2, r3, tx);
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
        double hi,
        double? fillValue)
    {
        if (!double.IsFinite(value) || (fillValue is { } fill && value.Equals(fill)))
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

    private static (int StrideY, int StrideX) ComputeSpatialStrides(int[] shape, int yDim, int xDim)
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

    private static double[] DecodeBuffer(ZarrSubsetResult result)
    {
        var dtype = Normalize(result.DataType);
        var elementSize = ElementSize(dtype, result.DataType);
        var count = result.Data.Length / elementSize;
        var values = new double[count];
        var data = result.Data.AsSpan();

        switch (dtype)
        {
            case "f4":
                ReadAll(data, count, 4, values, static s => BinaryPrimitives.ReadSingleLittleEndian(s));
                break;
            case "f8":
                ReadAll(data, count, 8, values, static s => BinaryPrimitives.ReadDoubleLittleEndian(s));
                break;
            case "i1":
                for (var i = 0; i < count && i < data.Length; i++)
                {
                    values[i] = (sbyte)data[i];
                }
                break;
            case "u1":
            case "b1":
                for (var i = 0; i < count && i < data.Length; i++)
                {
                    values[i] = data[i];
                }
                break;
            case "i2":
                ReadAll(data, count, 2, values, static s => BinaryPrimitives.ReadInt16LittleEndian(s));
                break;
            case "u2":
                ReadAll(data, count, 2, values, static s => BinaryPrimitives.ReadUInt16LittleEndian(s));
                break;
            case "i4":
                ReadAll(data, count, 4, values, static s => BinaryPrimitives.ReadInt32LittleEndian(s));
                break;
            case "u4":
                ReadAll(data, count, 4, values, static s => BinaryPrimitives.ReadUInt32LittleEndian(s));
                break;
            case "i8":
                ReadAll(data, count, 8, values, static s => BinaryPrimitives.ReadInt64LittleEndian(s));
                break;
            case "u8":
                ReadAll(data, count, 8, values, static s => BinaryPrimitives.ReadUInt64LittleEndian(s));
                break;
            default:
                throw new InvalidOperationException($"Zarr dtype '{result.DataType}' is not supported for tile rendering.");
        }

        return values;
    }

    private static void ReadAll(ReadOnlySpan<byte> data, int count, int size, double[] values, ReadSample read)
    {
        for (var i = 0; i < count; i++)
        {
            var offset = i * size;
            if (offset + size > data.Length)
            {
                values[i] = double.NaN;
                continue;
            }
            values[i] = read(data.Slice(offset, size));
        }
    }

    private delegate double ReadSample(ReadOnlySpan<byte> source);

    private static (double Min, double Max) ResolveRange(double[] values, double? fillValue)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var value in values)
        {
            // Exact equality is required (not tolerance-based): fillValue is the
            // producer-declared CF/NetCDF/Zarr "_FillValue"/no-data sentinel, and
            // only a bit-identical match identifies a no-data pixel. A tolerance
            // here would risk misclassifying genuine nearby data values as
            // missing.
            if (!double.IsFinite(value) || (fillValue is { } fill && value.Equals(fill)))
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
        (double Min, double Max) ramp,
        double? fillValue)
    {
        // Exact equality is required (not tolerance-based): fillValue is the
        // producer-declared CF/NetCDF/Zarr "_FillValue"/no-data sentinel, and
        // only a bit-identical match identifies a no-data pixel. A tolerance
        // here would risk misclassifying genuine nearby data values as
        // missing.
        if (!double.IsFinite(value) || (fillValue is { } fill && value.Equals(fill)))
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

    private static string Normalize(string dtype)
        => dtype.Length > 0 && dtype[0] is '<' or '|' or '=' ? dtype[1..] : dtype;
}
