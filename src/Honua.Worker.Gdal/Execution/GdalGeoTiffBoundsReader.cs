// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalGeoTiffBoundsReader
{
    private const ushort GeoKeyModelType = 1024;
    private const ushort GeoKeyGeographicType = 2048;
    private const ushort GeoKeyProjectedCsType = 3072;
    private const ushort GeoKeyModelTypeProjected = 1;
    private const ushort GeoKeyUserDefined = 32767;

    private enum TiffVariant
    {
        Classic,
        BigTiff,
    }

    private sealed class TiffIfdLayout
    {
        public TiffByteOrder ByteOrder { get; init; }

        public TiffVariant Variant { get; init; }

        public long EntriesStart { get; init; }

        public int EntryCount { get; init; }

        public int EntrySize { get; init; }
    }

    private struct GeoKeyAccumulator
    {
        private ushort _modelType;
        private int? _projectedEpsg;
        private int? _geographicEpsg;
        private bool _userDefinedProjected;

        public void Apply(ushort keyId, ushort valueOffset)
        {
            if (keyId == GeoKeyModelType)
            {
                _modelType = valueOffset;
                return;
            }

            if (keyId == GeoKeyProjectedCsType)
            {
                _userDefinedProjected |= valueOffset == GeoKeyUserDefined;
                if (valueOffset > 0 && valueOffset != GeoKeyUserDefined)
                {
                    _projectedEpsg = valueOffset;
                }

                return;
            }

            if (keyId == GeoKeyGeographicType && valueOffset > 0 && valueOffset != GeoKeyUserDefined)
            {
                _geographicEpsg = valueOffset;
            }
        }

        public readonly int? ResolveEpsg()
        {
            if (_projectedEpsg.HasValue)
            {
                return _projectedEpsg.Value;
            }

            return _userDefinedProjected || _modelType == GeoKeyModelTypeProjected
                ? null
                : _geographicEpsg;
        }
    }

    internal static GeoTiffBounds? ReadBounds(
        ReadOnlySpan<byte> data,
        GdalRasterDimensionGuard.RasterDimensions dims)
    {
        var layout = LocateFirstIfd(data);
        if (layout is null)
        {
            return null;
        }

        try
        {
            var tags = ScanGeoTiffTags(data, layout);
            var context = new BoundsBuildContext
            {
                ByteOrder = layout.ByteOrder,
                Dimensions = dims,
                Tags = tags,
                EpsgCode = ReadGeoKeyEpsg(data, layout.ByteOrder, tags),
            };
            return GdalGeoTiffBoundsGeometry.BuildBounds(data, context);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static TiffIfdLayout? LocateFirstIfd(ReadOnlySpan<byte> data)
    {
        if (GdalRasterDimensionGuard.ClassifyContainer(data) is not GdalRasterDimensionGuard.RasterContainerFormat.Tiff
            || data.Length < 8)
        {
            return null;
        }

        var order = data[0] == 0x49 ? TiffByteOrder.LittleEndian : TiffByteOrder.BigEndian;
        var magic = ReadU16(data, 2, order);
        return magic switch
        {
            42 => LocateClassicIfd(data, order),
            43 => LocateBigTiffIfd(data, order),
            _ => null,
        };
    }

    private static TiffIfdLayout? LocateClassicIfd(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        var ifdOffset = (long)ReadU32(data, 4, order);
        if (ifdOffset <= 0 || ifdOffset + 2 > data.Length)
        {
            return null;
        }

        int entryCount = ReadU16(data, (int)ifdOffset, order);
        var entriesStart = ifdOffset + 2;
        const int EntrySize = 12;
        if (entriesStart + ((long)entryCount * EntrySize) > data.Length)
        {
            return null;
        }

        return new TiffIfdLayout
        {
            ByteOrder = order,
            Variant = TiffVariant.Classic,
            EntriesStart = entriesStart,
            EntryCount = entryCount,
            EntrySize = EntrySize,
        };
    }

    private static TiffIfdLayout? LocateBigTiffIfd(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        if (data.Length < 16 || ReadU16(data, 4, order) != 8)
        {
            return null;
        }

        var ifdOffset = (long)ReadU64(data, 8, order);
        if (ifdOffset <= 0 || ifdOffset + 8 > data.Length)
        {
            return null;
        }

        var entryCountRaw = ReadU64(data, (int)ifdOffset, order);
        if (entryCountRaw > 4096)
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

        return new TiffIfdLayout
        {
            ByteOrder = order,
            Variant = TiffVariant.BigTiff,
            EntriesStart = entriesStart,
            EntryCount = entryCount,
            EntrySize = EntrySize,
        };
    }

    private static GeoTiffTagOffsets ScanGeoTiffTags(ReadOnlySpan<byte> data, TiffIfdLayout layout)
    {
        var offsets = new GeoTiffTagOffsets();
        var isBig = layout.Variant == TiffVariant.BigTiff;
        var valueDelta = isBig ? 12 : 8;
        for (var i = 0; i < layout.EntryCount; i++)
        {
            var pos = (int)(layout.EntriesStart + ((long)i * layout.EntrySize));
            var entry = new TiffTagEntry
            {
                Tag = ReadU16(data, pos, layout.ByteOrder),
                Type = ReadU16(data, pos + 2, layout.ByteOrder),
                Count = isBig
                    ? ReadU64(data, pos + 4, layout.ByteOrder)
                    : ReadU32(data, pos + 4, layout.ByteOrder),
                Offset = isBig
                    ? (long)ReadU64(data, pos + valueDelta, layout.ByteOrder)
                    : ReadU32(data, pos + valueDelta, layout.ByteOrder),
            };
            offsets.Record(entry);
        }

        return offsets;
    }

    private static int? ReadGeoKeyEpsg(ReadOnlySpan<byte> data, TiffByteOrder order, GeoTiffTagOffsets tags)
    {
        if (!tags.GeoKeyOffset.HasValue || tags.GeoKeyCount < 4)
        {
            return null;
        }

        var start = tags.GeoKeyOffset.Value;
        var byteLength = (long)tags.GeoKeyCount * 2;
        if (start < 0 || start + byteLength > data.Length)
        {
            return null;
        }

        var keyCount = ReadU16(data, (int)start + 6, order);
        var maxKeys = Math.Min(keyCount, (tags.GeoKeyCount - 4) / 4);
        var accumulator = new GeoKeyAccumulator();
        for (var i = 0; i < maxKeys; i++)
        {
            var entry = (int)start + 8 + (i * 8);
            if (ReadU16(data, entry + 2, order) != 0)
            {
                continue;
            }

            accumulator.Apply(ReadU16(data, entry, order), ReadU16(data, entry + 6, order));
        }

        return accumulator.ResolveEpsg();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
    {
        var slice = data.Slice(offset, 2);
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
            : BinaryPrimitives.ReadUInt16BigEndian(slice);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
    {
        var slice = data.Slice(offset, 4);
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
    {
        var slice = data.Slice(offset, 8);
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
            : BinaryPrimitives.ReadUInt64BigEndian(slice);
    }
}
