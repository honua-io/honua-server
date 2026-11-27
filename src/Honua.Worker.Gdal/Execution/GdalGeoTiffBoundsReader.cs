// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;

namespace Honua.Worker.Gdal.Execution;

internal sealed class GeoTiffBounds
{
    public double MinX { get; init; }

    public double MinY { get; init; }

    public double MaxX { get; init; }

    public double MaxY { get; init; }

    public double ScaleX { get; init; }

    public double ScaleY { get; init; }

    public int? EpsgCode { get; init; }
}

internal static class GdalGeoTiffBoundsReader
{
    private const int TagModelPixelScale = 33550;
    private const int TagModelTiepoint = 33922;
    private const int TagModelTransformation = 34264;
    private const int TagGeoKeyDirectory = 34735;
    private const int TypeShort = 3;
    private const int TypeDouble = 12;

    private const ushort GeoKeyModelType = 1024;
    private const ushort GeoKeyGeographicType = 2048;
    private const ushort GeoKeyProjectedCsType = 3072;
    private const ushort GeoKeyModelTypeProjected = 1;
    private const ushort GeoKeyUserDefined = 32767;

    private enum TiffByteOrder
    {
        LittleEndian,
        BigEndian,
    }

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

    private sealed class GeoTiffTagOffsets
    {
        public long? ScaleOffset { get; set; }

        public long? TiepointOffset { get; set; }

        public long? TransformOffset { get; set; }

        public long? GeoKeyOffset { get; set; }

        public int GeoKeyCount { get; set; }

        public void Record(TiffTagEntry entry)
        {
            if (entry.Tag == TagModelPixelScale && entry.Type == TypeDouble && entry.Count >= 2)
            {
                ScaleOffset = entry.Offset;
            }
            else if (entry.Tag == TagModelTiepoint && entry.Type == TypeDouble && entry.Count >= 6)
            {
                TiepointOffset = entry.Offset;
            }
            else if (entry.Tag == TagModelTransformation && entry.Type == TypeDouble && entry.Count >= 16)
            {
                TransformOffset = entry.Offset;
            }
            else if (entry.Tag == TagGeoKeyDirectory && entry.Type == TypeShort && entry.Count >= 4)
            {
                GeoKeyOffset = entry.Offset;
                GeoKeyCount = (int)Math.Min(entry.Count, 4096UL);
            }
        }
    }

    private sealed class TiffTagEntry
    {
        public int Tag { get; init; }

        public int Type { get; init; }

        public ulong Count { get; init; }

        public long Offset { get; init; }
    }

    private sealed class BoundsBuildContext
    {
        public TiffByteOrder ByteOrder { get; init; }

        public GdalRasterDimensionGuard.RasterDimensions Dimensions { get; init; }

        public required GeoTiffTagOffsets Tags { get; init; }

        public int? EpsgCode { get; init; }
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
            return BuildBoundsFromScaleAndTiepoint(data, context)
                ?? BuildBoundsFromTransformation(data, context);
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

    private static GeoTiffBounds? BuildBoundsFromScaleAndTiepoint(
        ReadOnlySpan<byte> data,
        BoundsBuildContext context)
    {
        var values = ReadScaleAndTiepoint(data, context);
        if (values is null)
        {
            return null;
        }

        var minX = values.X - (values.I * values.ScaleX);
        var maxY = values.Y + (values.J * values.ScaleY);
        return ValidateFiniteBounds(new GeoTiffBounds
        {
            MinX = minX,
            MinY = maxY - (context.Dimensions.Height * values.ScaleY),
            MaxX = minX + (context.Dimensions.Width * values.ScaleX),
            MaxY = maxY,
            ScaleX = values.ScaleX,
            ScaleY = values.ScaleY,
            EpsgCode = context.EpsgCode,
        });
    }

    private sealed class ScaleAndTiepoint
    {
        public double ScaleX { get; init; }

        public double ScaleY { get; init; }

        public double I { get; init; }

        public double J { get; init; }

        public double X { get; init; }

        public double Y { get; init; }
    }

