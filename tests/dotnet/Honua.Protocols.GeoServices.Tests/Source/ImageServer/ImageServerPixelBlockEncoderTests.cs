// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Raster;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerPixelBlockEncoderTests
{
    [UnitTest]
    public void LercEncoder_MixedMaskAndConstantBands_DecodesToStoredSamples()
    {
        // Band 1 varies with a NoData pixel; band 2 is constant (LERC2 stores it as zMin only);
        // band 3 is entirely NoData.
        var raster = new RasterResult
        {
            Data = [10, 20, 30, 40, 50, 7, 7, 7, 7, 7, 1, 2, 3, 4, 5],
            ContentType = "application/octet-stream",
            Width = 5,
            Height = 1,
            BandCount = 3,
            PixelType = "8BUI",
            BandValidityMasks = [[0xb8], [0xf8], [0x00]],
        };

        var bands = LercTestDecoder.Decode(ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Lerc).Data, 5, 1);

        bands.Should().HaveCount(3);
        bands[0].Values.Should().Equal(10, 0, 30, 40, 50);
        bands[0].Valid.Should().Equal(true, false, true, true, true);
        bands[1].Values.Should().Equal(7, 7, 7, 7, 7);
        bands[1].Valid.Should().OnlyContain(valid => valid);
        bands[2].Valid.Should().OnlyContain(valid => !valid);
    }

    [UnitTest]
    public void LercEncoder_FloatSamples_RoundTripBitExact()
    {
        var samples = new[] { -17.25f, 0f, 33.5f, 1e-7f, float.MaxValue, -3.75f };
        var raster = new RasterResult
        {
            Data = samples.SelectMany(BitConverter.GetBytes).ToArray(),
            ContentType = "application/octet-stream",
            Width = 3,
            Height = 2,
            BandCount = 1,
            PixelType = "32BF",
        };

        var blob = ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Lerc).Data;

        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(34)).Should().Be(6, "dataType 6 is LERC2 float");
        var decoded = LercTestDecoder.Decode(blob, 3, 2, bytesPerSample: 4).Single();
        decoded.RawSamples.Should().Equal(raster.Data);
    }

    [UnitTest]
    public void BipEncoder_InterleavesBandsPerPixelAndKeepsAnyBandMask()
    {
        var raster = new RasterResult
        {
            Data = [1, 2, 3, 11, 12, 13],
            ContentType = "application/octet-stream",
            Width = 3,
            Height = 1,
            BandCount = 2,
            PixelType = "8BUI",
            BandValidityMasks = [[0x80], [0x20]],
        };

        ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Bip).Data
            .Should().Equal(1, 11, 2, 12, 3, 13, 0xa0);
    }
}
