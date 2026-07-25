// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
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
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

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

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_GdalMultiTileFixture_MapsFourCoordinatesToExactRangesAndPixels()
    {
        var fixtureDirectory = Path.Join(
            AppContext.BaseDirectory,
            "Features",
            "Protocols",
            "Cog",
            "Fixtures");
        var cogBytes = await File.ReadAllBytesAsync(
            Path.Join(fixtureDirectory, "lzw_pred2_uint8_multitile.tif"));
        var expectedPixels = await File.ReadAllBytesAsync(
            Path.Join(fixtureDirectory, "lzw_pred2_uint8_multitile.bin"));
        var rangeReader = new FixtureRangeReader(cogBytes);
        var metadata = await new CogMetadataExtractor()
            .ReadMetadataAsync(rangeReader, "bucket", "cog.tif");
        rangeReader.ClearRequests();

        metadata.OverviewLevels.Should().ContainSingle();
        var overview = metadata.OverviewLevels[0];
        overview.TileOffsets.Should().HaveCount(4);
        overview.TileByteCounts.Should().HaveCount(4);
        expectedPixels.Should().HaveCount(4 * metadata.TileWidth * metadata.TileHeight);

        var metadataReader = Substitute.For<ICogMetadataReader>();
        var cogStore = Substitute.For<ICogStore>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CogTileResolver(
            [rangeReader],
            metadataReader,
            cogStore,
            cache,
            NullLogger<CogTileResolver>.Instance);

        var requests = new[]
        {
            (Row: 0, Col: 0, TileIndex: 0),
            (Row: 0, Col: 1, TileIndex: 1),
            (Row: 1, Col: 0, TileIndex: 2),
            (Row: 1, Col: 1, TileIndex: 3)
        };
        var decodedTileLength = metadata.TileWidth * metadata.TileHeight;

        foreach (var request in requests)
        {
            rangeReader.ClearRequests();

            var result = await resolver.GetTileAsync(
                CreateRegistration(metadata),
                level: 8,
                row: request.Row,
                col: request.Col,
                RasterFormat.Raw);

            result.Should().NotBeNull();
            result!.Value.ContentType.Should().Be("application/octet-stream");
            result.Value.Data.Should().Equal(
                expectedPixels.AsSpan(request.TileIndex * decodedTileLength, decodedTileLength).ToArray());
            rangeReader.Requests.Should().ContainSingle()
                .Which.Should().Be(new RangeRequest(
                    overview.TileOffsets[request.TileIndex],
                    overview.TileByteCounts[request.TileIndex]));
        }
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

    private readonly record struct RangeRequest(long Offset, int Length);

    private sealed class FixtureRangeReader(byte[] data) : ICloudRangeReader
    {
        private readonly byte[] _data = data;
        private readonly List<RangeRequest> _requests = [];

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public List<RangeRequest> Requests => _requests;

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(new RangeRequest(offset, length));

            if (offset < 0 || offset >= _data.LongLength || length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var available = (int)Math.Min(length, _data.LongLength - offset);
            return Task.FromResult(_data.AsSpan((int)offset, available).ToArray());
        }

        public async Task<Stream> ReadRangeStreamAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            var range = await ReadRangeAsync(bucket, key, offset, length, cancellationToken)
                .ConfigureAwait(false);
            // Ownership transfers to the caller, which disposes the returned stream.
            // codeql[cs/local-not-disposed]
            return new MemoryStream(range, writable: false);
        }

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_data.LongLength);
        }

        public void ClearRequests() => _requests.Clear();
    }
}
