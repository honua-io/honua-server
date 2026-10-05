// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Raster;

/// <summary>
/// Encodes canonical planar samples as lossless LERC2 (version 3): one blob per band, concatenated
/// in band order, which is how exportImage <c>format=lerc</c> delivers a multi-band block.
/// </summary>
/// <remarks>
/// Each blob stores its band's validity mask (run-length coded) and every valid sample verbatim
/// ("one sweep"), so decoding reproduces the stored samples bit for bit; there is no quantization
/// and <c>maxZError</c> only records the lossless tolerance for the sample type. A band whose
/// valid samples are all equal is stored as the constant <c>zMin</c>, as LERC2 specifies.
/// </remarks>
internal static class ImageServerLercEncoder
{
    private const int Version = 3;
    private const int MicroBlockSize = 8;
    private const short EndOfRuns = short.MinValue;
    private const int MaxLiteralRun = short.MaxValue;
    private static readonly byte[] FileKey = Encoding.ASCII.GetBytes("Lerc2 ");

    // FileKey, version, checksum, six int fields, three double fields.
    private static readonly int HeaderLength = FileKey.Length + (8 * sizeof(int)) + (3 * sizeof(double));
    private static readonly int ChecksumStart = FileKey.Length + (2 * sizeof(int));

    internal static int BytesPerSample(string? pixelType) => pixelType?.ToUpperInvariant() switch
    {
        "8BUI" or "8BSI" => 1,
        "16BUI" or "16BSI" => 2,
        "32BUI" or "32BSI" or "32BF" => 4,
        "64BF" => 8,
        _ => throw new NotSupportedException("Unsupported raw sample type."),
    };

    internal static RasterResult Encode(RasterResult raster)
    {
        var (dataType, bytesPerSample, maxZError) = raster.PixelType?.ToUpperInvariant() switch
        {
            "8BSI" => (0, 1, 0.5),
            "8BUI" => (1, 1, 0.5),
            "16BSI" => (2, 2, 0.5),
            "16BUI" => (3, 2, 0.5),
            "32BSI" => (4, 4, 0.5),
            "32BUI" => (5, 4, 0.5),
            "32BF" => (6, 4, 0d),
            "64BF" => (7, 8, 0d),
            _ => throw new NotSupportedException("Unsupported LERC sample type."),
        };
        if (raster.Width <= 0 || raster.Height <= 0 || raster.BandCount <= 0 ||
            raster.ContentType != "application/octet-stream")
        {
            throw new InvalidDataException("Invalid canonical raw raster layout.");
        }

        var pixels = checked(raster.Width * raster.Height);
        var bandLength = checked(pixels * bytesPerSample);
        if (checked((long)bandLength * raster.BandCount) != raster.Data.Length)
        {
            throw new InvalidDataException("Raw sample length does not match its layout.");
        }

        var maskLength = (pixels + 7) / 8;
        if (raster.BandValidityMasks is { } masks &&
            (masks.Length != raster.BandCount || masks.Any(mask => mask is null || mask.Length != maskLength)))
        {
            throw new InvalidDataException("Band validity masks do not match the raster layout.");
        }

        using var output = new MemoryStream();
        for (var band = 0; band < raster.BandCount; band++)
        {
            var samples = raster.Data.AsSpan(band * bandLength, bandLength);
            var blob = EncodeBand(
                samples,
                raster.BandValidityMasks?[band],
                raster.Width,
                raster.Height,
                dataType,
                bytesPerSample,
                maxZError);
            output.Write(blob);
        }

        return raster with { Data = output.ToArray(), ContentType = "application/octet-stream" };
    }

    private static byte[] EncodeBand(
        ReadOnlySpan<byte> samples,
        byte[]? storedMask,
        int width,
        int height,
        int dataType,
        int bytesPerSample,
        double maxZError)
    {
        var pixels = width * height;
        var mask = new byte[(pixels + 7) / 8];
        var validCount = 0;
        var zMin = double.PositiveInfinity;
        var zMax = double.NegativeInfinity;
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            if (storedMask is not null && (storedMask[pixel / 8] & (0x80 >> (pixel % 8))) == 0)
            {
                continue;
            }

            var value = ReadSample(samples.Slice(pixel * bytesPerSample, bytesPerSample), dataType);
            // LERC2 orders valid values by zMin/zMax; a NaN sample has no place in that range, so
            // it travels as an invalid (NoData) pixel.
            if (double.IsNaN(value))
            {
                continue;
            }

            mask[pixel / 8] |= (byte)(0x80 >> (pixel % 8));
            validCount++;
            zMin = Math.Min(zMin, value);
            zMax = Math.Max(zMax, value);
        }

        if (validCount == 0)
        {
            zMin = 0;
            zMax = 0;
        }

