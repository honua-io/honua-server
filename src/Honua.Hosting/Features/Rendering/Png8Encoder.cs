// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal static class Png8Encoder
{
    private const int MaxPaletteEntries = 256;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal static byte[] Encode(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var snapshot = surface.Snapshot();
        var (width, height, pixelBytes) = ReadRgbaPixels(snapshot);
        var (palette, indices) = BuildPaletteAndIndices(pixelBytes, width, height);

        using var output = new MemoryStream(checked(width * height + 1024));
        output.Write(PngSignature);
        WriteIhdrChunk(output, width, height);
        WritePaletteChunks(output, palette);
        WriteIdatChunk(output, indices, width, height);
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static (int Width, int Height, byte[] Pixels) ReadRgbaPixels(SKImage snapshot)
    {
        var width = snapshot.Width;
        var height = snapshot.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Render surface dimensions must be positive.");
        }

        var pixelBytes = new byte[checked(width * height * 4)];
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (!snapshot.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0))
        {
            throw new InvalidOperationException("Failed to read pixels from render surface for PNG8 encoding.");
        }

        Marshal.Copy(bitmap.GetPixels(), pixelBytes, 0, pixelBytes.Length);
        return (width, height, pixelBytes);
    }

    private static void WriteIhdrChunk(Stream output, int width, int height)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 3;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);
    }

    private static void WritePaletteChunks(Stream output, uint[] palette)
    {
        var plte = new byte[palette.Length * 3];
        var lastNonOpaqueIndex = -1;
        for (var i = 0; i < palette.Length; i++)
        {
            var color = palette[i];
            plte[i * 3] = (byte)(color & 0xFF);
            plte[i * 3 + 1] = (byte)((color >> 8) & 0xFF);
            plte[i * 3 + 2] = (byte)((color >> 16) & 0xFF);
            if (((color >> 24) & 0xFF) < 255)
            {
                lastNonOpaqueIndex = i;
            }
        }

        WriteChunk(output, "PLTE"u8, plte);
        if (lastNonOpaqueIndex < 0)
        {
            return;
        }

        var trns = new byte[lastNonOpaqueIndex + 1];
        for (var i = 0; i <= lastNonOpaqueIndex; i++)
        {
            trns[i] = (byte)((palette[i] >> 24) & 0xFF);
        }

        WriteChunk(output, "tRNS"u8, trns);
    }

    private static void WriteIdatChunk(Stream output, byte[] indices, int width, int height)
    {
        using var idatBuffer = new MemoryStream();
        using (var zlib = new ZLibStream(idatBuffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1];
            row[0] = 0;
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(indices, y * width, row, 1, width);
                zlib.Write(row, 0, row.Length);
            }
        }

        WriteChunk(output, "IDAT"u8, idatBuffer.ToArray());
    }

    private static (uint[] Palette, byte[] Indices) BuildPaletteAndIndices(byte[] rgba, int width, int height)
    {
        var pixelCount = width * height;
        var indices = new byte[pixelCount];
        var exactLookup = new Dictionary<uint, byte>(MaxPaletteEntries);
        var exactPalette = new List<uint>(MaxPaletteEntries);
        var overflow = false;

        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * 4;
            var r = rgba[offset];
            var g = rgba[offset + 1];
            var b = rgba[offset + 2];
            var a = rgba[offset + 3];
            // Canonicalize fully transparent pixels so RGB noise does not waste palette slots.
            var packed = a == 0
                ? 0u
                : (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

            if (!exactLookup.TryGetValue(packed, out var index))
            {
                if (exactPalette.Count >= MaxPaletteEntries)
                {
                    overflow = true;
                    break;
                }

                index = (byte)exactPalette.Count;
                exactLookup[packed] = index;
                exactPalette.Add(packed);
            }

            indices[i] = index;
        }

        if (!overflow)
        {
            return (exactPalette.ToArray(), indices);
        }

        // Fallback for images with >256 distinct RGBA values:
        // Index 0 = fully transparent, indices 1..216 = 6x6x6 opaque RGB cube (0, 51, 102, 153, 204, 255).
        var quantizedPalette = new uint[217];
        quantizedPalette[0] = 0u;
        var slot = 1;
        for (var ri = 0; ri < 6; ri++)
        {
            var r = (byte)(ri * 51);
            for (var gi = 0; gi < 6; gi++)
            {
                var g = (byte)(gi * 51);
                for (var bi = 0; bi < 6; bi++)
                {
                    var b = (byte)(bi * 51);
                    quantizedPalette[slot++] = (uint)r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;
                }
            }
        }

        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * 4;
            var a = rgba[offset + 3];
            if (a < 128)
            {
                indices[i] = 0;
                continue;
            }

            var ri = (rgba[offset] * 5 + 127) / 255;
            var gi = (rgba[offset + 1] * 5 + 127) / 255;
            var bi = (rgba[offset + 2] * 5 + 127) / 255;
            indices[i] = (byte)(1 + (ri * 36) + (gi * 6) + bi);
        }

        return (quantizedPalette, indices);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        output.Write(lengthBytes);
        output.Write(type);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = ComputeCrc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }
}
