// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.TestKit.Formats;

namespace Honua.Server.Tests.Features.Tiles;

/// <summary>
/// Pins the archive reader the PMTiles proofs read through (honua-server#4421), and with it the
/// reason those proofs decode per directory entry instead of decoding the tile-data section whole.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class PMTilesArchiveReaderTests
{
    private static readonly byte[] FirstTile =
        MvtTileBuilder.PointLayer("first", [(10, 20, "a"), (30, 40, "b")]);

    private static readonly byte[] SecondTile =
        MvtTileBuilder.PointLayer("second", [(2048, 2048, "c")]);

    [Fact]
    public async Task ReadTiles_MultiTileArchive_ReturnsEachDeclaredSliceIntact()
    {
        var archive = await BuildArchiveAsync();

        var tiles = PMTilesArchiveReader.ReadTiles(archive);

        tiles.Should().HaveCount(2);
        tiles.Select(tile => tile.Data).Should().BeEquivalentTo(
            [FirstTile, SecondTile],
            "each entry's offset and length must slice out exactly the bytes that were added");
        foreach (var tile in tiles)
        {
            MvtTileDecoder.TryDecode(tile.Data, out var decoded).Should().BeTrue();
            decoded!.FeatureCount.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task DecodingTheWholeTileDataSection_CannotDistinguishOneTileFromMany()
    {
        // The reason the endpoint proof walks the directory: protobuf messages concatenate, so the
        // whole tile-data section of a multi-tile archive decodes as one merged tile whose layers
        // are the union of the individual tiles'. A directory entry with a wrong offset or length
        // leaves that section decoding perfectly while the slice a client fetches does not — so
        // "the section decodes" is not evidence that any addressable tile does.
        var archive = await BuildArchiveAsync();
        var header = PMTilesArchiveReader.ReadHeader(archive);
        var section = archive[(int)header.TileDataOffset..];

        MvtTileDecoder.TryDecode(section, out var merged).Should().BeTrue();
        merged!.Layers.Select(layer => layer.Name).Should().BeEquivalentTo(
            ["first", "second"],
            "two independently bounded tiles decode as a single two-layer message, which is exactly " +
            "why a section-wide decode proves nothing about the per-entry slices");
    }

    [Fact]
    public async Task ReadHeader_ArchiveWrittenByTheProducer_ReportsTheDeclaredLayout()
    {
        var archive = await BuildArchiveAsync();

        var header = PMTilesArchiveReader.ReadHeader(archive);

        header.TileType.Should().Be((byte)PMTilesTileType.Mvt);
        header.TileEntriesCount.Should().Be(2);
        header.Clustered.Should().BeTrue();
        header.RootDirectoryOffset.Should().Be(PMTilesArchiveReader.HeaderSize);
        header.LeafDirectoryLength.Should().Be(0, "two entries fit inside the root directory");
        (header.TileDataOffset + header.TileDataLength).Should().Be((ulong)archive.Length);
    }

    [Fact]
    public void ReadTiles_NonPMTilesBuffer_Fails()
    {
        var notAnArchive = new byte[PMTilesArchiveReader.HeaderSize];

        var read = () => PMTilesArchiveReader.ReadTiles(notAnArchive);

        read.Should().Throw<InvalidDataException>();
    }

    private static async Task<byte[]> BuildArchiveAsync()
    {
        var writer = new PMTilesWriter();
        writer.AddTile(0, 0, 0, FirstTile);
        writer.AddTile(1, 0, 0, SecondTile);

        using var output = new MemoryStream();
        await writer.WriteAsync(
            output,
            new PMTilesArchiveMetadata
            {
                MinLon = -180,
                MinLat = -85,
                MaxLon = 180,
                MaxLat = 85,
                MinZoom = 0,
                MaxZoom = 1,
            });

        return output.ToArray();
    }
}
