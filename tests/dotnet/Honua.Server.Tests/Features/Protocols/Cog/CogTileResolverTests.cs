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
        var tileData = CreateStandaloneJpeg();
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
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
        await metadataReader.DidNotReceiveWithAnyArgs()
            .ReadMetadataAsync(default!, default!, default!, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegTileRequestedAsPng_ReturnsNull()
    {
        var tileData = CreateStandaloneJpeg();
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
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
        await metadataReader.DidNotReceiveWithAnyArgs()
            .ReadMetadataAsync(default!, default!, default!, default);
        await rangeReader.DidNotReceiveWithAnyArgs()
            .ReadRangeAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_WithMisalignedExtent_ReturnsNull()
    {
        var tileData = CreateStandaloneJpeg();
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
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
            .ReadRangeAsync(default!, default!, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_WithMultipleOverviews_UsesResolutionMatchedOverview()
    {
        var fullResolutionTile = CreateStandaloneJpeg();
        var matchedOverviewTile = CreateStandaloneJpeg();
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
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var metadata = new CogMetadata(
            Width: 8,
            Height: 8,
            BandCount: 3,
            PixelType: "uint8",
            Srid: 3857,
            Compression: "JPEG",
            TileWidth: 1,
            TileHeight: 1,
            OverviewLevels:
            [
                new CogOverviewLevel(
                    Level: 0,
                    Width: 8,
                    Height: 8,
                    IfdOffset: 8,
                    TileOffsets: Enumerable.Repeat(16L, 8 * 8).ToArray(),
                    TileByteCounts: Enumerable.Repeat(fullResolutionTile.Length, 8 * 8).ToArray()),
                new CogOverviewLevel(
                    Level: 1,
                    Width: 4,
                    Height: 4,
                    IfdOffset: 24,
                    TileOffsets: Enumerable.Repeat(32L, 4 * 4).ToArray(),
                    TileByteCounts: Enumerable.Repeat(matchedOverviewTile.Length, 4 * 4).ToArray())
            ],
            Extent: CreateWorldExtent(),
            PhotometricInterpretation: 2);

        var result = await resolver.GetTileAsync(
            CreateRegistration(metadata),
            level: 2,
            row: 0,
            col: 0,
            RasterFormat.JPEG);

        result.Should().NotBeNull();
        result!.Value.Data.Should().Equal(matchedOverviewTile);
        await rangeReader.Received(1).ReadRangeAsync("bucket", "cog.tif", 32, matchedOverviewTile.Length, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_LosslessTileRequestedAsRaw_ReturnsExactPixels()
    {
        var tileData = new byte[256 * 256];
        for (var i = 0; i < tileData.Length; i++)
        {
            tileData[i] = (byte)i;
        }

        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);
        var metadata = CreateMetadata("NONE", tileData.Length) with
        {
            BandCount = 1,
            PhotometricInterpretation = 1,
        };

        var result = await resolver.GetTileAsync(
            CreateRegistration(metadata),
            level: 0,
            row: 0,
            col: 0,
            RasterFormat.Raw);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("application/octet-stream");
        result.Value.Data.Should().Equal(tileData);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_TruncatedJpegTables_ReturnsNull()
    {
        var tileData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var rangeReader = CreateRangeReader(tileData);
        var metadataReader = Substitute.For<ICogMetadataReader>();
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

        result.Should().BeNull();
    }

    private static ICloudRangeReader CreateRangeReader(byte[] tileData)
    {
        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.ReadRangeAsync("bucket", "cog.tif", 16, tileData.Length, Arg.Any<CancellationToken>())
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
        Width: compression == "JPEG" ? 1 : 256,
        Height: compression == "JPEG" ? 1 : 256,
        BandCount: 3,
        PixelType: "uint8",
        Srid: 3857,
        Compression: compression,
        TileWidth: compression == "JPEG" ? 1 : 256,
        TileHeight: compression == "JPEG" ? 1 : 256,
        OverviewLevels:
        [
            new CogOverviewLevel(
                Level: 0,
                Width: compression == "JPEG" ? 1 : 256,
                Height: compression == "JPEG" ? 1 : 256,
                IfdOffset: 8,
                TileOffsets: [16],
                TileByteCounts: [tileLength])
        ],
        Extent: extent ?? CreateWorldExtent(),
        PhotometricInterpretation: 2);

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

    private static byte[] CreateStandaloneJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////" +
        "2wBDAf//////////////////////////////////////////////////////////////////////////////////////" +
        "wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF/" +
        "/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAA" +
        "AAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/a" +
        "AAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA" +
        "/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==");
}
