// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalTiffDimensionReader
{
    internal const int MaxIfdEntries = 4096;

    private const int TagImageWidth = 256;
    private const int TagImageLength = 257;
    private const int TagBitsPerSample = 258;
    private const int TagSamplesPerPixel = 277;

    private const int TypeByte = 1;
    private const int TypeShort = 3;
    private const int TypeLong = 4;
    private const int TypeSByte = 6;
    private const int TypeSShort = 8;
    private const int TypeSLong = 9;
    private const int TypeLong8 = 16;
    private const int TypeSLong8 = 17;

    private readonly record struct IfdLayout(
        TiffByteOrder ByteOrder,
        long EntriesStart,
        int EntryCount,
        int EntrySize,
        int InlineWidth);

    private readonly record struct TagEntry(int Tag, int Type, long Count, int ValueFieldOffset);

    private sealed class DimensionAccumulator
    {
        public long Width { get; private set; }

        public long Height { get; private set; }

        public long Samples { get; private set; }

        public long Bits { get; private set; }

        public bool Invalid { get; private set; }

        public void Apply(ReadOnlySpan<byte> data, IfdLayout layout, TagEntry entry)
        {
            switch (entry.Tag)
            {
                case TagImageWidth:
                    RecordWidth(ReadScalarTag(data, layout, entry));
                    break;
                case TagImageLength:
                    RecordHeight(ReadScalarTag(data, layout, entry));
                    break;
                case TagSamplesPerPixel:
                    RecordSamples(ReadScalarTag(data, layout, entry));
                    break;
                case TagBitsPerSample:
                    RecordBits(ReadBitsPerSample(data, layout, entry));
                    break;
            }
        }

        private void RecordWidth(long? value)
        {
            if (value is not > 0)
            {
                Invalid = true;
                return;
            }

            Width = Math.Max(Width, value.Value);
        }

        private void RecordHeight(long? value)
        {
            if (value is not > 0)
            {
                Invalid = true;
                return;
            }

            Height = Math.Max(Height, value.Value);
        }

        private void RecordSamples(long? value)
        {
            if (value is not > 0)
            {
                Invalid = true;
                return;
            }

            Samples = Math.Max(Samples, value.Value);
        }

        private void RecordBits(long? value)
        {
            if (value is > 0)
            {
                Bits = Math.Max(Bits, value.Value);
            }
        }

        public GdalRasterDimensionGuard.RasterDimensions? Build()
        {
            if (Invalid || Width <= 0 || Height <= 0)
            {
                return null;
            }

            var resolvedSamples = Samples < 1 ? 1L : Samples;
            var resolvedBits = Bits < 1 ? 8L : Bits;
            var bands = resolvedSamples > int.MaxValue ? int.MaxValue : (int)resolvedSamples;
            var bitsPerSample = resolvedBits > int.MaxValue ? int.MaxValue : (int)resolvedBits;
            return new GdalRasterDimensionGuard.RasterDimensions(Width, Height, bands, bitsPerSample);
        }
    }

    public static bool IsTiffMagic(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        var isLittle = data[0] == 0x49 && data[1] == 0x49
            && data[3] == 0x00 && (data[2] == 0x2A || data[2] == 0x2B);
        var isBig = data[0] == 0x4D && data[1] == 0x4D
            && data[2] == 0x00 && (data[3] == 0x2A || data[3] == 0x2B);
        return isLittle || isBig;
    }

    public static GdalRasterDimensionGuard.RasterDimensions? Read(ReadOnlySpan<byte> data)
    {
        if (!IsTiffMagic(data) || data.Length < 8)
        {
            return null;
        }

        var order = data[0] == 0x49 ? TiffByteOrder.LittleEndian : TiffByteOrder.BigEndian;
        var layout = LocateFirstIfd(data, order);
        if (layout is null)
        {
            return null;
        }

        return ScanIfd(data, layout.Value);
    }

    private static IfdLayout? LocateFirstIfd(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        var version = ReadU16(data, 2, order);
        return version switch
        {
            42 => LocateClassicIfd(data, order),
            43 => LocateBigTiffIfd(data, order),
            _ => null,
        };
    }

    private static IfdLayout? LocateClassicIfd(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        var ifdOffset = (long)ReadU32(data, 4, order);
        if (ifdOffset < 8 || ifdOffset + 2 > data.Length)
        {
            return null;
        }

        int entryCount = ReadU16(data, (int)ifdOffset, order);
        var entriesStart = ifdOffset + 2;
        const int EntrySize = 12;
        if (entryCount <= 0 || entryCount > MaxIfdEntries || entriesStart + ((long)entryCount * EntrySize) > data.Length)
        {
            return null;
        }

        return new IfdLayout(order, entriesStart, entryCount, EntrySize, InlineWidth: 4);
    }

    private static IfdLayout? LocateBigTiffIfd(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        if (data.Length < 16 || ReadU16(data, 4, order) != 8 || ReadU16(data, 6, order) != 0)
        {
            return null;
        }

        var ifdOffsetRaw = ReadU64(data, 8, order);
        if (ifdOffsetRaw < 16 || ifdOffsetRaw > (ulong)(data.Length - 8))
        {
            return null;
        }

        var ifdOffset = (long)ifdOffsetRaw;
        var entryCountRaw = ReadU64(data, (int)ifdOffset, order);
        if (entryCountRaw == 0 || entryCountRaw > MaxIfdEntries)
        {
            return null;
        }

        var entryCount = (int)entryCountRaw;
        var entriesStart = ifdOffset + 8;
        const int EntrySize = 20;
        if (entriesStart + ((long)entryCount * EntrySize) > data.Length)
        {
            return null;
        }

        return new IfdLayout(order, entriesStart, entryCount, EntrySize, InlineWidth: 8);
    }

    private static GdalRasterDimensionGuard.RasterDimensions? ScanIfd(ReadOnlySpan<byte> data, IfdLayout layout)
    {
        var accumulator = new DimensionAccumulator();
        for (var i = 0; i < layout.EntryCount; i++)
        {
            var entry = ReadTagEntry(data, layout, i);
            accumulator.Apply(data, layout, entry);
            if (accumulator.Invalid)
            {
                return null;
            }
        }

        return accumulator.Build();
    }

    private static TagEntry ReadTagEntry(ReadOnlySpan<byte> data, IfdLayout layout, int index)
    {
        var pos = (int)(layout.EntriesStart + ((long)index * layout.EntrySize));
        int tag = ReadU16(data, pos, layout.ByteOrder);
        int type = ReadU16(data, pos + 2, layout.ByteOrder);
        if (layout.InlineWidth == 8)
        {
            var countRaw = ReadU64(data, pos + 4, layout.ByteOrder);
            long count = countRaw > long.MaxValue ? long.MaxValue : (long)countRaw;
            return new TagEntry(tag, type, count, pos + 12);
        }

        return new TagEntry(tag, type, ReadU32(data, pos + 4, layout.ByteOrder), pos + 8);
    }

    private static long? ReadScalarTag(ReadOnlySpan<byte> data, IfdLayout layout, TagEntry entry)
    {
        if (entry.Count != 1)
        {
            return null;
        }

        return entry.Type switch
        {
            TypeByte => data[entry.ValueFieldOffset],
            TypeShort => ReadU16(data, entry.ValueFieldOffset, layout.ByteOrder),
            TypeLong => ReadU32(data, entry.ValueFieldOffset, layout.ByteOrder),
            TypeSByte => ReadNonNegative((sbyte)data[entry.ValueFieldOffset]),
            TypeSShort => ReadNonNegative(ReadI16(data, entry.ValueFieldOffset, layout.ByteOrder)),
            TypeSLong => ReadNonNegative(ReadI32(data, entry.ValueFieldOffset, layout.ByteOrder)),
            TypeLong8 when layout.InlineWidth == 8 => ReadClampedU64(data, entry.ValueFieldOffset, layout.ByteOrder),
            TypeSLong8 when layout.InlineWidth == 8 => ReadNonNegative(ReadI64(data, entry.ValueFieldOffset, layout.ByteOrder)),
            _ => null,
        };
    }

    private static long? ReadBitsPerSample(ReadOnlySpan<byte> data, IfdLayout layout, TagEntry entry)
    {
        if (entry.Type != TypeShort)
        {
            return null;
        }

        var sampleCount = entry.Count > 0 ? entry.Count : 1L;
        var readAtLong = sampleCount <= layout.InlineWidth / 2
            ? entry.ValueFieldOffset
            : ReadOffset(data, layout, entry.ValueFieldOffset);
        if (readAtLong < 0 || readAtLong + 2 > data.Length)
        {
            return null;
        }

        var readAt = (int)readAtLong;
        var toRead = (int)Math.Min(sampleCount, Math.Min(512L, (data.Length - readAt) / 2L));
        long maxBits = 0;
        for (var idx = 0; idx < toRead; idx++)
        {
            maxBits = Math.Max(maxBits, ReadU16(data, readAt + (idx * 2), layout.ByteOrder));
        }

        return maxBits > 0 ? maxBits : null;
    }

    private static long ReadOffset(ReadOnlySpan<byte> data, IfdLayout layout, int offset)
    {
        if (layout.InlineWidth != 8)
        {
            return ReadU32(data, offset, layout.ByteOrder);
        }

        var raw = ReadU64(data, offset, layout.ByteOrder);
        return raw > long.MaxValue ? -1L : (long)raw;
    }

    private static long? ReadNonNegative(long value) => value >= 0 ? value : null;

    private static long ReadClampedU64(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
    {
        var raw = ReadU64(data, offset, order);
        return raw > long.MaxValue ? long.MaxValue : (long)raw;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static short ReadI16(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    private static int ReadI32(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8))
            : BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));

    private static long ReadI64(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
        => order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8))
            : BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8));
}
