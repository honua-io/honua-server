// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

// The bounded-roster COG: GDAL 3.14 COG driver, TILING_SCHEME=GoogleMapsCompatible,
// COMPRESS=DEFLATE, a 256x256 EPSG:4326 gradient over -122.46..-122.38, 37.72..37.80 warped to native
// zoom 12 (512x512, one z11 overview). Each .bin is GDAL's own nearest-neighbour read of that tile's
// EPSG:3857 extent at 256x256 (gdal.Translate projWin, resampleAlg=nearest), not a Honua snapshot.
[Protocol(TestProtocols.ImageServer)]
public sealed class CogOverzoomTileTests
{
    private const string Fixture = "cog_roster_z12_deflate";
    private const int TileSize = 256;

    [Theory]
    [InlineData(14, 6333, 2621)]
    [InlineData(13, 3166, 1310)]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_FinerThanNativeZoom_MatchesGdalNearestRead(int level, int row, int col)
    {
        var expected = await ReadFixtureAsync($"{Fixture}_z{level}.bin");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = await CreateResolverAsync(cache);

        var png = await resolver.GetTileAsync(Registration, level, row, col, RasterFormat.PNG)
            ?? throw new InvalidOperationException("the overzoomed tile was not served");

        png.ContentType.Should().Be("image/png");
        png.Width.Should().Be(TileSize);
        png.Height.Should().Be(TileSize);
        var chunks = ReadPngChunks(png.Data);
        chunks["tRNS"].Should().Equal(0, 0); // GDAL nodata 0 stays transparent.
        InflateSamples(chunks["IDAT"]).Should().Equal(expected);
    }

    [Theory]
    [InlineData(RasterFormat.TIFF)]
    [InlineData(RasterFormat.COG)]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_OverzoomedTiff_CarriesRequestedTileGeoreferencing(RasterFormat format)
    {
        var expected = await ReadFixtureAsync($"{Fixture}_z14.bin");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = await CreateResolverAsync(cache);

        var tiff = await resolver.GetTileAsync(Registration, 14, 6333, 2621, format)
            ?? throw new InvalidOperationException("the overzoomed TIFF tile was not served");

        tiff.ContentType.Should().Be("image/tiff");
        tiff.Data[^(expected.Length + 4)..^4].Should().Equal(expected);
        var output = await new CogMetadataExtractor().ReadMetadataAsync(
            new CogPinnedRangeReader(CreateReader(tiff.Data), "fixture-v1"), "fixtures", "output");
        var bounds = TileMath.GetTileBounds(2621, 6333, 14);
        output.Width.Should().Be(TileSize);
        output.Srid.Should().Be(3857);
        output.Extent.XMin.Should().BeApproximately(bounds.XMin, 1e-6);
        output.Extent.YMax.Should().BeApproximately(bounds.YMax, 1e-6);
    }

    [Theory]
    [InlineData(14, 1, 1)]
    [InlineData(23, 6333 << 9, 2621 << 9)]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_OutsideCogOrBeyondOverzoomRange_ReturnsNull(int level, int row, int col)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = await CreateResolverAsync(cache);

        var tile = await resolver.GetTileAsync(Registration, level, row, col, RasterFormat.PNG);

        tile.Should().BeNull();
    }

    [Fact]
    [Operation(Operations.GetTile)]
    public async Task GetTileAsync_NativeZoom_StillServesStoredTile()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = await CreateResolverAsync(cache);

        var tile = await resolver.GetTileAsync(Registration, 12, 6333 >> 2, 2621 >> 2, RasterFormat.PNG);

        tile.Should().NotBeNull();
        tile!.Value.Width.Should().Be(TileSize);
    }

    private static CogRegistration Registration { get; } = new()
    {
        Id = 1,
        LayerId = 5000,
        Name = Fixture,
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static async Task<CogTileResolver> CreateResolverAsync(IMemoryCache cache)
    {
        var source = await ReadFixtureAsync(Fixture + ".tif");
        return new CogTileResolver([CreateReader(source)], new CogMetadataExtractor(),
            Substitute.For<ICogStore>(), cache, NullLogger<CogTileResolver>.Instance);
    }

    private static Task<byte[]> ReadFixtureAsync(string name)
        => File.ReadAllBytesAsync(Path.Join(AppContext.BaseDirectory, "CogFixtures", name));

    private static Dictionary<string, byte[]> ReadPngChunks(byte[] png)
    {
        png[..8].Should().Equal(137, 80, 78, 71, 13, 10, 26, 10);
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            chunks.Add(Encoding.ASCII.GetString(png, offset + 4, 4), png.AsSpan(offset + 8, length).ToArray());
            offset += 12 + length;
        }
        return chunks;
    }

    private static byte[] InflateSamples(byte[] idat)
    {
        using var zlib = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var samples = new byte[TileSize * TileSize];
        for (var row = 0; row < TileSize; row++)
        {
            zlib.ReadByte().Should().Be(0); // Independently undo filter None.
            zlib.ReadExactly(samples.AsSpan(row * TileSize, TileSize));
        }
        return samples;
    }

    // Serves the fixture bytes for every ETag-pinned range read the resolver makes.
    private static ICloudRangeReader CreateReader(byte[] source)
    {
        var reader = Substitute.For<ICloudRangeReader>();
        reader.Provider.Returns(CloudStorageProvider.AwsS3);
        reader.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CloudObjectMetadata { SizeBytes = source.Length, ETag = "fixture-v1" });
        reader.ReadRangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), "fixture-v1",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var offset = (int)call.ArgAt<long>(2);
                return source.AsSpan(offset, Math.Min(call.ArgAt<int>(3), source.Length - offset)).ToArray();
            });
        return reader;
    }
}
