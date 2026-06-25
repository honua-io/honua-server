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
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);

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

        var pixels = new byte[tileSize * tileSize * 4];
        for (var py = 0; py < tileSize; py++)
        {
            // Nearest sample row from the grid (north-up: grid row 0 is the top).
            var gy = height == 1 ? 0 : (int)((long)py * height / tileSize);
            if (gy >= height)
            {
                gy = height - 1;
            }

            for (var px = 0; px < tileSize; px++)
            {
                var gx = width == 1 ? 0 : (int)((long)px * width / tileSize);
                if (gx >= width)
                {
                    gx = width - 1;
                }

                var index = (gy * strideY) + (gx * strideX);
                var value = index >= 0 && index < values.Length ? values[index] : double.NaN;
                var offset = ((py * tileSize) + px) * 4;
                WritePixel(pixels.AsSpan(offset), value, colormap, ramp, fillValue);
            }
        }

        return PngEncoder.Encode(pixels, tileSize, tileSize);
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
            if (!double.IsFinite(value) || (fillValue is { } fill && value == fill))
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
        if (!double.IsFinite(value) || (fillValue is { } fill && value == fill))
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
