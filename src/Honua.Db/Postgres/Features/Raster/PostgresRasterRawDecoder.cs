// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

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
            var noDataSpan = wkb.Slice(offset, sampleBytes);
            offset += sampleBytes;
            var source = wkb.Slice(offset, bandLength);
            var target = data!.AsSpan(band * bandLength, bandLength);
            var mask = new byte[(pixels + 7) / 8];
            masks[band] = mask;

            if (little || sampleBytes == 1)
            {
                source.CopyTo(target);
            }
            else
            {
                CopyBigEndianToLittleEndian(source, target, sampleBytes, pixels);
            }

            var isNoDataBand = (flags & 0x20) != 0;
            if (!isNoDataBand)
            {
                var hasNoData = (flags & 0x40) != 0;
                if (!hasNoData)
                {
                    FillAllValidMask(mask, pixels);
                }
                else
                {
                    PopulateValidityMask(target, noDataSpan, type, little, pixels, mask);
                }
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

    private static void CopyBigEndianToLittleEndian(
        ReadOnlySpan<byte> source,
        Span<byte> target,
        int sampleBytes,
        int pixels)
    {
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var offset = pixel * sampleBytes;
            var destination = target.Slice(offset, sampleBytes);
            source.Slice(offset, sampleBytes).CopyTo(destination);
            destination.Reverse();
        }
    }

    private static void FillAllValidMask(byte[] mask, int pixels)
    {
        mask.AsSpan().Fill(0xFF);
        var remainder = pixels & 7;
        if (remainder != 0)
        {
            mask[^1] = (byte)(0xFF << (8 - remainder));
        }
    }

    // Target bytes are already normalized to little-endian; hoist the sample type switch outside
    // the pixel loop and scan typed spans directly. NoData is a stored sentinel, so exact equality
    // (including NaN matching NaN) is required.
    private static void PopulateValidityMask(
        ReadOnlySpan<byte> littleEndianTarget,
        ReadOnlySpan<byte> noDataSpan,
        int type,
        bool sourceLittle,
        int pixels,
        byte[] mask)
    {
        if (!BitConverter.IsLittleEndian)
        {
            var noData = ReadSample(noDataSpan, type, sourceLittle);
            var sampleBytes = littleEndianTarget.Length / pixels;
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                var value = ReadSample(littleEndianTarget.Slice(pixel * sampleBytes, sampleBytes), type, little: true);
                if (value.CompareTo(noData) != 0)
                {
                    mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
                }
            }

            return;
        }

        switch (type)
        {
            case 0 or 1 or 2 or 3 or 4:
                PopulateEqualityMask(littleEndianTarget, noDataSpan[0], mask);
                break;
            case 5:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, short>(littleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadInt16LittleEndian(noDataSpan) : BinaryPrimitives.ReadInt16BigEndian(noDataSpan),
                    mask);
                break;
            case 6:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, ushort>(littleEndianTarget),
                    ReadUInt16(noDataSpan, sourceLittle),
                    mask);
                break;
            case 7:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, int>(littleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadInt32LittleEndian(noDataSpan) : BinaryPrimitives.ReadInt32BigEndian(noDataSpan),
                    mask);
                break;
            case 8:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, uint>(littleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadUInt32LittleEndian(noDataSpan) : BinaryPrimitives.ReadUInt32BigEndian(noDataSpan),
                    mask);
                break;
            case 10:
                PopulateFloatMask(
                    MemoryMarshal.Cast<byte, float>(littleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadSingleLittleEndian(noDataSpan) : BinaryPrimitives.ReadSingleBigEndian(noDataSpan),
                    mask);
                break;
            case 11:
                PopulateDoubleMask(
                    MemoryMarshal.Cast<byte, double>(littleEndianTarget),
                    ReadDouble(noDataSpan, sourceLittle),
                    mask);
                break;
            default:
                throw new NotSupportedException("Unsupported raster sample type.");
        }
    }

    private static void PopulateEqualityMask<T>(ReadOnlySpan<T> samples, T noData, byte[] mask)
        where T : unmanaged, IEquatable<T>
    {
        for (var pixel = 0; pixel < samples.Length; pixel++)
        {
            if (!samples[pixel].Equals(noData))
            {
                mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
            }
        }
    }

    private static void PopulateFloatMask(ReadOnlySpan<float> samples, float noData, byte[] mask)
    {
        if (float.IsNaN(noData))
        {
            for (var pixel = 0; pixel < samples.Length; pixel++)
            {
                if (!float.IsNaN(samples[pixel]))
                {
                    mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
                }
            }

            return;
        }

        for (var pixel = 0; pixel < samples.Length; pixel++)
        {
            if (samples[pixel].CompareTo(noData) != 0)
            {
                mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
            }
        }
    }

    private static void PopulateDoubleMask(ReadOnlySpan<double> samples, double noData, byte[] mask)
    {
        if (double.IsNaN(noData))
        {
            for (var pixel = 0; pixel < samples.Length; pixel++)
            {
                if (!double.IsNaN(samples[pixel]))
                {
                    mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
                }
            }

            return;
        }

        for (var pixel = 0; pixel < samples.Length; pixel++)
        {
            if (samples[pixel].CompareTo(noData) != 0)
            {
                mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
            }
        }
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
