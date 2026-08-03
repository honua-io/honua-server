// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Geoprocessing;

/// <summary>
/// Reads the bounded TIFF header metadata needed by raster admission without loading GDAL or
/// decoding the raster body in the serving process.
/// </summary>
internal static class InlineRasterMetadataReader
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxIfdEntries = 3000;

    public static bool TryReadBase64(string? encoded, out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var charCount = Math.Min(encoded.Length, MaxHeaderBytes / 3 * 4);
        charCount -= charCount % 4;
        if (charCount == 0)
        {
            return false;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(charCount / 4 * 3);
        return Convert.TryFromBase64Chars(encoded.AsSpan(0, charCount), buffer, out var bytesWritten)
            && TryRead(buffer.AsSpan(0, bytesWritten), out metadata);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (payload.Length < 8)
        {
            return false;
        }

        var littleEndian = payload[0] == (byte)'I' && payload[1] == (byte)'I';
        var bigEndian = payload[0] == (byte)'M' && payload[1] == (byte)'M';
        if (!littleEndian && !bigEndian)
        {
            return false;
        }

        var magic = ReadUInt16(payload, 2, littleEndian);
        return magic switch
        {
            42 => TryReadClassicTiff(payload, littleEndian, out metadata),
            43 => TryReadBigTiff(payload, littleEndian, out metadata),
            _ => false,
        };
    }

    private static bool TryReadClassicTiff(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        var ifdOffset = ReadUInt32(payload, 4, littleEndian);
        if (ifdOffset > int.MaxValue || !Contains(payload, (int)ifdOffset, 2))
        {
            return false;
        }

        var entryCount = ReadUInt16(payload, (int)ifdOffset, littleEndian);
        if (entryCount > MaxIfdEntries)
        {
            return false;
        }

        var entriesOffset = (int)ifdOffset + 2;
        if (!Contains(payload, entriesOffset, entryCount * 12))
        {
            return false;
        }

        return TryReadDimensions(payload, littleEndian, entriesOffset, entryCount, entrySize: 12, inlineSize: 4, isBigTiff: false, out metadata);
    }

    private static bool TryReadBigTiff(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (payload.Length < 16
            || ReadUInt16(payload, 4, littleEndian) != 8
            || ReadUInt16(payload, 6, littleEndian) != 0)
        {
            return false;
        }

        var ifdOffset = ReadUInt64(payload, 8, littleEndian);
        if (ifdOffset > int.MaxValue || !Contains(payload, (int)ifdOffset, 8))
        {
            return false;
        }

        var entryCount = ReadUInt64(payload, (int)ifdOffset, littleEndian);
        if (entryCount > MaxIfdEntries)
        {
            return false;
        }

        var entriesOffset = (int)ifdOffset + 8;
        var entriesLength = checked((int)entryCount * 20);
        if (!Contains(payload, entriesOffset, entriesLength))
        {
            return false;
        }

        return TryReadDimensions(payload, littleEndian, entriesOffset, (int)entryCount, entrySize: 20, inlineSize: 8, isBigTiff: true, out metadata);
    }

    private static bool TryReadDimensions(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        int entriesOffset,
        int entryCount,
        int entrySize,
        int inlineSize,
        bool isBigTiff,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        ulong? width = null;
        ulong? height = null;
        ulong bands = 1;
        ulong sampleBytes = 8;

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = entriesOffset + index * entrySize;
            var tag = ReadUInt16(payload, entryOffset, littleEndian);
            if (tag is not (256 or 257 or 258 or 277))
            {
                continue;
            }

            ulong value;
            var valueRead = tag == 258
                ? TryReadMaximumEntryValue(
                    payload,
                    littleEndian,
                    entryOffset,
                    inlineSize,
                    isBigTiff,
                    out value)
                : TryReadFirstEntryValue(
                    payload,
                    littleEndian,
                    entryOffset,
                    inlineSize,
                    isBigTiff,
                    out value);
            if (!valueRead || value == 0)
            {
                return false;
            }

            switch (tag)
            {
                case 256:
                    width = value;
                    break;
                case 257:
                    height = value;
                    break;
                case 258:
                    if (value > ulong.MaxValue - 7)
                    {
                        return false;
                    }

                    sampleBytes = Math.Max(sampleBytes, (value + 7) / 8);
                    break;
                case 277:
                    bands = value;
                    break;
            }
        }

        if (width is null
            || height is null
            || width > long.MaxValue
            || height > long.MaxValue
            || bands > long.MaxValue
            || sampleBytes > long.MaxValue)
        {
            return false;
        }

        metadata = new InlineRasterMetadata(
            (long)width,
            (long)height,
            (long)bands,
            (long)sampleBytes);
        return true;
    }

    private static bool TryReadMaximumEntryValue(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        int entryOffset,
        int inlineSize,
        bool isBigTiff,
        out ulong value)
    {
        value = 0;
        var type = ReadUInt16(payload, entryOffset + 2, littleEndian);
        var typeSize = ResolveTypeSize(type);
        if (typeSize == 0)
        {
            return false;
        }

        var count = isBigTiff
            ? ReadUInt64(payload, entryOffset + 4, littleEndian)
            : ReadUInt32(payload, entryOffset + 4, littleEndian);
        if (count == 0 || count > 1024 || count > ulong.MaxValue / (ulong)typeSize)
        {
            return false;
        }

        var valueBytes = count * (ulong)typeSize;
        var valueFieldOffset = entryOffset + (isBigTiff ? 12 : 8);
        var dataOffset = valueBytes <= (ulong)inlineSize
            ? (ulong)valueFieldOffset
            : isBigTiff
                ? ReadUInt64(payload, valueFieldOffset, littleEndian)
                : ReadUInt32(payload, valueFieldOffset, littleEndian);
        if (dataOffset > int.MaxValue
            || valueBytes > int.MaxValue
            || !Contains(payload, (int)dataOffset, (int)valueBytes))
        {
            return false;
        }

        for (ulong index = 0; index < count; index++)
        {
            var offset = (int)(dataOffset + index * (ulong)typeSize);
            var candidate = ReadEntryValue(payload, offset, littleEndian, type);
            value = Math.Max(value, candidate);
        }

        return true;
    }

    private static bool TryReadFirstEntryValue(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        int entryOffset,
        int inlineSize,
        bool isBigTiff,
        out ulong value)
    {
        value = 0;
        var type = ReadUInt16(payload, entryOffset + 2, littleEndian);
        var typeSize = ResolveTypeSize(type);
        if (typeSize == 0)
        {
            return false;
        }

        var count = isBigTiff
            ? ReadUInt64(payload, entryOffset + 4, littleEndian)
            : ReadUInt32(payload, entryOffset + 4, littleEndian);
        if (count == 0 || count > ulong.MaxValue / (ulong)typeSize)
        {
            return false;
        }

        var valueBytes = count * (ulong)typeSize;
        var valueFieldOffset = entryOffset + (isBigTiff ? 12 : 8);
        ulong dataOffset;
        if (valueBytes <= (ulong)inlineSize)
        {
            dataOffset = (ulong)valueFieldOffset;
        }
        else
        {
            dataOffset = isBigTiff
                ? ReadUInt64(payload, valueFieldOffset, littleEndian)
                : ReadUInt32(payload, valueFieldOffset, littleEndian);
        }

        if (dataOffset > int.MaxValue || !Contains(payload, (int)dataOffset, typeSize))
        {
            return false;
        }

        value = ReadEntryValue(payload, (int)dataOffset, littleEndian, type);
        return true;
    }

    private static int ResolveTypeSize(ushort type) => type switch
    {
        1 => 1,
        3 => 2,
        4 => 4,
        16 or 18 => 8,
        _ => 0,
    };

    private static ulong ReadEntryValue(
        ReadOnlySpan<byte> payload,
        int offset,
        bool littleEndian,
        ushort type) => type switch
        {
            1 => payload[offset],
            3 => ReadUInt16(payload, offset, littleEndian),
            4 => ReadUInt32(payload, offset, littleEndian),
            16 or 18 => ReadUInt64(payload, offset, littleEndian),
            _ => 0,
        };

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);

    private static ulong ReadUInt64(ReadOnlySpan<byte> payload, int offset, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..])
            : BinaryPrimitives.ReadUInt64BigEndian(payload[offset..]);

    private static bool Contains(ReadOnlySpan<byte> payload, int offset, int length)
        => offset >= 0 && length >= 0 && offset <= payload.Length - length;
}

/// <summary>Trusted dimensions read from a bounded inline TIFF header.</summary>
internal readonly record struct InlineRasterMetadata(
    long Width,
    long Height,
    long Bands,
    long SampleBytes);
