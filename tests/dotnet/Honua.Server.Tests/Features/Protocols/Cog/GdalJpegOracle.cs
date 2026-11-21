// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Cog;

internal static class GdalJpegOracle
{
    public const int TileSize = 128;

    public static string FixtureDirectory { get; } = Path.Join(AppContext.BaseDirectory, "CogFixtures");

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
