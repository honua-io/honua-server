// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// Compares a served JPEG tile with GDAL's own decode of the source COG.
/// The <c>jpeg_*</c> fixtures and their <c>.bin</c> oracles come from
/// <c>scripts/raster/generate-jpeg-cog-fixtures.py</c> (GDAL 3.13.1): the expected samples are
/// what libtiff/libjpeg decode from the TIFF, independently of Honua's table assembly.
/// </summary>
internal static class GdalJpegOracle
{
    public const int TileSize = 128;

    public static string FixtureDirectory { get; } = Path.Join(AppContext.BaseDirectory, "CogFixtures");

    /// <summary>
    /// Decodes <paramref name="jpeg"/> with Skia (libjpeg-turbo) and requires every sample to
    /// equal GDAL's decode. Both decoders run the same integer IDCT and colour conversion, so an
    /// exact match is expected; a wrong table or colour transform moves samples by tens of levels.
    /// </summary>
    public static void AssertMatchesGdalDecode(byte[] jpeg, byte[] expected, int bands)
    {
        expected.Should().HaveCount(TileSize * TileSize * bands);
        using var decoded = SKBitmap.Decode(jpeg);
        decoded.Should().NotBeNull("the served tile must be a decodable JPEG stream");
        decoded.Width.Should().Be(TileSize);
        decoded.Height.Should().Be(TileSize);

        var maxDifference = 0;
        var firstMismatch = string.Empty;
        for (var row = 0; row < TileSize; row++)
        {
            for (var col = 0; col < TileSize; col++)
            {
                var pixel = decoded.GetPixel(col, row);
                var offset = ((row * TileSize) + col) * bands;
                ReadOnlySpan<byte> actual = bands == 1
                    ? [pixel.Red]
                    : [pixel.Red, pixel.Green, pixel.Blue];
                if (bands == 1)
                {
                    pixel.Green.Should().Be(pixel.Red);
                    pixel.Blue.Should().Be(pixel.Red);
                }
                for (var band = 0; band < bands; band++)
                {
                    var difference = Math.Abs(actual[band] - expected[offset + band]);
                    if (difference > maxDifference)
                    {
                        maxDifference = difference;
                        firstMismatch = $"({col},{row}) band {band}: served {actual[band]}, GDAL {expected[offset + band]}";
                    }
                }
            }
        }

        maxDifference.Should().Be(0, "every decoded sample must equal GDAL's decode; worst {0}", firstMismatch);
    }
}
