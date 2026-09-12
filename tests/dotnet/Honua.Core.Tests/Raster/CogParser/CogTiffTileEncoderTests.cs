// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogTiffTileEncoderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Encode_FloatDem_PreservesIndependentValuesNoDataAndGeoreferencing(bool littleEndian)
    {
        var samples = new byte[16 * 16 * sizeof(float)];
        for (var row = 0; row < 16; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                var value = row == 1 && col == 1 ? -9999 : row * 10 + col / 2f;
                if (littleEndian)
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan((row * 16 + col) * 4), value);
                else
                    BinaryPrimitives.WriteSingleBigEndian(samples.AsSpan((row * 16 + col) * 4), value);
            }
        }
        var extent = new RasterExtent { XMin = 100, YMin = 136, XMax = 132, YMax = 200, Srid = 3857 };
        var metadata = new CogMetadata(16, 16, 1, "float32", 3857, "DEFLATE", 16, 16, [], extent,
            BitsPerSample: 32, IsLittleEndian: littleEndian, NoData: "-9999");
        var tiff = CogTiffTileEncoder.Encode(samples, metadata, extent);
        tiff.Should().NotBeNull();

        // Parse the emitted TIFF tag bytes directly, independently of Honua's TIFF parser.
        tiff![..8].Should().Equal(73, 73, 42, 0, 8, 0, 0, 0);
        var fields = ReadFields(tiff);
        U32(fields[256]).Should().Be(16);
        U32(fields[257]).Should().Be(16);
        U16(fields[258]).Should().Be(32);
        U16(fields[259]).Should().Be(1); // Decoded samples are stored uncompressed.
        U16(fields[339]).Should().Be(3); // IEEE floating point, never an integer reinterpretation.
        Encoding.ASCII.GetString(fields[42113]).Should().Be("-9999\0");
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33550]).Should().Be(2);
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33550].AsSpan(8)).Should().Be(4);
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33922].AsSpan(24)).Should().Be(100);
        BinaryPrimitives.ReadDoubleLittleEndian(fields[33922].AsSpan(32)).Should().Be(200);
        U16(fields[34735].AsSpan(22)).Should().Be(1); // PixelIsArea, after normalization.
        U16(fields[34735].AsSpan(30)).Should().Be(3857);
        U32(fields[325]).Should().Be(1024);
        var tileOffset = (int)U32(fields[324]);
        (tileOffset + 1024).Should().Be(tiff.Length);
        for (var row = 0; row < 16; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                var expected = row == 1 && col == 1 ? -9999 : row * 10 + col / 2f;
                BinaryPrimitives.ReadSingleLittleEndian(tiff.AsSpan(tileOffset + (row * 16 + col) * 4))
                    .Should().Be(expected);
            }
        }
    }

    private static Dictionary<ushort, byte[]> ReadFields(byte[] tiff)
    {
        var fields = new Dictionary<ushort, byte[]>();
        var count = U16(tiff.AsSpan(8));
        for (var i = 0; i < count; i++)
        {
            var entry = tiff.AsSpan(10 + i * 12, 12);
            var type = U16(entry[2..]);
            var size = type switch { 2 => 1, 3 => 2, 4 => 4, 12 => 8, _ => throw new InvalidDataException() };
            var length = checked((int)U32(entry[4..]) * size);
            var value = length <= 4 ? entry.Slice(8, length).ToArray()
                : tiff.AsSpan((int)U32(entry[8..]), length).ToArray();
            fields.Add(U16(entry), value);
        }
        U32(tiff.AsSpan(10 + count * 12)).Should().Be(0);
        return fields;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
}
