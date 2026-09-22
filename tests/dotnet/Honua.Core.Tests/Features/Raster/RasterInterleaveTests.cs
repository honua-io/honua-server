// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Pins the raw-sample layout contract behind <see cref="RasterFormat.Raw"/>: the EHdr
/// driver hands back band-interleaved-by-line bytes, an Esri client expects band
/// sequential, and a single band is the same bytes either way.
/// </summary>
public sealed class RasterInterleaveTests
{
    [Theory]
    [InlineData("8BUI", 1)]
    [InlineData("8bui", 1)]
    [InlineData("1BB", 1)]
    [InlineData("16BSI", 2)]
    [InlineData("16BUI", 2)]
    [InlineData("32BF", 4)]
    [InlineData("32BUI", 4)]
    [InlineData("64BF", 8)]
    public void BytesPerSample_FollowsThePostgisPixelType(string pixelType, int expected)
    {
        RasterInterleave.BytesPerSample(pixelType).Should().Be(expected);
    }

    [UnitTest]
    public void BytesPerSample_RejectsAnUnknownType()
    {
        var act = () => RasterInterleave.BytesPerSample("128BX");
        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void SingleBand_IsReturnedUnchanged()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };

        var result = RasterInterleave.BandInterleavedByLineToBandSequential(data, width: 3, height: 2, bands: 1, bytesPerSample: 1);

        result.Should().BeSameAs(data);
    }

    [UnitTest]
    public void ThreeBands_AreRegroupedBandByBand()
    {
        // 2 columns x 2 rows x 3 bands, one byte per sample, BIL: row 0 is
        // [b1: 10 11][b2: 20 21][b3: 30 31], row 1 is [b1: 12 13][b2: 22 23][b3: 32 33].
        var bil = new byte[] { 10, 11, 20, 21, 30, 31, 12, 13, 22, 23, 32, 33 };

        var bsq = RasterInterleave.BandInterleavedByLineToBandSequential(bil, width: 2, height: 2, bands: 3, bytesPerSample: 1);

        bsq.Should().Equal(10, 11, 12, 13, 20, 21, 22, 23, 30, 31, 32, 33);
    }

    [UnitTest]
    public void MultiByteSamples_MoveAsWholeSamples()
    {
        // 1 column x 2 rows x 2 bands, two bytes per sample.
        var bil = new byte[] { 0xA0, 0xA1, 0xB0, 0xB1, 0xA2, 0xA3, 0xB2, 0xB3 };

        var bsq = RasterInterleave.BandInterleavedByLineToBandSequential(bil, width: 1, height: 2, bands: 2, bytesPerSample: 2);

        bsq.Should().Equal(0xA0, 0xA1, 0xA2, 0xA3, 0xB0, 0xB1, 0xB2, 0xB3);
    }

    [UnitTest]
    public void ALengthMismatch_IsAnError_NotASilentlyTruncatedBuffer()
    {
        var act = () => RasterInterleave.BandInterleavedByLineToBandSequential(new byte[5], width: 2, height: 2, bands: 2, bytesPerSample: 1);

        act.Should().Throw<ArgumentException>().WithMessage("*holds 5 bytes*need 8*");
    }
}
