// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Core.Tests.Features.Tiles.PMTiles;

public class PMTilesWriterTests
{
    private static readonly PMTilesArchiveMetadata DefaultMetadata = new()
    {
        MinLon = -180,
        MinLat = -85,
        MaxLon = 180,
        MaxLat = 85,
        MinZoom = 0,
        MaxZoom = 2
    };

    [Fact]
    public async Task WriteAsync_SingleTile_ProducesValidArchive()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, CreateFakeTileData(100));

        using var stream = new MemoryStream();
        var bytesWritten = await writer.WriteAsync(stream, DefaultMetadata);

        bytesWritten.Should().BeGreaterThan(PMTilesHeader.HeaderSize);
        stream.Position = 0;

        // Validate header
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        header.AddressedTilesCount.Should().Be(1);
        header.TileEntriesCount.Should().Be(1);
        header.TileType.Should().Be(PMTilesTileType.Mvt);
        header.MinZoom.Should().Be(0);
        header.MaxZoom.Should().Be(2);
    }

    [Fact]
    public async Task WriteAsync_MultipleTilesMultipleZooms_ProducesValidArchive()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, CreateFakeTileData(50));
        writer.AddTile(1, 0, 0, CreateFakeTileData(60));
        writer.AddTile(1, 1, 0, CreateFakeTileData(70));
        writer.AddTile(1, 0, 1, CreateFakeTileData(80));
        writer.AddTile(1, 1, 1, CreateFakeTileData(90));

        using var stream = new MemoryStream();
        var bytesWritten = await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        header.AddressedTilesCount.Should().Be(5);
        header.TileEntriesCount.Should().Be(5);
        header.Clustered.Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_EmptyTiles_AreSkipped()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, []);
        writer.AddTile(1, 0, 0, CreateFakeTileData(50));

        writer.TileCount.Should().Be(1);

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        header.AddressedTilesCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_WithGzipCompression_SetsHeaderCorrectly()
    {
        var writer = new PMTilesWriter(PMTilesCompression.Gzip, PMTilesCompression.Gzip);
        writer.AddTile(0, 0, 0, CreateFakeTileData(100));

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        header.TileCompression.Should().Be(PMTilesCompression.Gzip);
        header.InternalCompression.Should().Be(PMTilesCompression.Gzip);
    }

    [Fact]
    public async Task WriteAsync_MagicBytesAreCorrect()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, CreateFakeTileData(50));

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var magic = new byte[7];
        await stream.ReadExactlyAsync(magic);

        magic.Should().BeEquivalentTo("PMTiles"u8.ToArray());
    }

    [Fact]
    public async Task WriteAsync_VersionIs3()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, CreateFakeTileData(50));

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 7;
        var version = stream.ReadByte();

        version.Should().Be(3);
    }

    [Fact]
    public async Task WriteAsync_BoundsAreEncodedAsE7()
    {
        var metadata = new PMTilesArchiveMetadata
        {
            MinLon = -122.5,
            MinLat = 37.5,
            MaxLon = -121.5,
            MaxLat = 38.5,
            MinZoom = 5,
            MaxZoom = 10
        };

        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(5, 5, 12, CreateFakeTileData(50));

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, metadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        header.MinLonE7.Should().Be(-1_225_000_000);
        header.MinLatE7.Should().Be(375_000_000);
        header.MaxLonE7.Should().Be(-1_215_000_000);
        header.MaxLatE7.Should().Be(385_000_000);
        header.MinZoom.Should().Be(5);
        header.MaxZoom.Should().Be(10);
    }

    [Fact]
    public async Task WriteAsync_TileDataIsRetrievable()
    {
        var tileData = CreateFakeTileData(200);
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, tileData);

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        // Read tile data from the archive
        stream.Position = (long)header.TileDataOffset;
        var retrievedTile = new byte[tileData.Length];
        await stream.ReadExactlyAsync(retrievedTile);

        retrievedTile.Should().BeEquivalentTo(tileData);
    }

    [Fact]
    public async Task WriteAsync_LayoutIsHeaderRootMetadataLeafData()
    {
        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, CreateFakeTileData(50));

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DefaultMetadata);

        stream.Position = 0;
        var headerBytes = new byte[PMTilesHeader.HeaderSize];
        await stream.ReadExactlyAsync(headerBytes);
        var header = PMTilesHeader.ReadFrom(headerBytes);

        // Root directory follows header
        header.RootDirectoryOffset.Should().Be((ulong)PMTilesHeader.HeaderSize);

        // JSON metadata follows root directory
        header.JsonMetadataOffset.Should().Be(header.RootDirectoryOffset + header.RootDirectoryLength);

        // Leaf directory follows JSON metadata
        header.LeafDirectoryOffset.Should().Be(header.JsonMetadataOffset + header.JsonMetadataLength);

        // Tile data follows leaf directory
        header.TileDataOffset.Should().Be(header.LeafDirectoryOffset + header.LeafDirectoryLength);
    }

    [Fact]
    public async Task WriteAsync_CancellationToken_Throws()
    {
        var writer = new PMTilesWriter();
        writer.AddTile(0, 0, 0, CreateFakeTileData(50));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var stream = new MemoryStream();
        var act = () => writer.WriteAsync(stream, DefaultMetadata, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AddTile_NullData_ThrowsArgumentNullException()
    {
        var writer = new PMTilesWriter();
        var act = () => writer.AddTile(0, 0, 0, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TileCount_ReflectsAddedTiles()
    {
        var writer = new PMTilesWriter();
        writer.TileCount.Should().Be(0);

        writer.AddTile(0, 0, 0, CreateFakeTileData(10));
        writer.TileCount.Should().Be(1);

        writer.AddTile(1, 0, 0, CreateFakeTileData(10));
        writer.TileCount.Should().Be(2);
    }

    private static byte[] CreateFakeTileData(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        // Ensure first and last bytes are nonzero for unique hash behavior
        data[0] = (byte)(data[0] | 1);
        data[^1] = (byte)(data[^1] | 1);
        return data;
    }
}
