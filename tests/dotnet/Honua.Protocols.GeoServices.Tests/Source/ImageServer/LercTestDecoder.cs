// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using System.Buffers.Binary;
using System.Text;
using FluentAssertions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

internal static class LercTestDecoder
{
    internal sealed record LercBand(double[] Values, bool[] Valid, byte[] RawSamples);

    // An independent reader of the LERC2 blob layout (header, Fletcher-32 checksum, run-length
    // mask, one-sweep samples), so the test fails if the wire framing drifts from the format.
    internal static List<LercBand> Decode(byte[] data, int width, int height, int bytesPerSample = 1)
    {
        var bands = new List<LercBand>();
        var offset = 0;
        while (offset < data.Length)
        {
            var header = ReadHeader(data.AsSpan(offset), width, height) with { BytesPerSample = bytesPerSample };
            ReadOnlySpan<byte> blob = data.AsSpan(offset, header.BlobSize);
            var pixels = width * height;
            var (valid, position) = ReadValidityMask(blob, pixels, header.ValidCount);
            valid.Count(static bit => bit).Should().Be(header.ValidCount);
            bands.Add(ReadSamples(blob[position..], header, valid));
            offset += header.BlobSize;
        }

        return bands;
    }

    private readonly record struct LercHeader(
        int ValidCount, int BlobSize, int DataType, double ZMin, double ZMax, int BytesPerSample = 1);

    private static LercHeader ReadHeader(ReadOnlySpan<byte> blob, int width, int height)
    {
        Encoding.ASCII.GetString(blob[..6]).Should().Be("Lerc2 ");
        BinaryPrimitives.ReadInt32LittleEndian(blob[6..]).Should().Be(3);
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(blob[10..]);
        BinaryPrimitives.ReadInt32LittleEndian(blob[14..]).Should().Be(height);
        BinaryPrimitives.ReadInt32LittleEndian(blob[18..]).Should().Be(width);
        var header = new LercHeader(
            ValidCount: BinaryPrimitives.ReadInt32LittleEndian(blob[22..]),
            BlobSize: BinaryPrimitives.ReadInt32LittleEndian(blob[30..]),
            DataType: BinaryPrimitives.ReadInt32LittleEndian(blob[34..]),
            ZMin: BinaryPrimitives.ReadDoubleLittleEndian(blob[46..]),
            ZMax: BinaryPrimitives.ReadDoubleLittleEndian(blob[54..]));
        Fletcher32(blob[14..header.BlobSize]).Should().Be(checksum, "the LERC2 checksum covers every byte after it");
        return header;
    }

    private static (bool[] Valid, int Position) ReadValidityMask(ReadOnlySpan<byte> blob, int pixels, int validCount)
    {
        var valid = new bool[pixels];
        var maskBytes = BinaryPrimitives.ReadInt32LittleEndian(blob[62..]);
        var position = 66;
        if (validCount == pixels)
        {
            Array.Fill(valid, true);
        }
        else if (validCount > 0)
        {
            var mask = new List<byte>();
            var end = position + maskBytes;
            while (true)
            {
                var count = BinaryPrimitives.ReadInt16LittleEndian(blob[position..]);
                position += 2;
                if (count == short.MinValue)
                {
                    break;
                }

                if (count > 0)
                {
                    mask.AddRange(blob.Slice(position, count).ToArray());
                    position += count;
                }
                else
                {
                    mask.AddRange(Enumerable.Repeat(blob[position], -count));
                    position++;
                }
            }

            position.Should().Be(end);
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                valid[pixel] = (mask[pixel / 8] & (0x80 >> (pixel % 8))) != 0;
            }
        }

        return (valid, position);
    }

    // samples starts after the mask; a fully consumed blob ends at samples.Length.
    private static LercBand ReadSamples(ReadOnlySpan<byte> samples, LercHeader header, bool[] valid)
    {
        var bytesPerSample = header.BytesPerSample;
        var pixels = valid.Length;
        var values = new double[pixels];
        var raw = new byte[pixels * bytesPerSample];
        // codeql[cs/equality-on-floats]: mirrors the encoder's exact constant-band check; a tolerance would expect samples the blob does not store
        if (header.ValidCount > 0 && header.ZMin == header.ZMax)
        {
            samples.Length.Should().Be(0, "a constant band carries no samples");
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                values[pixel] = valid[pixel] ? header.ZMin : 0;
            }
        }
        else if (header.ValidCount > 0)
        {
            var position = 0;
            samples[position++].Should().Be(1, "samples are stored in one sweep");
            for (var pixel = 0; pixel < pixels; pixel++)
            {
                if (!valid[pixel])
                {
                    continue;
                }

                var sample = samples.Slice(position, bytesPerSample);
                sample.CopyTo(raw.AsSpan(pixel * bytesPerSample));
                values[pixel] = header.DataType switch
                {
                    1 => sample[0],
                    6 => BinaryPrimitives.ReadSingleLittleEndian(sample),
                    _ => throw new NotSupportedException($"Test reader does not decode dataType {header.DataType}."),
                };
                position += bytesPerSample;
            }

            position.Should().Be(samples.Length);
        }

        return new LercBand(values, valid, raw);
    }

    private static uint Fletcher32(ReadOnlySpan<byte> bytes)
    {
        uint sum1 = 0xffff, sum2 = 0xffff;
        var index = 0;
        for (var words = bytes.Length / 2; words > 0;)
        {
            var block = Math.Min(words, 359);
            words -= block;
            while (block-- > 0)
            {
                sum1 += (uint)((bytes[index] << 8) | bytes[index + 1]);
                index += 2;
                sum2 += sum1;
            }

            sum1 = (sum1 & 0xffff) + (sum1 >> 16);
            sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        }

        if ((bytes.Length & 1) != 0)
        {
            sum1 += (uint)bytes[index] << 8;
            sum2 += sum1;
        }

        sum1 = (sum1 & 0xffff) + (sum1 >> 16);
        sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        return (sum2 << 16) | sum1;
    }
}
