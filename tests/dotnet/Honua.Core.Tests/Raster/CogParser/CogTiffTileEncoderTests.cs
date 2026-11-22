// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using static Honua.Core.Tests.Raster.CogParser.TiffTagReader;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogTiffTileEncoderTests
{
    [Trait("Tier", "Fast")]
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
                var value = row == 1 && col == 1 ? -9999f : row * 10f + col / 2f;
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
        tiff![..8].Should().Equal(73, 73, 42, 0, 192, 0, 0, 0);
        Encoding.ASCII.GetString(tiff, 8, 67).Should().Be(
            "GDAL_STRUCTURAL_METADATA_SIZE=000140 bytes\nLAYOUT=IFDS_BEFORE_DATA\n");
        var fields = TiffTagReader.ReadFields(tiff);
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
        // GDAL's COG block leader (tile size) and trailer (last four tile bytes repeated).
        U32(tiff.AsSpan(tileOffset - 4)).Should().Be(1024);
        (tileOffset + 1024 + 4).Should().Be(tiff.Length);
        tiff[^4..].Should().Equal(tiff[(tileOffset + 1020)..(tileOffset + 1024)]);
        for (var row = 0; row < 16; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                var expected = row == 1 && col == 1 ? -9999f : row * 10f + col / 2f;
                BinaryPrimitives.ReadSingleLittleEndian(tiff.AsSpan(tileOffset + (row * 16 + col) * 4))
                    .Should().Be(expected);
            }
        }
    }
}
