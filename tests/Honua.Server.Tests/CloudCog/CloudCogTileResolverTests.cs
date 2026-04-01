// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
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
[Protocol(Protocols.ImageServer)]
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

    private static CogMetadata CreateMetadata(string compression, int tileLength) => new(
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
        Extent: new RasterExtent
        {
            XMin = 0,
            YMin = 0,
            XMax = 256,
            YMax = 256,
            Srid = 3857
        });
}
