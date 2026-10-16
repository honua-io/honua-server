// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Unit")]
public sealed class PostgresTerrainPngTests
{
    [UnitTest]
    public void TerrainPng_RoundTripsMissingAndFiniteElevationPixelsWithDistinctAlpha()
    {
        double?[] elevations = [null, 0, -17.25, -10000, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 123.4];
        byte[] rgba = new byte[elevations.Length * 4];
        for (var index = 0; index < elevations.Length; index++)
        {
            PostgresTerrainTileService.WriteTerrainPixel(rgba.AsSpan(index * 4, 4), elevations[index]);
        }

        var png = PostgresTerrainTileService.PngEncoder.EncodeRgba(rgba, 4, 2);
        var decoded = MiniPngDecoder.Decode(png);

        decoded.Width.Should().Be(4);
        decoded.Height.Should().Be(2);
        // Literal Terrain-RGB samples, independently checked after PNG inflate.
        // Missing samples are transparent; valid -10000 remains opaque black.
        decoded.Rgba.Should().Equal(new byte[]
        {
            0, 0, 0, 0, 1, 134, 160, 255, 1, 133, 244, 255, 0, 0, 0, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 139, 114, 255
        });

        // Stock QGIS 3.44.14 MapTiler interpretation uses alpha to distinguish
        // missing data, then applies this RGB decoding formula to opaque pixels.
        for (var index = 0; index < elevations.Length; index++)
        {
            var pixel = decoded.Pixel(index % 4, index / 4);
            var nativeValue = pixel.A == 255
                ? -10000 + (pixel.R * 65536 + pixel.G * 256 + pixel.B) * 0.1
                : double.NaN;
            if (elevations[index] is { } elevation && double.IsFinite(elevation))
            {
                nativeValue.Should().BeApproximately(elevation, 0.051);
            }
            else
            {
                double.IsNaN(nativeValue).Should().BeTrue();
            }
        }
    }

    [UnitTest]
    public void TerrainPng_EmptyCoverageIsTransparentForEveryPixel()
    {
        var decoded = MiniPngDecoder.Decode(PostgresTerrainTileService.PngEncoder.EncodeRgba(new byte[4 * 3 * 4], 4, 3));

        decoded.Width.Should().Be(4);
        decoded.Height.Should().Be(3);
        decoded.Rgba.Should().OnlyContain(value => value == 0);
    }

    [UnitTest]
    public void TerrainPng_RejectsRgbBufferThatWouldDiscardAlpha()
    {
        Action encode = () => PostgresTerrainTileService.PngEncoder.EncodeRgba(new byte[4 * 3 * 3], 4, 3);

        encode.Should().Throw<ArgumentException>();
    }
}
