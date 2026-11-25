// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Honua.Db.Postgres.Features.Raster;

internal static partial class PostgresRasterRawDecoder
{
    private readonly ref struct ValidityMaskRequest
    {
        public ReadOnlySpan<byte> LittleEndianTarget { get; init; }
        public ReadOnlySpan<byte> NoDataSpan { get; init; }
        public int SampleType { get; init; }
        public RasterByteOrder SourceByteOrder { get; init; }
        public int Pixels { get; init; }
        public byte[] Mask { get; init; }
    }

    private static void PopulateBandValidityMask(byte flags, ValidityMaskRequest request)
    {
        var isNoDataBand = (flags & 0x20) != 0;
        if (isNoDataBand)
        {
            return;
        }

        var hasNoData = (flags & 0x40) != 0;
        if (!hasNoData)
        {
            FillAllValidMask(request.Mask, request.Pixels);
        }
        else
        {
            PopulateValidityMask(request);
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
    private static void PopulateValidityMask(ValidityMaskRequest request)
    {
        if (!BitConverter.IsLittleEndian)
        {
            PopulateValidityMaskFallback(request);
            return;
        }

        if (request.SampleType is 10 or 11)
        {
            PopulateFloatingValidityMask(request);
            return;
        }

        PopulateIntegerValidityMask(request);
    }

    private static void PopulateValidityMaskFallback(ValidityMaskRequest request)
    {
        var sourceLittle = request.SourceByteOrder == RasterByteOrder.LittleEndian;
        var noData = ReadSample(request.NoDataSpan, request.SampleType, sourceLittle);
        var sampleBytes = request.LittleEndianTarget.Length / request.Pixels;
        for (var pixel = 0; pixel < request.Pixels; pixel++)
        {
            var value = ReadSample(request.LittleEndianTarget.Slice(pixel * sampleBytes, sampleBytes), request.SampleType, little: true);
            if (value.CompareTo(noData) != 0)
            {
                request.Mask[pixel >> 3] |= (byte)(0x80 >> (pixel & 7));
            }
        }
    }

    private static void PopulateIntegerValidityMask(ValidityMaskRequest request)
    {
        var sourceLittle = request.SourceByteOrder == RasterByteOrder.LittleEndian;
        switch (request.SampleType)
        {
            case 0 or 1 or 2 or 3 or 4:
                PopulateEqualityMask(request.LittleEndianTarget, request.NoDataSpan[0], request.Mask);
                break;
            case 5:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, short>(request.LittleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadInt16LittleEndian(request.NoDataSpan) : BinaryPrimitives.ReadInt16BigEndian(request.NoDataSpan),
                    request.Mask);
                break;
            case 6:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, ushort>(request.LittleEndianTarget),
                    ReadUInt16(request.NoDataSpan, sourceLittle),
                    request.Mask);
                break;
            case 7:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, int>(request.LittleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadInt32LittleEndian(request.NoDataSpan) : BinaryPrimitives.ReadInt32BigEndian(request.NoDataSpan),
                    request.Mask);
                break;
            case 8:
                PopulateEqualityMask(
                    MemoryMarshal.Cast<byte, uint>(request.LittleEndianTarget),
                    sourceLittle ? BinaryPrimitives.ReadUInt32LittleEndian(request.NoDataSpan) : BinaryPrimitives.ReadUInt32BigEndian(request.NoDataSpan),
                    request.Mask);
                break;
            default:
                throw new NotSupportedException("Unsupported raster sample type.");
        }
    }

    private static void PopulateFloatingValidityMask(ValidityMaskRequest request)
    {
        var sourceLittle = request.SourceByteOrder == RasterByteOrder.LittleEndian;
        if (request.SampleType == 10)
        {
            PopulateFloatMask(
                MemoryMarshal.Cast<byte, float>(request.LittleEndianTarget),
                sourceLittle ? BinaryPrimitives.ReadSingleLittleEndian(request.NoDataSpan) : BinaryPrimitives.ReadSingleBigEndian(request.NoDataSpan),
                request.Mask);
        }
        else
        {
            PopulateDoubleMask(
                MemoryMarshal.Cast<byte, double>(request.LittleEndianTarget),
                ReadDouble(request.NoDataSpan, sourceLittle),
                request.Mask);
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
}
