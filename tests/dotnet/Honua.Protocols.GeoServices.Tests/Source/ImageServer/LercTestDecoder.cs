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
            var blob = data.AsSpan(offset);
            Encoding.ASCII.GetString(blob[..6]).Should().Be("Lerc2 ");
            BinaryPrimitives.ReadInt32LittleEndian(blob[6..]).Should().Be(3);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(blob[10..]);
            BinaryPrimitives.ReadInt32LittleEndian(blob[14..]).Should().Be(height);
            BinaryPrimitives.ReadInt32LittleEndian(blob[18..]).Should().Be(width);
            var validCount = BinaryPrimitives.ReadInt32LittleEndian(blob[22..]);
            var blobSize = BinaryPrimitives.ReadInt32LittleEndian(blob[30..]);
            var dataType = BinaryPrimitives.ReadInt32LittleEndian(blob[34..]);
            var zMin = BinaryPrimitives.ReadDoubleLittleEndian(blob[46..]);
            var zMax = BinaryPrimitives.ReadDoubleLittleEndian(blob[54..]);
            blob = blob[..blobSize];
            Fletcher32(blob[14..]).Should().Be(checksum, "the LERC2 checksum covers every byte after it");

            var pixels = width * height;
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

            valid.Count(static bit => bit).Should().Be(validCount);
            var values = new double[pixels];
            var raw = new byte[pixels * bytesPerSample];
            // codeql[cs/equality-on-floats]: mirrors the encoder's exact constant-band check; a tolerance would expect samples the blob does not store
            if (validCount > 0 && zMin == zMax)
            {
                position.Should().Be(blobSize, "a constant band carries no samples");
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    values[pixel] = valid[pixel] ? zMin : 0;
                }
            }
            else if (validCount > 0)
            {
                blob[position++].Should().Be(1, "samples are stored in one sweep");
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    if (!valid[pixel])
                    {
                        continue;
                    }

                    var sample = blob.Slice(position, bytesPerSample);
                    sample.CopyTo(raw.AsSpan(pixel * bytesPerSample));
                    values[pixel] = dataType switch
                    {
                        1 => sample[0],
                        6 => BinaryPrimitives.ReadSingleLittleEndian(sample),
                        _ => throw new NotSupportedException($"Test reader does not decode dataType {dataType}."),
                    };
                    position += bytesPerSample;
                }

                position.Should().Be(blobSize);
            }

            bands.Add(new LercBand(values, valid, raw));
            offset += blobSize;
        }

        return bands;
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
