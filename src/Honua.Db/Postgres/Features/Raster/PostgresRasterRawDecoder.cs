// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

internal static partial class PostgresRasterRawDecoder
{
    private const int HeaderLength = 61;
    private const int MaxSampleBytes = 256 * 1024 * 1024;

    private enum RasterByteOrder
    {
        BigEndian,
        LittleEndian
    }

    private readonly struct RasterHeader
    {
        public required RasterByteOrder ByteOrder { get; init; }
        public required ushort BandCount { get; init; }
        public required ushort Width { get; init; }
        public required ushort Height { get; init; }
        public required int Pixels { get; init; }
    }

    private sealed class BandDecodeState
    {
        public int Offset { get; set; } = HeaderLength;
        public byte[]? Data { get; set; }
        public required byte[][] Masks { get; init; }
        public int SampleType { get; set; } = -1;
        public int SampleBytes { get; set; }
        public string? PixelType { get; set; }
    }

    internal static RasterResult Decode(ReadOnlySpan<byte> wkb)
    {
        var header = ReadRasterHeader(wkb);
        var state = new BandDecodeState { Masks = new byte[header.BandCount][] };
        for (var band = 0; band < header.BandCount; band++)
        {
            DecodeBand(wkb, header, state);
        }

        if (state.Offset != wkb.Length)
        {
            throw new InvalidDataException("Unexpected trailing raster bytes.");
        }

        return BuildRasterResult(wkb, header, state);
    }

    private static RasterHeader ReadRasterHeader(ReadOnlySpan<byte> wkb)
    {
        Require(wkb, 0, HeaderLength);
        if (wkb[0] is not (0 or 1)) throw new InvalidDataException("Invalid raster byte order.");
        var little = wkb[0] == 1;
        if (ReadUInt16(wkb[1..], little) != 0) throw new NotSupportedException("Unsupported raster WKB version.");
        var bandCount = ReadUInt16(wkb[3..], little);
        var width = ReadUInt16(wkb[57..], little);
        var height = ReadUInt16(wkb[59..], little);
        if (width == 0 || height == 0 || bandCount == 0) throw new InvalidDataException("Empty raster pixel layout.");
        return new RasterHeader
        {
            ByteOrder = little ? RasterByteOrder.LittleEndian : RasterByteOrder.BigEndian,
            BandCount = bandCount,
            Width = width,
            Height = height,
            Pixels = checked((int)width * height)
        };
    }

    private static (int SampleBytes, string PixelType) ResolveSampleLayout(int type) => type switch
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

    private static void DecodeBand(ReadOnlySpan<byte> wkb, RasterHeader header, BandDecodeState state)
    {
        Require(wkb, state.Offset, 1);
        var flags = wkb[state.Offset++];
        if ((flags & 0x80) != 0) throw new NotSupportedException("Out-db bands must be materialized before export.");
        var type = flags & 0x0f;
        var bandIndex = InitializeOrValidateBandLayout(header, state, type);

        var bandLength = checked(header.Pixels * state.SampleBytes);
        Require(wkb, state.Offset, checked(state.SampleBytes + bandLength));
        var noDataSpan = wkb.Slice(state.Offset, state.SampleBytes);
        state.Offset += state.SampleBytes;
        var source = wkb.Slice(state.Offset, bandLength);
        var target = state.Data!.AsSpan(bandIndex * bandLength, bandLength);
        var mask = new byte[(header.Pixels + 7) / 8];
        state.Masks[bandIndex] = mask;

        if (header.ByteOrder == RasterByteOrder.LittleEndian || state.SampleBytes == 1)
        {
            source.CopyTo(target);
        }
        else
        {
            CopyBigEndianToLittleEndian(source, target, state.SampleBytes);
        }

        PopulateBandValidityMask(flags, new ValidityMaskRequest
        {
            LittleEndianTarget = target,
            NoDataSpan = noDataSpan,
            SampleType = type,
            SourceByteOrder = header.ByteOrder,
            Pixels = header.Pixels,
            Mask = mask
        });

        state.Offset += bandLength;
    }

    private static int InitializeOrValidateBandLayout(RasterHeader header, BandDecodeState state, int type)
    {
        var layout = ResolveSampleLayout(type);
        if (state.Data is null)
        {
            state.SampleType = type;
            state.SampleBytes = layout.SampleBytes;
            state.PixelType = layout.PixelType;
            var total = checked((long)header.Pixels * state.SampleBytes * header.BandCount);
            if (total > MaxSampleBytes) throw new NotSupportedException("Raw raster exceeds the sample byte budget.");
            state.Data = new byte[(int)total];
            return 0;
        }

        if (type != state.SampleType)
        {
            throw new NotSupportedException("Raw export requires one common sample type across bands.");
        }

        return Array.IndexOf(state.Masks, null);
    }

    private static RasterResult BuildRasterResult(ReadOnlySpan<byte> wkb, RasterHeader header, BandDecodeState state)
    {
        var little = header.ByteOrder == RasterByteOrder.LittleEndian;
        var scaleX = ReadDouble(wkb[5..], little);
        var scaleY = ReadDouble(wkb[13..], little);
        var x = ReadDouble(wkb[21..], little);
        var y = ReadDouble(wkb[29..], little);
        var skewX = ReadDouble(wkb[37..], little);
        var skewY = ReadDouble(wkb[45..], little);
        var srid = little ? BinaryPrimitives.ReadInt32LittleEndian(wkb[53..]) : BinaryPrimitives.ReadInt32BigEndian(wkb[53..]);
        var xs = new[] { x, x + header.Width * scaleX, x + header.Height * skewX, x + header.Width * scaleX + header.Height * skewX };
        var ys = new[] { y, y + header.Width * skewY, y + header.Height * scaleY, y + header.Width * skewY + header.Height * scaleY };
        return new RasterResult
        {
            Data = state.Data!,
            ContentType = "application/octet-stream",
            Width = header.Width,
            Height = header.Height,
            BandCount = header.BandCount,
            PixelType = state.PixelType,
            BandValidityMasks = state.Masks,
            Srid = srid,
            GeoTransform = [x, scaleX, skewX, y, skewY, scaleY],
            Extent = new RasterExtent { XMin = xs.Min(), XMax = xs.Max(), YMin = ys.Min(), YMax = ys.Max(), Srid = srid }
        };
    }

    private static void CopyBigEndianToLittleEndian(
        ReadOnlySpan<byte> source,
        Span<byte> target,
        int sampleBytes)
    {
        for (var offset = 0; offset < source.Length; offset += sampleBytes)
        {
            var destination = target.Slice(offset, sampleBytes);
            source.Slice(offset, sampleBytes).CopyTo(destination);
            destination.Reverse();
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
