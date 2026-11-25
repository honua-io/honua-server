// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
                ? -10000 + (pixel.R * 65536d + pixel.G * 256d + pixel.B) * 0.1
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

    [UnitTest]
    public void EmptyTerrainRgbPng_IsPrecomputedTransparent256x256Png()
    {
        var decoded = MiniPngDecoder.Decode(PostgresTerrainTileService.EmptyTerrainRgbPng);

        decoded.Width.Should().Be(TerrainRgbEncoding.TileSize);
        decoded.Height.Should().Be(TerrainRgbEncoding.TileSize);
        decoded.Rgba.Should().OnlyContain(static value => value == 0);
    }

    [UnitTest]
    public async Task GetTerrainRgbTileAsync_UnsupportedSource_FailsWithoutOpeningBoundsQueryConnection()
    {
        var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        var crsRegistry = Substitute.For<ICrsRegistry>();
        crsRegistry.IsSridSupportedAsync(4326, Arg.Any<CancellationToken>()).Returns(true);
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns([new RasterInfo { Id = 1, LayerId = 1, Name = "rgb", Width = 10, Height = 10, BandCount = 3, Srid = 4326, PixelType = "8BUI" }]);

        var service = new PostgresTerrainTileService(
            connectionProvider,
            crsRegistry,
            rasterStore,
            NullLogger<PostgresTerrainTileService>.Instance);

        var act = () => service.GetTerrainRgbTileAsync(new TerrainTileRequest
        {
            LayerId = 1,
            Z = 0,
            X = 0,
            Y = 0
        });

        await act.Should().ThrowAsync<TerrainTileException>();
        await connectionProvider.DidNotReceiveWithAnyArgs().OpenConnectionAsync(default);
    }
}
