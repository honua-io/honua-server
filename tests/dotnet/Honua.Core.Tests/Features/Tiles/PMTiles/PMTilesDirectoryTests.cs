// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Tiles.PMTiles;

public class PMTilesDirectoryTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeDeserialize_SingleEntry_RoundTrips()
    {
        var entries = new PMTilesEntry[]
        {
            new(TileId: 0, Offset: 0, Length: 100, RunLength: 1)
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);
        var result = PMTilesDirectory.DeserializeEntries(bytes);

        result.Should().HaveCount(1);
        result[0].TileId.Should().Be(0UL);
        result[0].Offset.Should().Be(0UL);
        result[0].Length.Should().Be(100U);
        result[0].RunLength.Should().Be(1U);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeDeserialize_ContiguousEntries_RoundTrips()
    {
        // Contiguous entries: each entry's offset == previous offset + previous length
        var entries = new PMTilesEntry[]
        {
            new(TileId: 0, Offset: 0, Length: 100, RunLength: 1),
            new(TileId: 1, Offset: 100, Length: 200, RunLength: 1),
            new(TileId: 2, Offset: 300, Length: 150, RunLength: 1),
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);
        var result = PMTilesDirectory.DeserializeEntries(bytes);

        result.Should().HaveCount(3);
        for (var i = 0; i < entries.Length; i++)
        {
            result[i].TileId.Should().Be(entries[i].TileId, $"TileId mismatch at index {i}");
            result[i].Offset.Should().Be(entries[i].Offset, $"Offset mismatch at index {i}");
            result[i].Length.Should().Be(entries[i].Length, $"Length mismatch at index {i}");
            result[i].RunLength.Should().Be(entries[i].RunLength, $"RunLength mismatch at index {i}");
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeDeserialize_NonContiguousEntries_RoundTrips()
    {
        // Non-contiguous: gaps between entries
        var entries = new PMTilesEntry[]
        {
            new(TileId: 0, Offset: 0, Length: 100, RunLength: 1),
            new(TileId: 5, Offset: 500, Length: 200, RunLength: 1),
            new(TileId: 10, Offset: 1000, Length: 150, RunLength: 1),
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);
        var result = PMTilesDirectory.DeserializeEntries(bytes);

        result.Should().HaveCount(3);
        for (var i = 0; i < entries.Length; i++)
        {
            result[i].TileId.Should().Be(entries[i].TileId, $"TileId mismatch at index {i}");
            result[i].Offset.Should().Be(entries[i].Offset, $"Offset mismatch at index {i}");
            result[i].Length.Should().Be(entries[i].Length, $"Length mismatch at index {i}");
            result[i].RunLength.Should().Be(entries[i].RunLength, $"RunLength mismatch at index {i}");
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeDeserialize_ContiguousOffsets_EncodedAsZero()
    {
        // Verify that contiguous offsets produce compact encoding (varint 0)
        var entries = new PMTilesEntry[]
        {
            new(TileId: 0, Offset: 0, Length: 50, RunLength: 1),
            new(TileId: 1, Offset: 50, Length: 50, RunLength: 1),
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);

        // The serialized bytes should contain varint 0 for the second offset
        // because offset 50 == previous offset (0) + previous length (50)
        var result = PMTilesDirectory.DeserializeEntries(bytes);
        result[0].Offset.Should().Be(0UL);
        result[1].Offset.Should().Be(50UL);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeDeserialize_LeafEntries_RoundTrips()
    {
        // Leaf entries have RunLength=0
        var entries = new PMTilesEntry[]
        {
            new(TileId: 0, Offset: 0, Length: 512, RunLength: 0),
            new(TileId: 100, Offset: 512, Length: 256, RunLength: 0),
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);
        var result = PMTilesDirectory.DeserializeEntries(bytes);

        result.Should().HaveCount(2);
        result[0].RunLength.Should().Be(0U);
        result[0].IsLeaf.Should().BeTrue();
        result[1].RunLength.Should().Be(0U);
        result[1].IsLeaf.Should().BeTrue();
        result[0].Offset.Should().Be(0UL);
        result[1].Offset.Should().Be(512UL);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeEntries_IncludesNumEntriesPrefix()
    {
        var entries = new PMTilesEntry[]
        {
            new(TileId: 42, Offset: 0, Length: 100, RunLength: 1)
        };

        var bytes = PMTilesDirectory.SerializeEntries(entries);

        // First byte should be the varint-encoded number of entries (1)
        bytes[0].Should().Be(1, "first byte should be num_entries varint for 1 entry");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void SerializeEntries_ManyEntries_NumEntriesPrefixIsCorrect()
    {
        var entries = new PMTilesEntry[200];
        for (var i = 0; i < 200; i++)
        {
            entries[i] = new PMTilesEntry(
                TileId: (ulong)i,
                Offset: (ulong)(i * 100),
                Length: 100,
                RunLength: 1);
        }

        var bytes = PMTilesDirectory.SerializeEntries(entries);
        var result = PMTilesDirectory.DeserializeEntries(bytes);

        result.Should().HaveCount(200);

        // Verify first and last entries
        result[0].TileId.Should().Be(0UL);
        result[199].TileId.Should().Be(199UL);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task WriteAsync_DirectoryEntriesLocateTileData()
    {
        // End-to-end test: write an archive, parse directory, use entries to read tiles
        var tile0 = new byte[] { 1, 2, 3, 4, 5 };
        var tile1 = new byte[] { 10, 20, 30, 40, 50, 60 };
        var tile2 = new byte[] { 100, 200 };

        var writer = new PMTilesWriter(PMTilesCompression.None, PMTilesCompression.None);
        writer.AddTile(0, 0, 0, tile0);
        writer.AddTile(1, 0, 0, tile1);
        writer.AddTile(1, 1, 0, tile2);

        var metadata = new PMTilesArchiveMetadata
        {
            MinLon = -180,
            MinLat = -85,
            MaxLon = 180,
            MaxLat = 85,
            MinZoom = 0,
            MaxZoom = 1
        };

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, metadata);
        var archiveBytes = stream.ToArray();

        // Parse header
        var header = PMTilesHeader.ReadFrom(archiveBytes);

        // Read and decompress root directory
        var rootDirBytes = archiveBytes.AsSpan(
            (int)header.RootDirectoryOffset,
            (int)header.RootDirectoryLength).ToArray();
        var decompressedDir = PMTilesDirectory.Decompress(rootDirBytes, header.InternalCompression);
        var entries = PMTilesDirectory.DeserializeEntries(decompressedDir);

        entries.Should().HaveCount(3);

        // Look up the expected tile by the Hilbert tile id derived from z/x/y, not by payload
        // length: a writer that stored every tile at the wrong coordinate would pass a length
        // lookup as long as the lengths were distinct.
        var expectedByTileId = new Dictionary<ulong, byte[]>
        {
            [HilbertCurve.XYZToTileId(0, 0, 0)] = tile0,
            [HilbertCurve.XYZToTileId(1, 0, 0)] = tile1,
            [HilbertCurve.XYZToTileId(1, 1, 0)] = tile2,
        };

        // Entries are sorted by Hilbert tile ID
        var sortedTileIds = expectedByTileId.Keys.ToArray();
        Array.Sort(sortedTileIds);

        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].TileId.Should().Be(sortedTileIds[i]);

            var tileOffset = (int)(header.TileDataOffset + entries[i].Offset);
            var tileLength = (int)entries[i].Length;
            var tileData = archiveBytes.AsSpan(tileOffset, tileLength).ToArray();

            var expectedTile = expectedByTileId[entries[i].TileId];
            tileData.Should().BeEquivalentTo(expectedTile,
                $"the bytes at tileId={entries[i].TileId} must be that coordinate's tile");
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Decompress_GzipPayloadExceedingMaxDecompressedBytes_ThrowsInvalidDataException()
    {
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'A');
        var compressed = PMTilesDirectory.Compress(payload, PMTilesCompression.Gzip);

        var act = () => PMTilesDirectory.Decompress(compressed, PMTilesCompression.Gzip, maxDecompressedBytes: 1024);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds maximum allowed size*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Decompress_GzipPayloadWithinLimit_RoundTripsSuccessfully()
    {
        var payload = new byte[512];
        Array.Fill(payload, (byte)'B');
        var compressed = PMTilesDirectory.Compress(payload, PMTilesCompression.Gzip);

        var decompressed = PMTilesDirectory.Decompress(compressed, PMTilesCompression.Gzip, maxDecompressedBytes: 1024);

        decompressed.Should().Equal(payload);
    }

    [UnitTest]
    public void DeserializeEntries_EmptyBuffer_ThrowsInvalidDataException()
    {
        var act = () => PMTilesDirectory.DeserializeEntries([]);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_TruncatedVarint_ThrowsInvalidDataException()
    {
        // Continuation bit set on final byte with no following byte
        var act = () => PMTilesDirectory.DeserializeEntries([0x81]);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_TruncatedEntryColumns_ThrowsInvalidDataException()
    {
        // 1 entry requires at least 4 varints, but the final offset byte is truncated mid-varint.
        var act = () => PMTilesDirectory.DeserializeEntries([0x01, 0x00, 0x01, 0x0A, 0x80]);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_VarintExceeding64Bits_ThrowsInvalidDataException()
    {
        // 10-byte varint whose 10th byte sets bits beyond bit 63 (0x02 << 63 overflows ulong).
        byte[] overflowVarint = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];

        var act = () => PMTilesDirectory.DeserializeEntries(overflowVarint);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_EntryCountExceedingPayloadCapacity_ThrowsInvalidDataException()
    {
        // Claims 10 entries, but only 4 bytes follow (can hold at most 1 entry).
        var act = () => PMTilesDirectory.DeserializeEntries([0x0A, 0x00, 0x01, 0x01, 0x01]);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_EntryCountExceedingIntMax_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        PMTilesDirectory.WriteVarint(stream, (ulong)int.MaxValue + 1UL);
        var bytes = stream.ToArray();

        var act = () => PMTilesDirectory.DeserializeEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void DeserializeEntries_FirstEntryZeroOffset_ThrowsInvalidDataException()
    {
        // 1 entry: num_entries=1, tile_id_delta=0, run_length=1, length=10, offset=0 (invalid for i == 0)
        var act = () => PMTilesDirectory.DeserializeEntries([0x01, 0x00, 0x01, 0x0A, 0x00]);

        act.Should().Throw<InvalidDataException>();
    }
}
