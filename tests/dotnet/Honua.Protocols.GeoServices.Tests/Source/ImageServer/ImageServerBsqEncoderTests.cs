// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

public sealed class ImageServerBsqEncoderTests
{
    [UnitTheory]
    [InlineData(false, 0xf0)]
    [InlineData(true, 0x50)]
    public void Encode_StoredInterpretation_CombinesValidityWithoutChangingSamples(bool matchAny, byte expectedMask)
    {
        var raster = new RasterResult
        {
            Data = new byte[] { 0, 0, 0, 4, 0, 7, 8, 0 },
            ContentType = "application/octet-stream",
            Width = 4,
            Height = 1,
            BandCount = 2,
            PixelType = "8BUI",
            BandValidityMasks = [new byte[] { 0xd0 }, new byte[] { 0x70 }]
        };
        // First valid source sample is zero: validity comes from masks, never values.
        ImageServerBsqEncoder.Encode(raster, matchAny).Data.Should().Equal(raster.Data.Concat(new[] { expectedMask }));
        raster.BandValidityMasks![0].Should().Equal(0xd0);
        raster.BandValidityMasks[1].Should().Equal(0x70);
    }

    [UnitTheory]
    [InlineData(0x00, 0x00, 0x00)]
    [InlineData(0xff, 0xff, 0x80)]
    public void Encode_MatchAny_TrailingMaskBitsAndAllInvalidAreExact(byte first, byte second, byte last)
    {
        var raster = new RasterResult
        {
            Data = new byte[18],
            ContentType = "application/octet-stream",
            Width = 9,
            Height = 1,
            BandCount = 2,
            PixelType = "8BUI",
            BandValidityMasks = [new byte[] { first, second }, new byte[] { first, second }]
        };
        ImageServerBsqEncoder.Encode(raster, true).Data.Should().Equal(raster.Data.Concat(new byte[] { first, last }));
    }

    [UnitTest]
    public void Encode_AsymmetricBandMasks_PreservesValidOtherBandAndStoredNoDataSamples()
    {
        var samples = new float[] { -17.25f, 0, -9999, 8, -9999, 210, 220, 230 };
        var raster = new RasterResult
        {
            Data = samples.SelectMany(BitConverter.GetBytes).ToArray(),
            ContentType = "application/octet-stream",
            Width = 4,
            Height = 1,
            BandCount = 2,
            PixelType = "32BF",
            BandValidityMasks = [new byte[] { 0xd0 }, new byte[] { 0x70 }]
        };
        var encoded = ImageServerBsqEncoder.Encode(raster);
        encoded.Data.Should().Equal(raster.Data.Concat(new byte[] { 0xf0 }));
        raster.Data.Length.Should().Be(32);
        raster.BandValidityMasks![0].Should().Equal(0xd0);
    }

    [UnitTest]
    public void Encode_AllBandsNoData_ClearsMaskAndKeepsSampleBytes()
    {
        var raster = new RasterResult
        {
            Data = new byte[] { 9, 9, 9, 9, 8, 8, 8, 8 },
            ContentType = "application/octet-stream",
            Width = 4,
            Height = 1,
            BandCount = 2,
            PixelType = "8BUI",
            BandValidityMasks = [new byte[] { 0 }, new byte[] { 0 }]
        };
        ImageServerBsqEncoder.Encode(raster).Data.Should().Equal(raster.Data.Concat(new byte[] { 0 }));
    }

    [UnitTheory]
    [InlineData(1, "8BUI")]
    [InlineData(1, "8BSI")]
    [InlineData(2, "16BUI")]
    [InlineData(2, "16BSI")]
    [InlineData(4, "32BUI")]
    [InlineData(4, "32BSI")]
    [InlineData(4, "32BF")]
    [InlineData(8, "64BF")]
    public void Encode_AllValidTypedSamples_AppendsMsbFirstMaskWithUnusedBitsClear(int size, string type)
    {
        var raster = new RasterResult
        {
            Data = Enumerable.Range(0, 9 * size).Select(value => (byte)value).ToArray(),
            ContentType = "application/octet-stream",
            Width = 3,
            Height = 3,
            BandCount = 1,
            PixelType = type
        };
        ImageServerBsqEncoder.Encode(raster).Data.Should().Equal(raster.Data.Concat(new byte[] { 0xff, 0x80 }));
    }

    [UnitTheory]
    [InlineData("image/png", 2)]
    [InlineData("application/octet-stream", 1)]
    public void Encode_InconsistentProviderData_RejectsInsteadOfLabellingItBsq(string contentType, int length)
    {
        var raster = new RasterResult
        {
            Data = new byte[length],
            ContentType = contentType,
            Width = 2,
            Height = 1,
            BandCount = 1,
            PixelType = "8BUI"
        };
        var act = () => ImageServerBsqEncoder.Encode(raster);
        act.Should().Throw<InvalidDataException>();
    }
}
