// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Honua.Core.Tests.Raster.ZarrParser;

/// <summary>
/// A minimal PNG decoder for 8-bit truecolour+alpha images, written directly against the PNG
/// specification and sharing no code with <c>PngEncoder</c> (honua-server#4395).
/// </summary>
/// <remarks>
/// Every Zarr rendering test used to stop at the PNG signature and, at best, the IHDR
/// dimensions — so a renderer that emitted a correctly-framed but blank, wrong-valued or
/// wrong-colourmapped tile passed. This decoder makes the pixels assertable: it parses IHDR,
/// concatenates the IDAT stream, inflates it, and reverses the per-scanline filters.
/// </remarks>
internal static class MiniPngDecoder
{
    /// <summary>A decoded 8-bit RGBA image.</summary>
    /// <param name="Width">Image width in pixels.</param>
    /// <param name="Height">Image height in pixels.</param>
    /// <param name="Rgba">Row-major RGBA samples, 4 bytes per pixel.</param>
    internal sealed record DecodedImage(int Width, int Height, byte[] Rgba)
    {
        /// <summary>Returns the RGBA quadruple at the given pixel.</summary>
        public (byte R, byte G, byte B, byte A) Pixel(int x, int y)
        {
            var offset = ((y * Width) + x) * 4;
            return (Rgba[offset], Rgba[offset + 1], Rgba[offset + 2], Rgba[offset + 3]);
        }
    }

    /// <summary>Decodes an 8-bit RGBA PNG.</summary>
    public static DecodedImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature))
        {
            throw new InvalidDataException("Not a PNG: missing signature.");
        }

        var cursor = 8;
        int width = 0, height = 0;
        byte bitDepth = 0, colourType = 0, interlace = 0;
        using var idat = new MemoryStream();

        while (cursor + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(cursor, 4));
            var type = Encoding.ASCII.GetString(png, cursor + 4, 4);
            var dataStart = cursor + 8;

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(dataStart + 4, 4));
                    bitDepth = png[dataStart + 8];
                    colourType = png[dataStart + 9];
                    interlace = png[dataStart + 12];
                    break;
                case "IDAT":
                    idat.Write(png, dataStart, length);
                    break;
            }

            // length + type + data + CRC
            cursor = dataStart + length + 4;
            if (type == "IEND")
            {
                break;
            }
        }

        if (bitDepth != 8 || colourType != 6)
        {
            throw new NotSupportedException(
                $"Only 8-bit truecolour+alpha PNGs are decoded here (got bitDepth={bitDepth}, colourType={colourType}).");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("Interlaced PNGs are not decoded here.");
        }

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var expected = (stride + 1) * height;
        if (raw.Length < expected)
        {
            throw new InvalidDataException($"Inflated PNG data is {raw.Length} bytes; expected {expected}.");
        }

        var rgba = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            var filter = raw[row * (stride + 1)];
            var source = raw.AsSpan((row * (stride + 1)) + 1, stride);
            var target = rgba.AsSpan(row * stride, stride);
            var previous = row == 0 ? ReadOnlySpan<byte>.Empty : rgba.AsSpan((row - 1) * stride, stride);

            for (var index = 0; index < stride; index++)
            {
                int left = index >= bytesPerPixel ? target[index - bytesPerPixel] : 0;
                int up = previous.IsEmpty ? 0 : previous[index];
                int upLeft = previous.IsEmpty || index < bytesPerPixel ? 0 : previous[index - bytesPerPixel];

                var value = filter switch
                {
                    0 => source[index],
                    1 => (byte)(source[index] + left),
                    2 => (byte)(source[index] + up),
                    3 => (byte)(source[index] + ((left + up) / 2)),
                    4 => (byte)(source[index] + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filter} on row {row}."),
                };

                target[index] = value;
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    /// <summary>Distinct gray levels present in an image whose pixels are all opaque gray.</summary>
    public static IReadOnlyList<byte> GrayLevels(DecodedImage image)
    {
        var levels = new List<byte>(image.Width * image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b, _) = image.Pixel(x, y);
                if (r != g || g != b)
                {
                    throw new InvalidOperationException($"Pixel ({x},{y}) is not gray: ({r},{g},{b}).");
                }

                levels.Add(r);
            }
        }

        return levels;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpLeft = Math.Abs(estimate - upLeft);

        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpLeft ? up : upLeft;
    }
}
