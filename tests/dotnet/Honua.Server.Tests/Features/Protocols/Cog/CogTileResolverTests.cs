// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// Unit tests for COG tile format handling.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class CogTileResolverTests
{
    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegTileRequestedAsJpeg_ReturnsTile()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(
                Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(CreateMetadata("JPEG", tileData.Length));
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length)),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/jpeg");
        result!.Value.Data.Should().Equal(tileData);
        await rangeReader.Received(1).ReadRangeAsync(
            "bucket", "cog.tif", 16, tileData.Length, "etag-1", Arg.Any<CancellationToken>());
        await metadataReader.Received(1).ReadMetadataAsync(
            Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegTileRequestedAsPng_ReturnsNull()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(
                Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(CreateMetadata("JPEG", tileData.Length));
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length)),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.PNG);

        result.Should().BeNull();
        await metadataReader.Received(1).ReadMetadataAsync(
            Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_WithMisalignedExtent_ReturnsNull()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(
                Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(CreateMetadata("JPEG", tileData.Length, CreateMisalignedExtent()));
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(CreateMetadata("JPEG", tileData.Length, CreateMisalignedExtent())),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().BeNull();
        await rangeReader.DidNotReceiveWithAnyArgs()
            .ReadRangeAsync(default!, default!, default, default, default!, default);
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
        rangeReader.GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = 1L << 30, ETag = "etag-1" });
        rangeReader.ReadRangeAsync("bucket", "cog.tif", Arg.Any<long>(), Arg.Any<int>(), "etag-1", Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<long>(2) switch
            {
                16L => fullResolutionTile,
                32L => matchedOverviewTile,
                _ => Array.Empty<byte>()
            });

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

        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(
                Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(
            CreateRegistration(metadata),
            level: 3,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().NotBeNull();
        result!.Value.Data.Should().Equal(matchedOverviewTile);
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 32, matchedOverviewTile.Length, "etag-1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_TileByteCountAboveBound_DoesNotReadRemoteRange()
    {
        var rangeReader = CreateRangeReader([0x00]);
        var metadata = CreateMetadata("NONE", 128 * 1024 * 1024 + 1);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(
                Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            Substitute.For<ICogStore>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.PNG);

        result.Should().BeNull();
        await rangeReader.DidNotReceiveWithAnyArgs().ReadRangeAsync(
            default!, default!, default, default, default!, default);
        await rangeReader.DidNotReceiveWithAnyArgs().ReadRangeAsync(
            default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_ReplacedObject_RescansOffsetsAndPinsEveryRead()
    {
        var tile = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tile);
        var metadata = CreateMetadata("JPEG", tile.Length);
        var replacement = metadata with
        {
            OverviewLevels = [metadata.OverviewLevels[0] with { TileOffsets = [32L] }]
        };
        rangeReader.GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = 1024, ETag = "etag-1" },
                new CloudObjectMetadata { SizeBytes = 1024, ETag = "etag-2" });
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 32, tile.Length, "etag-2", Arg.Any<CancellationToken>())
            .Returns(tile);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        var scan = 0;
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<ICloudRangeReader>(0).ReadRangeAsync("bucket", "cog.tif", 0, 8);
                return ++scan == 1 ? metadata : replacement;
            });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver([rangeReader], metadataReader, Substitute.For<ICogStore>(),
            cache, NullLogger<CogTileResolver>.Instance);

        (await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.JPEG)).Should().NotBeNull();
        (await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.JPEG)).Should().NotBeNull();

        scan.Should().Be(2);
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 0, 8, "etag-1", Arg.Any<CancellationToken>());
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 0, 8, "etag-2", Arg.Any<CancellationToken>());
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 32, tile.Length, "etag-2", Arg.Any<CancellationToken>());
        await rangeReader.DidNotReceiveWithAnyArgs().ReadRangeAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_TruncatedTile_FailsClosed()
    {
        var tile = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var metadata = CreateMetadata("JPEG", tile.Length);
        var rangeReader = CreateRangeReader(tile);
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 16, tile.Length, "etag-1", Arg.Any<CancellationToken>())
            .Returns(tile[..2]);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver([rangeReader], metadataReader, Substitute.For<ICogStore>(),
            cache, NullLogger<CogTileResolver>.Instance);

        var result = await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.JPEG);

        result.Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(-1L, 4)]
    [InlineData(1023L, 4)]
    [InlineData(long.MaxValue, 4)]
    [InlineData(16L, int.MaxValue)]
    [InlineData(16L, -1)]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_InvalidTileRange_DoesNotReadPayload(long offset, int count)
    {
        var metadata = CreateMetadata("JPEG", count);
        metadata = metadata with { OverviewLevels = [metadata.OverviewLevels[0] with { TileOffsets = [offset] }] };
        var rangeReader = CreateRangeReader([0]);
        rangeReader.GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = 1024, ETag = "etag-1" });
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver([rangeReader], metadataReader, Substitute.For<ICogStore>(),
            cache, NullLogger<CogTileResolver>.Instance);

        (await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.JPEG)).Should().BeNull();

        await rangeReader.DidNotReceiveWithAnyArgs().ReadRangeAsync(default!, default!, default, default, default!, default);
        await rangeReader.DidNotReceiveWithAnyArgs().ReadRangeAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_UnchangedObject_ReusesPinnedMetadata()
    {
        var tile = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var metadata = CreateMetadata("JPEG", tile.Length);
        var rangeReader = CreateRangeReader(tile);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver([rangeReader], metadataReader, Substitute.For<ICogStore>(),
            cache, NullLogger<CogTileResolver>.Instance);

        for (var request = 0; request < 2; request++)
        {
            (await resolver.GetTileAsync(CreateRegistration(metadata), 0, 0, 0, RasterFormat.JPEG)).Should().NotBeNull();
        }
        await metadataReader.Received(1).ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>());
        await rangeReader.Received(2).GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>());
        await rangeReader.Received(2).ReadRangeAsync("bucket", "cog.tif", 16, tile.Length, "etag-1", Arg.Any<CancellationToken>());
    }

    private static ICloudRangeReader CreateRangeReader(byte[] tileData)
    {
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = 1L << 30, ETag = "etag-1" });
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 16, tileData.Length, Arg.Any<CancellationToken>())
            .Returns(tileData);
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 16, tileData.Length, "etag-1", Arg.Any<CancellationToken>())
            .Returns(tileData);
        return rangeReader;
    }

    private static CogRegistration CreateRegistration(CogMetadata metadata) => new()
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
