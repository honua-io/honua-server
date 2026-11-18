// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogTileOverzoomTests
{
    private static readonly RasterExtent Extent = new() { XMin = 0, YMin = 0, XMax = 16, YMax = 16, Srid = 3857 };

    [Trait("Tier", "Fast")]
    [Fact]
    public void Upsample_MultiBytePixels_ReplicatesEachSourcePixelOfTheQuadrant()
    {
        // 16x16 uint16 RGB tile; every pixel's six bytes encode (x, y) so a misplaced copy is visible.
        var metadata = Metadata(bands: 3, bits: 16);
        var samples = new byte[16 * 16 * 6];
        for (var i = 0; i < 16 * 16; i++)
        {
            samples[i * 6] = (byte)(i % 16);
            samples[(i * 6) + 2] = (byte)(i / 16);
            samples[(i * 6) + 5] = 0xAB;
        }

        // Shift 2: the quadrant at (3, 1) covers source pixels x 12..15, y 4..7, each repeated 4x4.
        var output = CogTileOverzoom.Upsample(samples, metadata, new CogOverzoomQuadrant(Shift: 2, Column: 3, Row: 1));

        for (var i = 0; i < 16 * 16; i++)
        {
            var (x, y) = (i % 16, i / 16);
            output.AsSpan(i * 6, 6).ToArray().Should().Equal(
                (byte)(12 + (x >> 2)), 0, (byte)(4 + (y >> 2)), 0, 0, 0xAB);
        }
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CanUpsample_ChunkyBytePixelsDividingTheTile_Accepts(int shift)
    {
        CogTileOverzoom.CanUpsample(Metadata(bands: 1, bits: 8), shift).Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(5, 8, 1)] // 16 >> 5 leaves no source pixel.
    [InlineData(1, 8, 2)] // Planar tiles hold one band per tile.
    [InlineData(1, 12, 1)] // Samples are not whole bytes.
    [InlineData(0, 8, 1)] // Not an overzoom.
    public void CanUpsample_UnsupportedLayoutOrShift_Refuses(int shift, int bits, int planar)
    {
        var metadata = Metadata(bands: 1, bits: bits) with { PlanarConfiguration = planar };

        CogTileOverzoom.CanUpsample(metadata, shift).Should().BeFalse();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Upsample_QuadrantOutsideAncestorOrShortTile_Throws()
    {
        var metadata = Metadata(bands: 1, bits: 8);

        FluentActions.Invoking(() => CogTileOverzoom.Upsample(new byte[256], metadata, new CogOverzoomQuadrant(1, 2, 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => CogTileOverzoom.Upsample(new byte[255], metadata, new CogOverzoomQuadrant(1, 0, 0)))
            .Should().Throw<InvalidDataException>();
    }

    private static CogMetadata Metadata(int bands, int bits)
        => new(16, 16, bands, $"uint{bits}", 3857, "DEFLATE", 16, 16, [], Extent, BitsPerSample: bits);
}
