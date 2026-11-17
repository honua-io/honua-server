// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.Cog;

// A request deeper than the COG's full-resolution zoom is served from the enlarged ancestor tile
// (a GoogleMapsCompatible COG written at about z12 and read at z14).
[Protocol(TestProtocols.ImageServer)]
public class CogOverzoomTileTests
{
    private const int TileSize = 256;
    private const int HeaderBytes = 16;

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_RequestTwoLevelsBelowNativeZoom_EnlargesAncestorQuadrant()
    {
        // Native tile z12 (655, 1583) holds the roster tile z14 (2621, 6333): quadrant x=1, y=1.
        var extent = TileExtent(level: 12, row: 1583, col: 655);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (resolver, _) = CreateResolver(CreateMetadata("NONE", extent), cache);

        var result = await resolver.GetTileAsync(
            CreateRegistration(extent), level: 14, row: 6333, col: 2621, RasterFormat.Raw);

        result.Should().NotBeNull();
        var samples = result!.Value.Data;
        samples.Should().HaveCount(TileSize * TileSize);
        samples[0].Should().Be(ColumnValue(64));
        samples[(10 * TileSize) + 5].Should().Be(ColumnValue(64 + 1));
        samples[(255 * TileSize) + 255].Should().Be(ColumnValue(64 + 63));
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_OverzoomRequestedAsPng_EncodesEnlargedSamples()
    {
        var extent = WorldExtent();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (resolver, _) = CreateResolver(CreateMetadata("NONE", extent), cache);

        var result = await resolver.GetTileAsync(
            CreateRegistration(extent), level: 2, row: 1, col: 2, RasterFormat.PNG);

        result.Should().NotBeNull();
        result!.Value.ContentType.Should().Be("image/png");
        using var bitmap = SKBitmap.Decode(result.Value.Data);
        bitmap.Width.Should().Be(TileSize);
        bitmap.GetPixel(10, 200).Red.Should().Be(ColumnValue(128 + 2));
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_DeeperThanOverzoomBound_ReturnsNullWithoutReadingTile()
    {
        var extent = WorldExtent();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (resolver, rangeReader) = CreateResolver(CreateMetadata("NONE", extent), cache);

        var result = await resolver.GetTileAsync(
            CreateRegistration(extent), level: CogOverzoomTile.MaxZoomDelta + 1, row: 0, col: 0, RasterFormat.Raw);

        result.Should().BeNull();
        await rangeReader.DidNotReceiveWithAnyArgs()
            .ReadRangeAsync(default!, default!, default, default, default!, default);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_JpegOverzoom_ReturnsNullWithoutReadingTile()
    {
        var extent = WorldExtent();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (resolver, rangeReader) = CreateResolver(CreateMetadata("JPEG", extent), cache);

        var result = await resolver.GetTileAsync(
            CreateRegistration(extent), level: 1, row: 0, col: 0, RasterFormat.JPEG);

        result.Should().BeNull();
        await rangeReader.DidNotReceiveWithAnyArgs()
            .ReadRangeAsync(default!, default!, default, default, default!, default);
    }

    [UnitTest]
    public void Enlarge_TopRightQuadrant_ReadsColumnOffsetFromColAndRowOffsetFromRow()
    {
        var metadata = CreateMetadata("NONE", WorldExtent()) with { TileWidth = 4, TileHeight = 4 };
        var samples = Enumerable.Range(0, 4 * 4).Select(value => (byte)value).ToArray();
        var window = CogOverzoomTile.CreateWindow(metadata, ancestorTileIndex: 0, (ZoomDelta: 1, Row: 0, Col: 1));

        var enlarged = CogOverzoomTile.Enlarge(samples, metadata, window!);

        // Each sample holds its source index (row * 4 + column). Row 0, col 1 is the top-right
        // quadrant, so the window origin is source column 2, row 0.
        enlarged.Should().NotBeNull();
        enlarged![..4].Should().Equal(2, 2, 3, 3);
        enlarged[(2 * 4)..(3 * 4)].Should().Equal(6, 6, 7, 7);
    }

    [UnitTest]
    public void Enlarge_MultiBandWindow_CopiesWholePixels()
    {
        var metadata = CreateMetadata("NONE", WorldExtent()) with { BandCount = 2, TileWidth = 4, TileHeight = 4 };
        var samples = Enumerable.Range(0, 4 * 4 * 2).Select(value => (byte)value).ToArray();
        var window = CogOverzoomTile.CreateWindow(metadata, ancestorTileIndex: 0, (ZoomDelta: 1, Row: 1, Col: 1));

        var enlarged = CogOverzoomTile.Enlarge(samples, metadata, window!);

        // Window origin is source pixel (2, 2): bytes 20..21; each source pixel covers 2 x 2 outputs.
        enlarged.Should().NotBeNull();
        enlarged![..4].Should().Equal(20, 21, 20, 21);
        enlarged[(3 * 4 * 2)..((3 * 4 * 2) + 2)].Should().Equal(28, 29);
    }

    private static byte ColumnValue(int column) => (byte)(40 + ((column / 16) * 8));

    private static (CogTileResolver Resolver, ICloudRangeReader RangeReader) CreateResolver(CogMetadata metadata, IMemoryCache cache)
    {
        var source = new byte[HeaderBytes + (TileSize * TileSize)];
        for (var offset = 0; offset < TileSize * TileSize; offset++)
        {
            source[HeaderBytes + offset] = ColumnValue(offset % TileSize);
        }

        var rangeReader = Substitute.For<ICloudRangeReader>();
        rangeReader.Provider.Returns(CloudStorageProvider.AwsS3);
        rangeReader.GetObjectMetadataAsync("bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = source.Length, ETag = "etag-1" });
        rangeReader.ReadRangeAsync("bucket", "cog.tif", HeaderBytes, TileSize * TileSize, "etag-1", Arg.Any<CancellationToken>())
            .Returns(source[HeaderBytes..]);
        var metadataReader = Substitute.For<ICogMetadataReader>();
        metadataReader.ReadMetadataAsync(Arg.Any<ICloudRangeReader>(), "bucket", "cog.tif", Arg.Any<CancellationToken>())
            .Returns(metadata);
        var resolver = new CogTileResolver(
            [rangeReader], metadataReader, Substitute.For<ICogStore>(),
            cache, NullLogger<CogTileResolver>.Instance);
        return (resolver, rangeReader);
    }

    private static CogRegistration CreateRegistration(RasterExtent extent) => new()
    {
        Id = 7,
        LayerId = 5000,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "bucket",
        ObjectKey = "cog.tif",
        Metadata = CreateMetadata("NONE", extent),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CogMetadata CreateMetadata(string compression, RasterExtent extent) => new(
        Width: TileSize,
        Height: TileSize,
        BandCount: 1,
        PixelType: "uint8",
        Srid: 3857,
        Compression: compression,
        TileWidth: TileSize,
        TileHeight: TileSize,
        OverviewLevels:
        [
            new CogOverviewLevel(Level: 0, Width: TileSize, Height: TileSize, IfdOffset: 8,
                TileOffsets: [HeaderBytes], TileByteCounts: [TileSize * TileSize])
        ],
        Extent: extent);

    private static RasterExtent TileExtent(int level, int row, int col)
    {
        var bounds = TileMath.GetTileBounds(col, row, level);
        return new RasterExtent { XMin = bounds.XMin, YMin = bounds.YMin, XMax = bounds.XMax, YMax = bounds.YMax, Srid = 3857 };
    }

    private static RasterExtent WorldExtent() => new()
    {
        XMin = -SpatialConstants.WebMercatorExtent,
        YMin = -SpatialConstants.WebMercatorExtent,
        XMax = SpatialConstants.WebMercatorExtent,
        YMax = SpatialConstants.WebMercatorExtent,
        Srid = 3857
    };
}
