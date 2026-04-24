// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.CloudCog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.CloudCog;

/// <summary>
/// Unit tests for cloud COG tile format handling.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class CloudCogTileResolverTests
{
    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegTileRequestedAsJpeg_ReturnsTile()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICloudCogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CloudCogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CloudCogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length)),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/jpeg");
        result.Value.Data.Should().Equal(tileData);
        await metadataReader.DidNotReceiveWithAnyArgs()
            .ReadMetadataAsync(default!, default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegTileRequestedAsPng_ReturnsNull()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICloudCogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CloudCogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CloudCogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length)),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.PNG);

        result.Should().BeNull();
        await metadataReader.DidNotReceiveWithAnyArgs()
            .ReadMetadataAsync(default!, default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_WithMisalignedExtent_ReturnsNull()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICloudCogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CloudCogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CloudCogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length, CreateMisalignedExtent())),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().BeNull();
        await rangeReader.DidNotReceiveWithAnyArgs()
            .ReadRangeAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_WithMultipleOverviews_UsesResolutionMatchedOverview()
    {
        var fullResolutionTile = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var matchedOverviewTile = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.ReadRangeAsync("bucket", "cog.tif", Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<long>(2) switch
            {
                16L => fullResolutionTile,
                32L => matchedOverviewTile,
                _ => Array.Empty<byte>()
            });

        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICloudCogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CloudCogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CloudCogTileResolver>.Instance);

        var metadata = new CogMetadata(
            Width: 4096,
            Height: 4096,
            BandCount: 3,
            PixelType: "uint8",
            Srid: 3857,
            Compression: "JPEG",
            TileWidth: 256,
            TileHeight: 256,
            OverviewLevels:
            [
                new CogOverviewLevel(Level: 0, Width: 4096, Height: 4096, IfdOffset: 8, TileOffsets: [16], TileByteCounts: [fullResolutionTile.Length]),
                new CogOverviewLevel(Level: 1, Width: 2048, Height: 2048, IfdOffset: 24, TileOffsets: [32], TileByteCounts: [matchedOverviewTile.Length])
            ],
            Extent: CreateWorldExtent());

        var result = await resolver.GetTileAsync(
            CreateRegistration(metadata),
            level: 3,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().NotBeNull();
        result!.Value.Data.Should().Equal(matchedOverviewTile);
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 32, matchedOverviewTile.Length, Arg.Any<CancellationToken>());
    }

    private static ICloudRangeReader CreateRangeReader(byte[] tileData)
    {
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 16, tileData.Length, Arg.Any<CancellationToken>())
            .Returns(tileData);
        return rangeReader;
    }

    private static CloudCogRegistration CreateRegistration(CogMetadata metadata) => new()
    {
        Id = 42,
        LayerId = 1,
        Name = "test-cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "bucket",
        ObjectKey = "cog.tif",
        Metadata = metadata,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CogMetadata CreateMetadata(string compression, int tileLength, RasterExtent? extent = null) => new(
        Width: 256,
        Height: 256,
        BandCount: 3,
        PixelType: "uint8",
        Srid: 3857,
        Compression: compression,
        TileWidth: 256,
        TileHeight: 256,
        OverviewLevels:
        [
            new CogOverviewLevel(
                Level: 0,
                Width: 256,
                Height: 256,
                IfdOffset: 8,
                TileOffsets: [16],
                TileByteCounts: [tileLength])
        ],
        Extent: extent ?? CreateWorldExtent());

    private static RasterExtent CreateWorldExtent() => new()
    {
        XMin = -SpatialConstants.WebMercatorExtent,
        YMin = -SpatialConstants.WebMercatorExtent,
        XMax = SpatialConstants.WebMercatorExtent,
        YMax = SpatialConstants.WebMercatorExtent,
        Srid = 3857
    };

    private static RasterExtent CreateMisalignedExtent() => new()
    {
        XMin = 0,
        YMin = 0,
        XMax = 256,
        YMax = 256,
        Srid = 3857
    };
}
