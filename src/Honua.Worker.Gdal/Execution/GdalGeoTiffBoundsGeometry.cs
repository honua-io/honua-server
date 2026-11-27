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

internal enum TiffByteOrder
{
    LittleEndian,
    BigEndian,
}

internal sealed class GeoTiffTagOffsets
{
    private const int TagModelPixelScale = 33550;
    private const int TagModelTiepoint = 33922;
    private const int TagModelTransformation = 34264;
    private const int TagGeoKeyDirectory = 34735;
    private const int TypeShort = 3;
    private const int TypeDouble = 12;

    public long? ScaleOffset { get; private set; }

    public long? TiepointOffset { get; private set; }

    public long? TransformOffset { get; private set; }

    public long? GeoKeyOffset { get; private set; }

    public int GeoKeyCount { get; private set; }

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

internal sealed class TiffTagEntry
{
    public int Tag { get; init; }

    public int Type { get; init; }

    public ulong Count { get; init; }

    public long Offset { get; init; }
}

internal sealed class BoundsBuildContext
{
    public TiffByteOrder ByteOrder { get; init; }

    public GdalRasterDimensionGuard.RasterDimensions Dimensions { get; init; }

    public required GeoTiffTagOffsets Tags { get; init; }

    public int? EpsgCode { get; init; }
}

internal static class GdalGeoTiffBoundsGeometry
{
    private sealed class ScaleAndTiepoint
    {
        public double ScaleX { get; init; }

        public double ScaleY { get; init; }

        public double I { get; init; }

        public double J { get; init; }

        public double X { get; init; }

        public double Y { get; init; }
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

    internal static GeoTiffBounds? BuildBounds(ReadOnlySpan<byte> data, BoundsBuildContext context)
        => BuildBoundsFromScaleAndTiepoint(data, context)
            ?? BuildBoundsFromTransformation(data, context);

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
}
