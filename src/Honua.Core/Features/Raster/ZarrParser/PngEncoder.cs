// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Minimal AOT-safe PNG encoder for 8-bit RGBA images. Emits a single IDAT chunk
/// with filter type 0 (None) per scanline, compressed with the managed zlib
/// (<see cref="ZLibStream"/>) deflate codec. Used to render Zarr coverage slices to
/// map tiles without taking a dependency on a native imaging library.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encodes an 8-bit RGBA pixel buffer (row-major, 4 bytes/pixel) to a PNG.
    /// </summary>
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var expected = checked(width * height * 4);
        if (rgba.Length < expected)
        {
            throw new ArgumentException("RGBA buffer is smaller than width*height*4.", nameof(rgba));
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        // IHDR: width, height, bit depth 8, colour type 6 (truecolour + alpha),
        // compression 0, filter 0, interlace 0.
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", BuildIdat(rgba, width, height));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);

        return output.ToArray();
    }

    private static byte[] BuildIdat(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var scanline = new byte[stride + 1];
            for (var y = 0; y < height; y++)
            {
                scanline[0] = 0; // filter type: None
                Buffer.BlockCopy(rgba, y * stride, scanline, 1, stride);
                zlib.Write(scanline, 0, scanline.Length);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }
        output.Write(typeBytes);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>
    /// Standard PNG CRC-32 (IEEE 802.3) over a chunk's type and data bytes.
    /// </summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in type)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }
    }
}
