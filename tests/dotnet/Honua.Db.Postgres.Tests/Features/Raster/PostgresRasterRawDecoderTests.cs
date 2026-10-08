// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresRasterRawDecoderTests
{
    // Real PostGIS WKB from the independent protected 4x3, two-band SQL fixture.
    private const string FloatWkb = "01000002007B14AE47E17A843F7B14AE47E17A84BF5C8FC2F5289C5EC085EB51B81EE5424000000000000000000000000000000000E6100000040003004A003C1CC600008AC10000000000000642000000410000CA4200000040003C1CC6000010C1000020410000304100004041000050414A003C1CC6000048430000524300005C43003C1CC6000090C00000FA430000164400002F44000048440000614400007A4400808944";

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decode_FloatFixture_PreservesAllSamplesAndAsymmetricMasks(bool bigEndian)
    {
        var wkb = Convert.FromHexString(FloatWkb);
        if (bigEndian)
        {
            wkb[0] = 0;
            foreach (var offset in new[] { 1, 3, 57, 59 }) Array.Reverse(wkb, offset, 2);
            foreach (var offset in new[] { 5, 13, 21, 29, 37, 45 }) Array.Reverse(wkb, offset, 8);
            Array.Reverse(wkb, 53, 4);
            foreach (var band in new[] { 62, 115 })
                for (var sample = 0; sample < 13; sample++) Array.Reverse(wkb, band + sample * 4, 4);
        }
        var result = PostgresRasterRawDecoder.Decode(wkb);
        var expected = new float[] { -17.25f,0,33.5f,8,101,2,-9999,-9,10,11,12,13,
            200,210,220,-9999,-4.5f,500,600,700,800,900,1000,1100 };
        result.Data.Should().Equal(expected.SelectMany(BitConverter.GetBytes));
        result.PixelType.Should().Be("32BF");
        result.BandValidityMasks![0].Should().Equal(0xfd, 0xf0);
        result.BandValidityMasks[1].Should().Equal(0xef, 0xf0);
        result.GeoTransform.Should().Equal(-122.44, 0.01, 0, 37.79, 0, -0.01);
        result.Srid.Should().Be(4326);
        result.Width.Should().Be(4);
        result.Height.Should().Be(3);
        result.BandCount.Should().Be(2);
    }

    [UnitTheory]
    [InlineData(3, "8BSI", "80", "7f")]
    [InlineData(4, "8BUI", "ff", "00")]
    [InlineData(5, "16BSI", "0080", "ff7f")]
    [InlineData(6, "16BUI", "ffff", "0000")]
    [InlineData(7, "32BSI", "00000080", "ffffff7f")]
    [InlineData(8, "32BUI", "ffffffff", "00000000")]
    [InlineData(10, "32BF", "000080bf", "0000c07f")]
    [InlineData(11, "64BF", "000000000000f0bf", "000000000000f87f")]
    // The nearest representable values on either side of -1 remain valid, even next to NoData.
    [InlineData(10, "32BF", "ffff7fbf", "000080bf")]
    [InlineData(10, "32BF", "010080bf", "000080bf")]
    [InlineData(11, "64BF", "ffffffffffffefbf", "000000000000f0bf")]
    [InlineData(11, "64BF", "010000000000f0bf", "000000000000f0bf")]
    public void Decode_TypedBoundarySamples_PreservesBitsAndNoData(int type, string name, string validHex, string noDataHex)
    {
        var valid = Convert.FromHexString(validHex);
        var noData = Convert.FromHexString(noDataHex);
        var header = Convert.FromHexString(FloatWkb)[..61];
        header[3] = 1;
        header[57] = 2;
        header[59] = 1;
        var bytes = header.Concat(new[] { (byte)(0x40 | type) }).Concat(noData).Concat(valid).Concat(noData).ToArray();
        var result = PostgresRasterRawDecoder.Decode(bytes);
        result.Data.Should().Equal(valid.Concat(noData));
        result.PixelType.Should().Be(name);
        result.BandValidityMasks![0].Should().Equal(0x80);
    }

    [UnitTest]
    public void Decode_AbsentNoDataFlag_DoesNotMaskStoredSentinel()
    {
        var bytes = Convert.FromHexString(FloatWkb);
        bytes[61] &= 0xbf;
        var result = PostgresRasterRawDecoder.Decode(bytes);
        result.BandValidityMasks![0].Should().Equal(0xff, 0xf0);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(66)]
    [InlineData(166)]
    public void Decode_TruncatedWkb_RejectsBeforeReturningPixels(int length)
    {
        var bytes = Convert.FromHexString(FloatWkb)[..length];
        var act = () => PostgresRasterRawDecoder.Decode(bytes);
        act.Should().Throw<InvalidDataException>();
    }

    [UnitTheory]
    [InlineData(1, 1)]
    [InlineData(61, 0xca)]
    [InlineData(114, 0x47)]
    public void Decode_UnsupportedVersionOfflineOrMixedType_Rejects(int offset, byte value)
    {
        var bytes = Convert.FromHexString(FloatWkb);
        bytes[offset] = value;
        var act = () => PostgresRasterRawDecoder.Decode(bytes);
        act.Should().Throw<NotSupportedException>();
    }
}
