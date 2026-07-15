// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Honua.Core.Features.Raster.Domain;
using SkiaSharp;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Re-encodes a rendered multidimensional (Zarr) slice — supplied by the canonical slice
/// reader as a row-major RGBA pixel buffer — into the container formats the ImageServer
/// <c>exportImage</c> contract advertises. PNG is emitted by the reader itself; this helper
/// covers the JPEG and TIFF outputs the reader does not produce.
/// </summary>
internal static class ImageServerRasterSliceEncoder
{
    private const string JpegContentType = "image/jpeg";
    private const string TiffContentType = "image/tiff";
    private const int DefaultJpegQuality = 75;

    /// <summary>
    /// Encodes an 8-bit RGBA buffer as a JPEG using the shared SkiaSharp raster stack.
    /// JPEG has no alpha channel, so transparent (NoData) pixels are flattened to black.
    /// </summary>
    public static (byte[] Data, string ContentType) EncodeJpeg(byte[] rgba, int width, int height, int? quality)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var clampedQuality = Math.Clamp(quality ?? DefaultJpegQuality, 1, 100);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), Math.Min(rgba.Length, width * height * 4));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, clampedQuality)
            ?? throw new InvalidOperationException("Unable to encode the multidimensional slice as JPEG.");
        return (encoded.ToArray(), JpegContentType);
    }

    /// <summary>
    /// Encodes an 8-bit RGBA buffer as a baseline RGB TIFF (dropping the alpha channel).
    /// A <see cref="TiffCompression.Deflate"/> request stores the single strip zlib-compressed
    /// (TIFF Adobe Deflate); any other value is stored uncompressed.
    /// </summary>
    public static (byte[] Data, string ContentType) EncodeTiff(
        byte[] rgba,
        int width,
        int height,
        TiffCompression? compression)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixelCount = checked(width * height);
        var rgb = new byte[checked(pixelCount * 3)];
        for (var i = 0; i < pixelCount; i++)
        {
            var src = i * 4;
            var dst = i * 3;
            rgb[dst] = rgba[src];
            rgb[dst + 1] = rgba[src + 1];
            rgb[dst + 2] = rgba[src + 2];
        }

        var deflate = compression == TiffCompression.Deflate;
        var strip = deflate ? ZlibCompress(rgb) : rgb;
        const int CompressionNone = 1;
        const int CompressionDeflate = 8;

        // 10 tags; each IFD entry is 12 bytes; the directory ends with a 4-byte next-IFD offset.
        const int TagCount = 10;
        var ifdSize = 2 + (TagCount * 12) + 4;
        var bitsPerSampleOffset = 8 + ifdSize;
        var stripOffset = bitsPerSampleOffset + 6;
        var totalSize = stripOffset + strip.Length;

        var buffer = new byte[totalSize];

        // Header: little-endian byte order, magic 42, first-IFD offset.
        buffer[0] = (byte)'I';
        buffer[1] = (byte)'I';
        WriteUInt16(buffer, 2, 42);
        WriteUInt32(buffer, 4, 8);

        WriteUInt16(buffer, 8, TagCount);

        const int TypeShort = 3;
        const int TypeLong = 4;
        var entry = 10;
        WriteEntry(buffer, ref entry, 256, TypeShort, 1, width);                                  // ImageWidth
        WriteEntry(buffer, ref entry, 257, TypeShort, 1, height);                                 // ImageLength
        WriteEntry(buffer, ref entry, 258, TypeShort, 3, bitsPerSampleOffset);                    // BitsPerSample -> [8,8,8]
        WriteEntry(buffer, ref entry, 259, TypeShort, 1, deflate ? CompressionDeflate : CompressionNone); // Compression
        WriteEntry(buffer, ref entry, 262, TypeShort, 1, 2);                                      // PhotometricInterpretation = RGB
        WriteEntry(buffer, ref entry, 273, TypeLong, 1, stripOffset);                             // StripOffsets
        WriteEntry(buffer, ref entry, 277, TypeShort, 1, 3);                                      // SamplesPerPixel
        WriteEntry(buffer, ref entry, 278, TypeShort, 1, height);                                 // RowsPerStrip
        WriteEntry(buffer, ref entry, 279, TypeLong, 1, strip.Length);                            // StripByteCounts
        WriteEntry(buffer, ref entry, 284, TypeShort, 1, 1);                                      // PlanarConfiguration = chunky

        // Next-IFD offset (0 = last).
        WriteUInt32(buffer, entry, 0);

        // BitsPerSample array (three SHORT values, 8 bits per channel).
        WriteUInt16(buffer, bitsPerSampleOffset, 8);
        WriteUInt16(buffer, bitsPerSampleOffset + 2, 8);
        WriteUInt16(buffer, bitsPerSampleOffset + 4, 8);

        strip.CopyTo(buffer.AsSpan(stripOffset));
        return (buffer, TiffContentType);
    }

    private static void WriteEntry(byte[] buffer, ref int offset, int tag, int type, int count, long value)
    {
        WriteUInt16(buffer, offset, (ushort)tag);
        WriteUInt16(buffer, offset + 2, (ushort)type);
        WriteUInt32(buffer, offset + 4, (uint)count);
        WriteUInt32(buffer, offset + 8, (uint)value);
        offset += 12;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
