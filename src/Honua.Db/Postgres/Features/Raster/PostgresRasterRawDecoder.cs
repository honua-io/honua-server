// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>Reads in-db PostGIS raster WKB into lossless, little-endian planar samples.</summary>
internal static class PostgresRasterRawDecoder
{
    private const int HeaderLength = 61;
    private const int MaxSampleBytes = 256 * 1024 * 1024;

    internal static RasterResult Decode(ReadOnlySpan<byte> wkb)
    {
        Require(wkb, 0, HeaderLength);
        if (wkb[0] is not (0 or 1)) throw new InvalidDataException("Invalid raster byte order.");
        var little = wkb[0] == 1;
        if (ReadUInt16(wkb[1..], little) != 0) throw new NotSupportedException("Unsupported raster WKB version.");
        var bandCount = ReadUInt16(wkb[3..], little);
        var width = ReadUInt16(wkb[57..], little);
        var height = ReadUInt16(wkb[59..], little);
        if (width == 0 || height == 0 || bandCount == 0) throw new InvalidDataException("Empty raster pixel layout.");
        var pixels = checked((int)width * height);
        var offset = HeaderLength;
        byte[]? data = null;
        var masks = new byte[bandCount][];
        var sampleType = -1;
        var sampleBytes = 0;
        string? pixelType = null;
        for (var band = 0; band < bandCount; band++)
        {
            Require(wkb, offset, 1);
            var flags = wkb[offset++];
            if ((flags & 0x80) != 0) throw new NotSupportedException("Out-db bands must be materialized before export.");
            var type = flags & 0x0f;
            var layout = type switch
            {
                0 => (1, "1BB"),
                1 => (1, "2BUI"),
                2 => (1, "4BUI"),
                3 => (1, "8BSI"),
                4 => (1, "8BUI"),
                5 => (2, "16BSI"),
                6 => (2, "16BUI"),
                7 => (4, "32BSI"),
                8 => (4, "32BUI"),
                10 => (4, "32BF"),
                11 => (8, "64BF"),
                _ => throw new NotSupportedException("Unsupported raster sample type.")
            };
            if (band == 0)
            {
                sampleType = type;
                sampleBytes = layout.Item1;
                pixelType = layout.Item2;
                var total = checked((long)pixels * sampleBytes * bandCount);
                if (total > MaxSampleBytes) throw new NotSupportedException("Raw raster exceeds the sample byte budget.");
                data = new byte[(int)total];
            }
            else if (type != sampleType)
            {
                throw new NotSupportedException("Raw export requires one common sample type across bands.");
            }
            var bandLength = checked(pixels * sampleBytes);
            Require(wkb, offset, checked(sampleBytes + bandLength));
            var noData = ReadSample(wkb.Slice(offset, sampleBytes), type, little);
            offset += sampleBytes;
            var source = wkb.Slice(offset, bandLength);
            var target = data!.AsSpan(band * bandLength, bandLength);
            masks[band] = new byte[(pixels + 7) / 8];
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                var sample = source.Slice(pixel * sampleBytes, sampleBytes);
                var destination = target.Slice(pixel * sampleBytes, sampleBytes);
                sample.CopyTo(destination);
                if (!little && sampleBytes > 1) destination.Reverse();
                var value = ReadSample(sample, type, little);
                // NoData is a stored sentinel, not a computed measurement. Match it exactly:
                // a tolerance would mask adjacent valid samples. Equals also matches NaN sentinels.
                var invalid = (flags & 0x20) != 0 || ((flags & 0x40) != 0 &&
                    value.Equals(noData));
                if (!invalid) masks[band][pixel / 8] |= (byte)(0x80 >> (pixel % 8));
            }
            offset += bandLength;
        }
        if (offset != wkb.Length) throw new InvalidDataException("Unexpected trailing raster bytes.");
        var scaleX = ReadDouble(wkb[5..], little);
        var scaleY = ReadDouble(wkb[13..], little);
        var x = ReadDouble(wkb[21..], little);
        var y = ReadDouble(wkb[29..], little);
        var skewX = ReadDouble(wkb[37..], little);
        var skewY = ReadDouble(wkb[45..], little);
        var srid = little ? BinaryPrimitives.ReadInt32LittleEndian(wkb[53..]) : BinaryPrimitives.ReadInt32BigEndian(wkb[53..]);
        var xs = new[] { x, x + width * scaleX, x + height * skewX, x + width * scaleX + height * skewX };
        var ys = new[] { y, y + width * skewY, y + height * scaleY, y + width * skewY + height * scaleY };
        return new RasterResult
        {
            Data = data!,
            ContentType = "application/octet-stream",
            Width = width,
            Height = height,
            BandCount = bandCount,
            PixelType = pixelType,
            BandValidityMasks = masks,
            Srid = srid,
            GeoTransform = [x, scaleX, skewX, y, skewY, scaleY],
            Extent = new RasterExtent { XMin = xs.Min(), XMax = xs.Max(), YMin = ys.Min(), YMax = ys.Max(), Srid = srid }
        };
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length) throw new InvalidDataException("Truncated raster WKB.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static double ReadDouble(ReadOnlySpan<byte> data, bool little)
        => little ? BinaryPrimitives.ReadDoubleLittleEndian(data) : BinaryPrimitives.ReadDoubleBigEndian(data);

    private static double ReadSample(ReadOnlySpan<byte> data, int type, bool little) => type switch
    {
        0 or 1 or 2 or 4 => data[0],
        3 => unchecked((sbyte)data[0]),
        5 => little ? BinaryPrimitives.ReadInt16LittleEndian(data) : BinaryPrimitives.ReadInt16BigEndian(data),
        6 => ReadUInt16(data, little),
        7 => little ? BinaryPrimitives.ReadInt32LittleEndian(data) : BinaryPrimitives.ReadInt32BigEndian(data),
        8 => little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data),
        10 => little ? BinaryPrimitives.ReadSingleLittleEndian(data) : BinaryPrimitives.ReadSingleBigEndian(data),
        11 => ReadDouble(data, little),
        _ => throw new NotSupportedException("Unsupported raster sample type.")
    };
}
