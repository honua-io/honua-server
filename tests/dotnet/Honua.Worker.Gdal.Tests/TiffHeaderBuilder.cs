// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Builds minimal, header-only TIFF / BigTIFF byte payloads that DECLARE a given
/// width/height/bands/bit-depth without any pixel data — the exact shape a
/// decompression-bomb raster takes (tiny on disk, enormous declared dimensions).
/// Used to exercise <c>GdalRasterDimensionGuard</c> with no GDAL binary.
/// </summary>
internal static class TiffHeaderBuilder
{
    private const int TagImageWidth = 256;
    private const int TagImageLength = 257;
    private const int TagBitsPerSample = 258;
    private const int TagSamplesPerPixel = 277;

    private const int TypeShort = 3;
    private const int TypeLong = 4;
    private const int TypeLong8 = 16;

    /// <summary>Builds a classic (32-bit) TIFF header with a single IFD.</summary>
    public static byte[] Classic(long width, long height, int bands = 1, int bits = 8, bool littleEndian = true)
    {
        var bytes = new List<byte>();
        // Header
        bytes.Add(littleEndian ? (byte)0x49 : (byte)0x4D);
        bytes.Add(littleEndian ? (byte)0x49 : (byte)0x4D);
        WriteU16(bytes, 42, littleEndian);
        WriteU32(bytes, 8, littleEndian); // first IFD at offset 8

        // IFD @8: entry count then entries.
        WriteU16(bytes, 4, littleEndian);
        WriteClassicEntry(bytes, TagImageWidth, TypeLong, width, littleEndian);
        WriteClassicEntry(bytes, TagImageLength, TypeLong, height, littleEndian);
        WriteClassicEntry(bytes, TagBitsPerSample, TypeShort, bits, littleEndian);
        WriteClassicEntry(bytes, TagSamplesPerPixel, TypeShort, bands, littleEndian);
        WriteU32(bytes, 0, littleEndian); // next IFD = none

        return bytes.ToArray();
    }

    /// <summary>Builds a BigTIFF (64-bit) header with a single IFD.</summary>
    public static byte[] BigTiff(long width, long height, int bands = 1, int bits = 8, bool littleEndian = true)
    {
        var bytes = new List<byte>();
        bytes.Add(littleEndian ? (byte)0x49 : (byte)0x4D);
        bytes.Add(littleEndian ? (byte)0x49 : (byte)0x4D);
        WriteU16(bytes, 43, littleEndian);
        WriteU16(bytes, 8, littleEndian); // bytesize of offsets
        WriteU16(bytes, 0, littleEndian); // constant 0
        WriteU64(bytes, 16, littleEndian); // first IFD at offset 16

        // IFD @16
        WriteU64(bytes, 4, littleEndian); // entry count
        WriteBigEntry(bytes, TagImageWidth, TypeLong8, width, littleEndian);
        WriteBigEntry(bytes, TagImageLength, TypeLong8, height, littleEndian);
        WriteBigEntry(bytes, TagBitsPerSample, TypeShort, bits, littleEndian);
        WriteBigEntry(bytes, TagSamplesPerPixel, TypeShort, bands, littleEndian);
        WriteU64(bytes, 0, littleEndian); // next IFD = none

        return bytes.ToArray();
    }

    private static void WriteClassicEntry(List<byte> bytes, int tag, int type, long value, bool little)
    {
        WriteU16(bytes, tag, little);
        WriteU16(bytes, type, little);
        WriteU32(bytes, 1, little); // count
        // 4-byte value field: scalar left-justified per type.
        var field = new List<byte>();
        WriteScalarField(field, type, value, little);
        while (field.Count < 4)
        {
            field.Add(0);
        }
        bytes.AddRange(field);
    }

    private static void WriteBigEntry(List<byte> bytes, int tag, int type, long value, bool little)
    {
        WriteU16(bytes, tag, little);
        WriteU16(bytes, type, little);
        WriteU64(bytes, 1, little); // count
        // 8-byte value field.
        var field = new List<byte>();
        WriteScalarField(field, type, value, little);
        while (field.Count < 8)
        {
            field.Add(0);
        }
        bytes.AddRange(field);
    }

    private static void WriteScalarField(List<byte> field, int type, long value, bool little)
    {
        switch (type)
        {
            case TypeShort:
                WriteU16(field, (int)value, little);
                break;
            case TypeLong:
                WriteU32(field, value, little);
                break;
            case TypeLong8:
                WriteU64(field, value, little);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "unsupported scalar type");
        }
    }

    private static void WriteU16(List<byte> bytes, int value, bool little)
    {
        var b0 = (byte)(value & 0xFF);
        var b1 = (byte)((value >> 8) & 0xFF);
        if (little)
        {
            bytes.Add(b0);
            bytes.Add(b1);
        }
        else
        {
            bytes.Add(b1);
            bytes.Add(b0);
        }
    }

    private static void WriteU32(List<byte> bytes, long value, bool little)
    {
        Span<byte> buf = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            buf[i] = (byte)((value >> (8 * i)) & 0xFF);
        }
        if (!little)
        {
            buf.Reverse();
        }
        foreach (var b in buf)
        {
            bytes.Add(b);
        }
    }

    private static void WriteU64(List<byte> bytes, long value, bool little)
    {
        Span<byte> buf = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            buf[i] = (byte)((value >> (8 * i)) & 0xFF);
        }
        if (!little)
        {
            buf.Reverse();
        }
        foreach (var b in buf)
        {
            bytes.Add(b);
        }
    }
}
