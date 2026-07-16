// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.CogParser;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Unit coverage for the horizontal differencing predictor, including the byte orders
/// and sample strides the GDAL fixtures cannot reach (GDAL writes little-endian only).
/// </summary>
public class TilePredictorTests
{
    [Fact]
    public void Undo_EightBitSingleBand_RunsPrefixSumPerRow()
    {
        // Two rows; the predictor must restart at each row boundary rather than
        // carrying the running sum across rows.
        var pixels = new byte[] { 1, 1, 1, 1, 100, 2, 2, 2 };
        var layout = new TilePixelLayout(4, 1, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);

        TilePredictor.Undo(pixels, layout);

        pixels.Should().Equal(1, 2, 3, 4, 100, 102, 104, 106);
    }

    [Fact]
    public void Undo_EightBitThreeBands_StridesBySamplesPerPixel()
    {
        // Each band accumulates independently: stride is samplesPerPixel, not 1.
        var pixels = new byte[] { 10, 20, 30, 1, 2, 3, 1, 2, 3 };
        var layout = new TilePixelLayout(3, 3, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);

        TilePredictor.Undo(pixels, layout);

        pixels.Should().Equal(10, 20, 30, 11, 22, 33, 12, 24, 36);
    }

    [Fact]
    public void Undo_EightBit_WrapsAtSampleWidth()
    {
        var pixels = new byte[] { 250, 10 };
        var layout = new TilePixelLayout(2, 1, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);

        TilePredictor.Undo(pixels, layout);

        pixels.Should().Equal(250, 4);
    }

    [Fact]
    public void Undo_SixteenBitLittleEndian_AccumulatesInFileByteOrder()
    {
        // 0x0100 = 256, delta 0x0002 = 2 -> 258 (0x0102)
        var pixels = new byte[] { 0x00, 0x01, 0x02, 0x00 };
        var layout = new TilePixelLayout(2, 1, 16, TilePixelLayout.PredictorHorizontalDifferencing, true);

        TilePredictor.Undo(pixels, layout);

        pixels.Should().Equal(0x00, 0x01, 0x02, 0x01);
    }

    [Fact]
    public void Undo_SixteenBitBigEndian_AccumulatesInFileByteOrder()
    {
        // Same values as the little-endian case, stored big-endian: 256 then delta 2 -> 258.
        var pixels = new byte[] { 0x01, 0x00, 0x00, 0x02 };
        var layout = new TilePixelLayout(2, 1, 16, TilePixelLayout.PredictorHorizontalDifferencing, false);

        TilePredictor.Undo(pixels, layout);

        pixels.Should().Equal(0x01, 0x00, 0x01, 0x02);
    }

    [Fact]
    public void Undo_ThirtyTwoBitLittleEndian_AccumulatesPerSample()
    {
        var pixels = new byte[8];
        BitConverter.TryWriteBytes(pixels.AsSpan(0), 70000u);
        BitConverter.TryWriteBytes(pixels.AsSpan(4), 5u);
        var layout = new TilePixelLayout(2, 1, 32, TilePixelLayout.PredictorHorizontalDifferencing, true);

        TilePredictor.Undo(pixels, layout);

        BitConverter.ToUInt32(pixels, 0).Should().Be(70000u);
        BitConverter.ToUInt32(pixels, 4).Should().Be(70005u);
    }

    [Fact]
    public void Undo_PredictorNone_LeavesPixelsUntouched()
    {
        var pixels = new byte[] { 1, 1, 1, 1 };

        TilePredictor.Undo(pixels, TilePixelLayout.None);

        pixels.Should().Equal(1, 1, 1, 1);
    }

    [Fact]
    public void Undo_RowStrideMismatch_ThrowsInvalidData()
    {
        // 5 bytes cannot be a whole number of 4-byte rows.
        var pixels = new byte[5];
        var layout = new TilePixelLayout(4, 1, 8, TilePixelLayout.PredictorHorizontalDifferencing, true);

        var act = () => TilePredictor.Undo(pixels, layout);

        act.Should().Throw<InvalidDataException>().WithMessage("*whole number*");
    }

    [Fact]
    public void Undo_UnsupportedBitDepth_ThrowsNamingPredictor()
    {
        var layout = new TilePixelLayout(4, 1, 4, TilePixelLayout.PredictorHorizontalDifferencing, true);

        var act = () => TilePredictor.Undo(new byte[8], layout);

        act.Should().Throw<UnsupportedTilePredictorException>().WithMessage("*4*");
    }
}