        var maskRuns = validCount is 0 || validCount == pixels ? [] : EncodeRuns(mask);
        var writeSamples = validCount > 0 && zMin != zMax;
        var blobLength = HeaderLength + sizeof(int) + maskRuns.Length +
            (writeSamples ? 1 + (validCount * bytesPerSample) : 0);
        var blob = new byte[blobLength];
        var span = blob.AsSpan();
        FileKey.CopyTo(span);
        var offset = FileKey.Length;
        WriteInt32(span, ref offset, Version);
        offset += sizeof(uint); // checksum, filled in once the blob is complete
        WriteInt32(span, ref offset, height);
        WriteInt32(span, ref offset, width);
        WriteInt32(span, ref offset, validCount);
        WriteInt32(span, ref offset, MicroBlockSize);
        WriteInt32(span, ref offset, blobLength);
        WriteInt32(span, ref offset, dataType);
        WriteDouble(span, ref offset, maxZError);
        WriteDouble(span, ref offset, zMin);
        WriteDouble(span, ref offset, zMax);
        WriteInt32(span, ref offset, maskRuns.Length);
        maskRuns.CopyTo(span[offset..]);
        offset += maskRuns.Length;
        if (writeSamples)
        {
            span[offset++] = 1; // one sweep: every valid sample, raw, in pixel order
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                if ((mask[pixel / 8] & (0x80 >> (pixel % 8))) != 0)
                {
                    samples.Slice(pixel * bytesPerSample, bytesPerSample).CopyTo(span[offset..]);
                    offset += bytesPerSample;
                }
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            span[(FileKey.Length + sizeof(int))..],
            ComputeFletcher32(span[ChecksumStart..]));
        return blob;
    }

    // LERC run-length coding: a little-endian Int16 count, then that many literal bytes; a
    // negative count repeats the single following byte; Int16.MinValue ends the stream.
    private static byte[] EncodeRuns(ReadOnlySpan<byte> mask)
    {
        using var runs = new MemoryStream();
        Span<byte> count = stackalloc byte[sizeof(short)];
        var index = 0;
        while (index < mask.Length)
        {
            var repeat = 1;
            while (index + repeat < mask.Length && repeat < MaxLiteralRun && mask[index + repeat] == mask[index])
            {
                repeat++;
            }

            if (repeat >= 3)
            {
                BinaryPrimitives.WriteInt16LittleEndian(count, (short)-repeat);
                runs.Write(count);
                runs.WriteByte(mask[index]);
                index += repeat;
                continue;
            }

            var literal = 1;
            while (index + literal < mask.Length && literal < MaxLiteralRun &&
                   !StartsRepeat(mask[(index + literal)..]))
            {
                literal++;
            }

            BinaryPrimitives.WriteInt16LittleEndian(count, (short)literal);
            runs.Write(count);
            runs.Write(mask.Slice(index, literal));
            index += literal;
        }

        BinaryPrimitives.WriteInt16LittleEndian(count, EndOfRuns);
        runs.Write(count);
        return runs.ToArray();
    }

    private static bool StartsRepeat(ReadOnlySpan<byte> remaining)
        => remaining.Length >= 3 && remaining[0] == remaining[1] && remaining[1] == remaining[2];

    // Fletcher-32 over 16-bit big-endian words, as the LERC2 reference decoder verifies it.
    private static uint ComputeFletcher32(ReadOnlySpan<byte> data)
    {
        uint sum1 = 0xffff;
        uint sum2 = 0xffff;
        var index = 0;
        var words = data.Length / 2;
        while (words > 0)
        {
            var block = Math.Min(words, 359);
            words -= block;
            for (; block > 0; block--)
            {
                sum1 += (uint)data[index++] << 8;
                sum1 += data[index++];
                sum2 += sum1;
            }

            sum1 = (sum1 & 0xffff) + (sum1 >> 16);
            sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        }

        if ((data.Length & 1) != 0)
        {
            sum1 += (uint)data[index] << 8;
            sum2 += sum1;
        }

        sum1 = (sum1 & 0xffff) + (sum1 >> 16);
        sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        return (sum2 << 16) | sum1;
    }

    private static double ReadSample(ReadOnlySpan<byte> sample, int dataType) => dataType switch
    {
        0 => unchecked((sbyte)sample[0]),
        1 => sample[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(sample),
        3 => BinaryPrimitives.ReadUInt16LittleEndian(sample),
        4 => BinaryPrimitives.ReadInt32LittleEndian(sample),
        5 => BinaryPrimitives.ReadUInt32LittleEndian(sample),
        6 => BinaryPrimitives.ReadSingleLittleEndian(sample),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(sample),
    };

    private static void WriteInt32(Span<byte> target, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[offset..], value);
        offset += sizeof(int);
    }

    private static void WriteDouble(Span<byte> target, ref int offset, double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(target[offset..], value);
        offset += sizeof(double);
    }
}
