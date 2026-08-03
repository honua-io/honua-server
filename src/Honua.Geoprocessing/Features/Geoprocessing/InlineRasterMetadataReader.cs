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
    private const int MaxIfdEntries = 3000;
    private const ushort ModelPixelScaleTag = 33550;
    private const ushort ModelTransformationTag = 34264;

    public static bool TryReadBase64(string? encoded, out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        return Base64ByteReader.TryCreate(encoded, out var reader)
            && TryReadBase64(reader, out metadata);
    }

    private static bool TryReadBase64(
        Base64ByteReader reader,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (!reader.TryReadByte(0, out var byteOrder0)
            || !reader.TryReadByte(1, out var byteOrder1))
        {
            return false;
        }

        var littleEndian = byteOrder0 == (byte)'I' && byteOrder1 == (byte)'I';
        var bigEndian = byteOrder0 == (byte)'M' && byteOrder1 == (byte)'M';
        if (!littleEndian && !bigEndian
            || !reader.TryReadUInt16(2, littleEndian, out var magic))
        {
            return false;
        }

        return magic switch
        {
            42 => TryReadBase64ClassicTiff(reader, littleEndian, out metadata),
            43 => TryReadBase64BigTiff(reader, littleEndian, out metadata),
            _ => false,
        };
    }

    private static bool TryReadBase64ClassicTiff(
        Base64ByteReader reader,
        bool littleEndian,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (!reader.TryReadUInt32(4, littleEndian, out var ifdOffset)
            || !reader.TryReadUInt16(ifdOffset, littleEndian, out var entryCount)
            || entryCount > MaxIfdEntries)
        {
            return false;
        }

        var entriesOffset = (ulong)ifdOffset + sizeof(ushort);
        return reader.Contains(entriesOffset, entryCount * 12L)
            && TryReadBase64Dimensions(
                reader,
                littleEndian,
                entriesOffset,
                entryCount,
                entrySize: 12,
                inlineSize: 4,
                isBigTiff: false,
                out metadata);
    }

    private static bool TryReadBase64BigTiff(
        Base64ByteReader reader,
        bool littleEndian,
        out InlineRasterMetadata metadata)
    {
        metadata = default;
        if (!reader.TryReadUInt16(4, littleEndian, out var offsetSize)
            || offsetSize != 8
            || !reader.TryReadUInt16(6, littleEndian, out var reserved)
            || reserved != 0
            || !reader.TryReadUInt64(8, littleEndian, out var ifdOffset)
            || !reader.TryReadUInt64(ifdOffset, littleEndian, out var entryCount)
            || entryCount > MaxIfdEntries)
        {
            return false;
        }

        var entriesOffset = ifdOffset + sizeof(ulong);
        return entriesOffset >= ifdOffset
            && reader.Contains(entriesOffset, checked((long)entryCount * 20))
            && TryReadBase64Dimensions(
                reader,
                littleEndian,
                entriesOffset,
                (int)entryCount,
                entrySize: 20,
                inlineSize: 8,
                isBigTiff: true,
                out metadata);
    }

    private static bool TryReadBase64Dimensions(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entriesOffset,
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
        double? pixelScaleX = null;
        double? pixelScaleY = null;
        AffinePixelTransform? modelTransformation = null;
        var modelTransformationSeen = false;

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = entriesOffset + (ulong)(index * entrySize);
            if (!reader.TryReadUInt16(entryOffset, littleEndian, out var tag))
            {
                return false;
            }

            if (tag == ModelPixelScaleTag)
            {
                if (TryReadBase64PixelScale(
                        reader,
                        littleEndian,
                        entryOffset,
                        inlineSize,
                        isBigTiff,
                        out var scaleX,
                        out var scaleY))
                {
                    pixelScaleX = scaleX;
                    pixelScaleY = scaleY;
                }

                continue;
            }

            if (tag == ModelTransformationTag)
            {
                modelTransformationSeen = true;
                modelTransformation = TryReadBase64ModelTransformation(
                    reader,
                    littleEndian,
                    entryOffset,
                    inlineSize,
                    isBigTiff,
                    out var transformation)
                        ? transformation
                        : null;
                continue;
            }

            if (tag is not (256 or 257 or 258 or 277))
            {
                continue;
            }

            var valueRead = tag == 258
                ? TryReadBase64MaximumEntryValue(
                    reader,
                    littleEndian,
                    entryOffset,
                    inlineSize,
                    isBigTiff,
                    out var value)
                : TryReadBase64FirstEntryValue(
                    reader,
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

        ResolveEffectivePixelScale(
            width.Value,
            height.Value,
            modelTransformationSeen,
            modelTransformation,
            ref pixelScaleX,
            ref pixelScaleY);

        metadata = new InlineRasterMetadata(
            (long)width,
            (long)height,
            (long)bands,
            (long)sampleBytes,
            pixelScaleX,
            pixelScaleY);
        return true;
    }

    private static bool TryReadBase64PixelScale(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entryOffset,
        int inlineSize,
        bool isBigTiff,
        out double scaleX,
        out double scaleY)
    {
        scaleX = 0;
        scaleY = 0;
        if (!reader.TryReadUInt16(entryOffset + 2, littleEndian, out var type)
            || type != 12
            || !TryReadBase64EntryCount(reader, littleEndian, entryOffset, isBigTiff, out var count)
            || count is < 2 or > 16
            || count > ulong.MaxValue / sizeof(double))
        {
            return false;
        }

        var valueBytes = count * sizeof(double);
        var valueFieldOffset = entryOffset + (isBigTiff ? 12UL : 8UL);
        if (!TryResolveBase64DataOffset(
                reader,
                littleEndian,
                valueFieldOffset,
                valueBytes,
                inlineSize,
                isBigTiff,
                out var dataOffset)
            || !reader.TryReadDouble(dataOffset, littleEndian, out scaleX)
            || !reader.TryReadDouble(dataOffset + sizeof(double), littleEndian, out scaleY))
        {
            return false;
        }

        return double.IsFinite(scaleX)
            && double.IsFinite(scaleY)
            && scaleX > 0
            && scaleY > 0;
    }

    private static bool TryReadBase64ModelTransformation(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entryOffset,
        int inlineSize,
        bool isBigTiff,
        out AffinePixelTransform transformation)
    {
        transformation = default;
        if (!reader.TryReadUInt16(entryOffset + 2, littleEndian, out var type)
            || type != 12
            || !TryReadBase64EntryCount(reader, littleEndian, entryOffset, isBigTiff, out var count)
            || count != 16)
        {
            return false;
        }

        const ulong valueBytes = 16 * sizeof(double);
        var valueFieldOffset = entryOffset + (isBigTiff ? 12UL : 8UL);
        if (!TryResolveBase64DataOffset(
                reader,
                littleEndian,
                valueFieldOffset,
                valueBytes,
                inlineSize,
                isBigTiff,
                out var dataOffset)
            || !reader.TryReadDouble(dataOffset, littleEndian, out var xFromColumn)
            || !reader.TryReadDouble(dataOffset + sizeof(double), littleEndian, out var xFromRow)
            || !reader.TryReadDouble(dataOffset + 4 * sizeof(double), littleEndian, out var yFromColumn)
            || !reader.TryReadDouble(dataOffset + 5 * sizeof(double), littleEndian, out var yFromRow))
        {
            return false;
        }

        transformation = new AffinePixelTransform(xFromColumn, xFromRow, yFromColumn, yFromRow);
        return transformation.IsFinite;
    }

    private static bool TryReadBase64MaximumEntryValue(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entryOffset,
        int inlineSize,
        bool isBigTiff,
        out ulong value)
    {
        value = 0;
        if (!reader.TryReadUInt16(entryOffset + 2, littleEndian, out var type))
        {
            return false;
        }

        var typeSize = ResolveTypeSize(type);
        if (typeSize == 0
            || !TryReadBase64EntryCount(reader, littleEndian, entryOffset, isBigTiff, out var count)
            || count == 0
            || count > 1024
            || count > ulong.MaxValue / (ulong)typeSize)
        {
            return false;
        }

        var valueBytes = count * (ulong)typeSize;
        var valueFieldOffset = entryOffset + (isBigTiff ? 12UL : 8UL);
        if (!TryResolveBase64DataOffset(
                reader,
                littleEndian,
                valueFieldOffset,
                valueBytes,
                inlineSize,
                isBigTiff,
                out var dataOffset)
            || !reader.Contains(dataOffset, checked((long)valueBytes)))
        {
            return false;
        }

        for (ulong index = 0; index < count; index++)
        {
            if (!TryReadBase64EntryValue(
                    reader,
                    dataOffset + index * (ulong)typeSize,
                    littleEndian,
                    type,
                    out var candidate))
            {
                return false;
            }

            value = Math.Max(value, candidate);
        }

        return true;
    }

    private static bool TryReadBase64FirstEntryValue(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entryOffset,
        int inlineSize,
        bool isBigTiff,
        out ulong value)
    {
        value = 0;
        if (!reader.TryReadUInt16(entryOffset + 2, littleEndian, out var type))
        {
            return false;
        }

        var typeSize = ResolveTypeSize(type);
        if (typeSize == 0
            || !TryReadBase64EntryCount(reader, littleEndian, entryOffset, isBigTiff, out var count)
            || count == 0
            || count > ulong.MaxValue / (ulong)typeSize)
        {
            return false;
        }

        var valueBytes = count * (ulong)typeSize;
        var valueFieldOffset = entryOffset + (isBigTiff ? 12UL : 8UL);
        return TryResolveBase64DataOffset(
                reader,
                littleEndian,
                valueFieldOffset,
                valueBytes,
                inlineSize,
                isBigTiff,
                out var dataOffset)
            && TryReadBase64EntryValue(reader, dataOffset, littleEndian, type, out value);
    }

    private static bool TryReadBase64EntryCount(
        Base64ByteReader reader,
        bool littleEndian,
        ulong entryOffset,
        bool isBigTiff,
        out ulong count)
    {
        if (isBigTiff)
        {
            return reader.TryReadUInt64(entryOffset + 4, littleEndian, out count);
        }

        var read = reader.TryReadUInt32(entryOffset + 4, littleEndian, out var classicCount);
        count = classicCount;
        return read;
    }

    private static bool TryResolveBase64DataOffset(
        Base64ByteReader reader,
        bool littleEndian,
        ulong valueFieldOffset,
        ulong valueBytes,
        int inlineSize,
        bool isBigTiff,
        out ulong dataOffset)
    {
        if (valueBytes <= (ulong)inlineSize)
        {
            dataOffset = valueFieldOffset;
            return reader.Contains(dataOffset, checked((long)valueBytes));
        }

        return isBigTiff
            ? reader.TryReadUInt64(valueFieldOffset, littleEndian, out dataOffset)
                && valueBytes <= long.MaxValue
                && reader.Contains(dataOffset, (long)valueBytes)
            : TryReadClassicBase64DataOffset(
                reader,
                littleEndian,
                valueFieldOffset,
                valueBytes,
                out dataOffset);
    }

    private static bool TryReadClassicBase64DataOffset(
        Base64ByteReader reader,
        bool littleEndian,
        ulong valueFieldOffset,
        ulong valueBytes,
        out ulong dataOffset)
    {
        var read = reader.TryReadUInt32(valueFieldOffset, littleEndian, out var classicOffset);
        dataOffset = classicOffset;
        return read
            && valueBytes <= long.MaxValue
            && reader.Contains(dataOffset, (long)valueBytes);
    }

    private static bool TryReadBase64EntryValue(
        Base64ByteReader reader,
        ulong offset,
        bool littleEndian,
        ushort type,
        out ulong value)
    {
        value = 0;
        switch (type)
        {
            case 1:
                if (!reader.TryReadByte(offset, out var byteValue))
                {
                    return false;
                }

                value = byteValue;
                return true;
            case 3:
                if (!reader.TryReadUInt16(offset, littleEndian, out var ushortValue))
                {
                    return false;
                }

                value = ushortValue;
                return true;
            case 4:
                if (!reader.TryReadUInt32(offset, littleEndian, out var uintValue))
                {
                    return false;
                }

                value = uintValue;
                return true;
            case 16:
            case 18:
                return reader.TryReadUInt64(offset, littleEndian, out value);
            default:
                return false;
        }
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
        double? pixelScaleX = null;
        double? pixelScaleY = null;
        AffinePixelTransform? modelTransformation = null;
        var modelTransformationSeen = false;

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = entriesOffset + index * entrySize;
            var tag = ReadUInt16(payload, entryOffset, littleEndian);
            if (tag == ModelPixelScaleTag)
            {
                if (TryReadPixelScale(
                        payload,
                        littleEndian,
                        entryOffset,
                        inlineSize,
                        isBigTiff,
                        out var scaleX,
                        out var scaleY))
                {
                    pixelScaleX = scaleX;
                    pixelScaleY = scaleY;
                }

                continue;
            }

            if (tag == ModelTransformationTag)
            {
                modelTransformationSeen = true;
                modelTransformation = TryReadModelTransformation(
                    payload,
                    littleEndian,
                    entryOffset,
                    inlineSize,
                    isBigTiff,
                    out var transformation)
                        ? transformation
                        : null;
                continue;
            }

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

        ResolveEffectivePixelScale(
            width.Value,
            height.Value,
            modelTransformationSeen,
            modelTransformation,
            ref pixelScaleX,
            ref pixelScaleY);

        metadata = new InlineRasterMetadata(
            (long)width,
            (long)height,
            (long)bands,
            (long)sampleBytes,
            pixelScaleX,
            pixelScaleY);
        return true;
    }

    private static bool TryReadPixelScale(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        int entryOffset,
        int inlineSize,
        bool isBigTiff,
        out double scaleX,
        out double scaleY)
    {
        scaleX = 0;
        scaleY = 0;
        if (ReadUInt16(payload, entryOffset + 2, littleEndian) != 12)
        {
            return false;
        }

        var count = isBigTiff
            ? ReadUInt64(payload, entryOffset + 4, littleEndian)
            : ReadUInt32(payload, entryOffset + 4, littleEndian);
        if (count is < 2 or > 16 || count > ulong.MaxValue / sizeof(double))
        {
            return false;
        }

        var valueBytes = count * sizeof(double);
        var valueFieldOffset = entryOffset + (isBigTiff ? 12 : 8);
        var dataOffset = valueBytes <= (ulong)inlineSize
            ? (ulong)valueFieldOffset
            : isBigTiff
                ? ReadUInt64(payload, valueFieldOffset, littleEndian)
                : ReadUInt32(payload, valueFieldOffset, littleEndian);
        if (dataOffset > int.MaxValue || !Contains(payload, (int)dataOffset, sizeof(double) * 2))
        {
            return false;
        }

        scaleX = ReadDouble(payload, (int)dataOffset, littleEndian);
        scaleY = ReadDouble(payload, (int)dataOffset + sizeof(double), littleEndian);
        return double.IsFinite(scaleX)
            && double.IsFinite(scaleY)
            && scaleX > 0
            && scaleY > 0;
    }

    private static bool TryReadModelTransformation(
        ReadOnlySpan<byte> payload,
        bool littleEndian,
        int entryOffset,
        int inlineSize,
        bool isBigTiff,
        out AffinePixelTransform transformation)
    {
        transformation = default;
        if (ReadUInt16(payload, entryOffset + 2, littleEndian) != 12)
        {
            return false;
        }

        var count = isBigTiff
            ? ReadUInt64(payload, entryOffset + 4, littleEndian)
            : ReadUInt32(payload, entryOffset + 4, littleEndian);
        if (count != 16)
        {
            return false;
        }

        const ulong valueBytes = 16 * sizeof(double);
        var valueFieldOffset = entryOffset + (isBigTiff ? 12 : 8);
        var dataOffset = valueBytes <= (ulong)inlineSize
            ? (ulong)valueFieldOffset
            : isBigTiff
                ? ReadUInt64(payload, valueFieldOffset, littleEndian)
                : ReadUInt32(payload, valueFieldOffset, littleEndian);
        if (dataOffset > int.MaxValue || !Contains(payload, (int)dataOffset, (int)valueBytes))
        {
            return false;
        }

        var offset = (int)dataOffset;
        transformation = new AffinePixelTransform(
            ReadDouble(payload, offset, littleEndian),
            ReadDouble(payload, offset + sizeof(double), littleEndian),
            ReadDouble(payload, offset + 4 * sizeof(double), littleEndian),
            ReadDouble(payload, offset + 5 * sizeof(double), littleEndian));
        return transformation.IsFinite;
    }

    private static void ResolveEffectivePixelScale(
        ulong width,
        ulong height,
        bool modelTransformationSeen,
        AffinePixelTransform? modelTransformation,
        ref double? pixelScaleX,
        ref double? pixelScaleY)
    {
        if (!modelTransformationSeen)
        {
            return;
        }

        pixelScaleX = null;
        pixelScaleY = null;
        if (modelTransformation is not { } transformation)
        {
            return;
        }

        var widthPixels = (double)width;
        var heightPixels = (double)height;
        var extentX = Math.Abs(transformation.XFromColumn) * widthPixels
            + Math.Abs(transformation.XFromRow) * heightPixels;
        var extentY = Math.Abs(transformation.YFromColumn) * widthPixels
            + Math.Abs(transformation.YFromRow) * heightPixels;
        if (!double.IsFinite(extentX)
            || !double.IsFinite(extentY)
            || extentX <= 0
            || extentY <= 0)
        {
            return;
        }

        pixelScaleX = extentX / widthPixels;
        pixelScaleY = extentY / heightPixels;
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

    private static double ReadDouble(ReadOnlySpan<byte> payload, int offset, bool littleEndian)
        => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(payload, offset, littleEndian)));

    private static bool Contains(ReadOnlySpan<byte> payload, int offset, int length)
        => offset >= 0 && length >= 0 && offset <= payload.Length - length;

    /// <summary>
    /// Provides bounded random access to an inline base64 payload. TIFF metadata can live well
    /// beyond the initial header (the first IFD offset is file-defined), so admission reads only
    /// the aligned base64 quanta that contain each required field instead of decoding the raster.
    /// </summary>
    private readonly struct Base64ByteReader
    {
        private readonly string _encoded;

        private Base64ByteReader(string encoded, int decodedLength)
        {
            _encoded = encoded;
            DecodedLength = decodedLength;
        }

        private int DecodedLength { get; }

        public static bool TryCreate(string encoded, out Base64ByteReader reader)
        {
            reader = default;
            var whitespaceCount = 0;
            foreach (var character in encoded)
            {
                if (char.IsWhiteSpace(character))
                {
                    whitespaceCount++;
                }
            }

            if (whitespaceCount > 0)
            {
                var normalized = GC.AllocateUninitializedArray<char>(encoded.Length - whitespaceCount);
                var destination = 0;
                foreach (var character in encoded)
                {
                    if (!char.IsWhiteSpace(character))
                    {
                        normalized[destination++] = character;
                    }
                }

                encoded = new string(normalized);
            }

            if (encoded.Length == 0 || encoded.Length % 4 != 0)
            {
                return false;
            }

            var padding = encoded[^1] == '=' ? 1 : 0;
            if (encoded.Length > 1 && encoded[^2] == '=')
            {
                padding++;
            }

            for (var index = 0; index < encoded.Length - padding; index++)
            {
                if (encoded[index] == '=')
                {
                    return false;
                }
            }

            var decodedLength = checked(encoded.Length / 4 * 3 - padding);
            reader = new Base64ByteReader(encoded, decodedLength);
            return true;
        }

        public bool Contains(ulong offset, long length)
            => length >= 0
                && offset <= (ulong)DecodedLength
                && (ulong)length <= (ulong)DecodedLength - offset;

        public bool TryReadByte(ulong offset, out byte value)
        {
            Span<byte> bytes = stackalloc byte[1];
            var read = TryReadBytes(offset, bytes);
            value = read ? bytes[0] : default;
            return read;
        }

        public bool TryReadUInt16(ulong offset, bool littleEndian, out ushort value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            if (!TryReadBytes(offset, bytes))
            {
                value = default;
                return false;
            }

            value = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt16BigEndian(bytes);
            return true;
        }

        public bool TryReadUInt32(ulong offset, bool littleEndian, out uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!TryReadBytes(offset, bytes))
            {
                value = default;
                return false;
            }

            value = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }

        public bool TryReadUInt64(ulong offset, bool littleEndian, out ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!TryReadBytes(offset, bytes))
            {
                value = default;
                return false;
            }

            value = littleEndian
                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt64BigEndian(bytes);
            return true;
        }

        public bool TryReadDouble(ulong offset, bool littleEndian, out double value)
        {
            if (!TryReadUInt64(offset, littleEndian, out var bits))
            {
                value = default;
                return false;
            }

            value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
            return true;
        }

        private bool TryReadBytes(ulong offset, Span<byte> destination)
        {
            if (destination.Length == 0 || !Contains(offset, destination.Length))
            {
                return false;
            }

            var byteOffset = checked((int)offset);
            var alignedByteOffset = byteOffset - byteOffset % 3;
            var leadingBytes = byteOffset - alignedByteOffset;
            var encodedOffset = alignedByteOffset / 3 * 4;
            var encodedLength = (leadingBytes + destination.Length + 2) / 3 * 4;
            if (encodedOffset > _encoded.Length - encodedLength)
            {
                return false;
            }

            // Primitive TIFF fields are at most eight bytes, plus two alignment bytes.
            Span<byte> decoded = stackalloc byte[12];
            return Convert.TryFromBase64Chars(
                    _encoded.AsSpan(encodedOffset, encodedLength),
                    decoded,
                    out var bytesWritten)
                && bytesWritten >= leadingBytes + destination.Length
                && CopyDecodedBytes(decoded, leadingBytes, destination);
        }

        private static bool CopyDecodedBytes(
            ReadOnlySpan<byte> decoded,
            int leadingBytes,
            Span<byte> destination)
        {
            decoded.Slice(leadingBytes, destination.Length).CopyTo(destination);
            return true;
        }
    }

    private readonly record struct AffinePixelTransform(
        double XFromColumn,
        double XFromRow,
        double YFromColumn,
        double YFromRow)
    {
        public bool IsFinite => double.IsFinite(XFromColumn)
            && double.IsFinite(XFromRow)
            && double.IsFinite(YFromColumn)
            && double.IsFinite(YFromRow);
    }
}

/// <summary>Trusted dimensions read from a bounded inline TIFF header.</summary>
internal readonly record struct InlineRasterMetadata(
    long Width,
    long Height,
    long Bands,
    long SampleBytes,
    double? PixelScaleX,
    double? PixelScaleY);