    private static ScaleAndTiepoint? ReadScaleAndTiepoint(ReadOnlySpan<byte> data, BoundsBuildContext context)
    {
        if (!context.Tags.ScaleOffset.HasValue || !context.Tags.TiepointOffset.HasValue)
        {
            return null;
        }

        var scaleOff = context.Tags.ScaleOffset.Value;
        var tieOff = context.Tags.TiepointOffset.Value;
        if (scaleOff < 0 || scaleOff + 16 > data.Length || tieOff < 0 || tieOff + 48 > data.Length)
        {
            return null;
        }

        var sx = ReadDouble(data, (int)scaleOff, context.ByteOrder);
        var sy = ReadDouble(data, (int)scaleOff + 8, context.ByteOrder);
        var i = ReadDouble(data, (int)tieOff, context.ByteOrder);
        var j = ReadDouble(data, (int)tieOff + 8, context.ByteOrder);
        var x = ReadDouble(data, (int)tieOff + 24, context.ByteOrder);
        var y = ReadDouble(data, (int)tieOff + 32, context.ByteOrder);
        if (!IsPositiveFinite(sx) || !IsPositiveFinite(sy) || !AreFinite(i, j) || !AreFinite(x, y))
        {
            return null;
        }

        return new ScaleAndTiepoint
        {
            ScaleX = sx,
            ScaleY = sy,
            I = i,
            J = j,
            X = x,
            Y = y,
        };
    }

    private sealed class AffineTransform
    {
        public double M0 { get; init; }

        public double M1 { get; init; }

        public double M3 { get; init; }

        public double M4 { get; init; }

        public double M5 { get; init; }

        public double M7 { get; init; }

        public double ScaleX { get; init; }

        public double ScaleY { get; init; }
    }

    private static AffineTransform? ReadAffineTransform(ReadOnlySpan<byte> data, BoundsBuildContext context)
    {
        if (!context.Tags.TransformOffset.HasValue)
        {
            return null;
        }

        var offset = context.Tags.TransformOffset.Value;
        if (offset < 0 || offset + (16 * 8) > data.Length)
        {
            return null;
        }

        var order = context.ByteOrder;
        var m0 = ReadDouble(data, (int)offset, order);
        var m1 = ReadDouble(data, (int)offset + 8, order);
        var m3 = ReadDouble(data, (int)offset + 24, order);
        var m4 = ReadDouble(data, (int)offset + 32, order);
        var m5 = ReadDouble(data, (int)offset + 40, order);
        var m7 = ReadDouble(data, (int)offset + 56, order);
        var sx = double.Hypot(m0, m4);
        var sy = double.Hypot(m1, m5);
        if (!IsPositiveFinite(sx) || !IsPositiveFinite(sy))
        {
            return null;
        }

        return new AffineTransform
        {
            M0 = m0,
            M1 = m1,
            M3 = m3,
            M4 = m4,
            M5 = m5,
            M7 = m7,
            ScaleX = sx,
            ScaleY = sy,
        };
    }

    private static GeoTiffBounds? BuildBoundsFromTransformation(
        ReadOnlySpan<byte> data,
        BoundsBuildContext context)
    {
        var t = ReadAffineTransform(data, context);
        if (t is null)
        {
            return null;
        }

        var w = context.Dimensions.Width;
        var h = context.Dimensions.Height;
        double[] xs = [t.M3, (t.M0 * w) + t.M3, (t.M1 * h) + t.M3, (t.M0 * w) + (t.M1 * h) + t.M3];
        double[] ys = [t.M7, (t.M4 * w) + t.M7, (t.M5 * h) + t.M7, (t.M4 * w) + (t.M5 * h) + t.M7];
        return ValidateFiniteBounds(new GeoTiffBounds
        {
            MinX = xs.Min(),
            MinY = ys.Min(),
            MaxX = xs.Max(),
            MaxY = ys.Max(),
            ScaleX = t.ScaleX,
            ScaleY = t.ScaleY,
            EpsgCode = context.EpsgCode,
        });
    }

    private static GeoTiffBounds? ValidateFiniteBounds(GeoTiffBounds candidate)
        => AreFinite(candidate.MinX, candidate.MaxX) && AreFinite(candidate.MinY, candidate.MaxY)
            ? candidate
            : null;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0d;

    private static bool AreFinite(double a, double b) => double.IsFinite(a) && double.IsFinite(b);

    private static double ReadDouble(ReadOnlySpan<byte> data, int offset, TiffByteOrder order)
    {
        var slice = data.Slice(offset, 8);
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
            : BinaryPrimitives.ReadDoubleBigEndian(slice);
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
