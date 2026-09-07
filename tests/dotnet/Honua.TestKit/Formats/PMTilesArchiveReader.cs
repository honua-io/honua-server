// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Honua.TestKit.Formats;

/// <summary>
/// A dependency-free reader for PMTiles v3 archives
/// (<see href="https://github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md"/>), written so tests
/// can extract the individual tile blobs a PMTiles client would fetch (honua-server#4421).
/// </summary>
/// <remarks>
/// Reading the tile-data section as one buffer is not enough to prove an archive is usable:
/// concatenated protobuf messages decode as a single merged message, so a directory entry with a
/// wrong offset or length still yields a section that parses while the tile a client actually
/// slices out does not. This reader walks the root and leaf directories and hands back one blob
/// per declared entry, which is the unit a client addresses.
/// <para>
/// Like <see cref="MvtTileDecoder"/> this deliberately re-implements the format rather than
/// calling the writer's own serializer: a test asset that shares the producer's code proves only
/// that the producer agrees with itself.
/// </para>
/// </remarks>
public static class PMTilesArchiveReader
{
    /// <summary>The fixed PMTiles v3 header size, in bytes.</summary>
    public const int HeaderSize = 127;

    /// <summary>Reads the archive header.</summary>
    /// <exception cref="InvalidDataException">The buffer is not a PMTiles v3 archive.</exception>
    public static PMTilesArchiveHeader ReadHeader(ReadOnlySpan<byte> archive)
    {
        if (archive.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"PMTiles archive is {archive.Length} bytes, shorter than the {HeaderSize}-byte header.");
        }

        if (!archive[..7].SequenceEqual("PMTiles"u8))
        {
            throw new InvalidDataException("PMTiles archive does not start with the PMTiles magic bytes.");
        }

        if (archive[7] != 3)
        {
            throw new InvalidDataException($"PMTiles archive declares version {archive[7]}, expected 3.");
        }

        return new PMTilesArchiveHeader(
            RootDirectoryOffset: BinaryPrimitives.ReadUInt64LittleEndian(archive[8..]),
            RootDirectoryLength: BinaryPrimitives.ReadUInt64LittleEndian(archive[16..]),
            JsonMetadataOffset: BinaryPrimitives.ReadUInt64LittleEndian(archive[24..]),
            JsonMetadataLength: BinaryPrimitives.ReadUInt64LittleEndian(archive[32..]),
            LeafDirectoryOffset: BinaryPrimitives.ReadUInt64LittleEndian(archive[40..]),
            LeafDirectoryLength: BinaryPrimitives.ReadUInt64LittleEndian(archive[48..]),
            TileDataOffset: BinaryPrimitives.ReadUInt64LittleEndian(archive[56..]),
            TileDataLength: BinaryPrimitives.ReadUInt64LittleEndian(archive[64..]),
            AddressedTilesCount: BinaryPrimitives.ReadUInt64LittleEndian(archive[72..]),
            TileEntriesCount: BinaryPrimitives.ReadUInt64LittleEndian(archive[80..]),
            TileContentsCount: BinaryPrimitives.ReadUInt64LittleEndian(archive[88..]),
            Clustered: archive[96] == 1,
            InternalCompression: archive[97],
            TileCompression: archive[98],
            TileType: archive[99],
            MinZoom: archive[100],
            MaxZoom: archive[101]);
    }

    /// <summary>
    /// Returns one blob per directory entry, sliced at the offset and length the directory
    /// declares and decompressed with the archive's tile compression.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The directories are malformed, or an entry addresses bytes outside the tile-data section.
    /// </exception>
    public static IReadOnlyList<PMTilesArchiveTile> ReadTiles(ReadOnlySpan<byte> archive)
    {
        var header = ReadHeader(archive);
        var tileData = Slice(archive, header.TileDataOffset, header.TileDataLength, "tile data");

        var rootEntries = DeserializeEntries(Decompress(
            Slice(archive, header.RootDirectoryOffset, header.RootDirectoryLength, "root directory"),
            header.InternalCompression));

        var tiles = new List<PMTilesArchiveTile>();
        foreach (var entry in rootEntries)
        {
            if (entry.RunLength == 0)
            {
                // A leaf pointer: its offset/length address the leaf-directory section.
                var leafSection = Slice(
                    archive, header.LeafDirectoryOffset, header.LeafDirectoryLength, "leaf directory");
                var leafBytes = SliceWithin(leafSection, entry.Offset, entry.Length, "leaf directory entry");
                foreach (var leafEntry in DeserializeEntries(Decompress(leafBytes, header.InternalCompression)))
                {
                    if (leafEntry.RunLength == 0)
                    {
                        throw new InvalidDataException(
                            "PMTiles leaf directory contains a nested leaf pointer, which the spec does not allow.");
                    }

                    tiles.Add(ReadTile(tileData, leafEntry, header.TileCompression));
                }
            }
            else
            {
                tiles.Add(ReadTile(tileData, entry, header.TileCompression));
            }
        }

        return tiles;
    }

    private static PMTilesArchiveTile ReadTile(
        ReadOnlySpan<byte> tileData, PMTilesDirectoryEntry entry, byte tileCompression)
    {
        var blob = SliceWithin(tileData, entry.Offset, entry.Length, $"tile entry {entry.TileId}");
        return new PMTilesArchiveTile(entry.TileId, entry.RunLength, Decompress(blob, tileCompression));
    }

    private static byte[] Slice(ReadOnlySpan<byte> archive, ulong offset, ulong length, string what)
    {
        if (offset + length > (ulong)archive.Length)
        {
            throw new InvalidDataException(
                $"PMTiles {what} section ({offset}+{length}) runs past the {archive.Length}-byte archive.");
        }

        return archive.Slice((int)offset, (int)length).ToArray();
    }

    private static byte[] SliceWithin(ReadOnlySpan<byte> section, ulong offset, uint length, string what)
    {
        if (offset + length > (ulong)section.Length)
        {
            throw new InvalidDataException(
                $"PMTiles {what} addresses bytes {offset}..{offset + length} outside its {section.Length}-byte section.");
        }

        if (length == 0)
        {
            throw new InvalidDataException($"PMTiles {what} declares a zero-length blob.");
        }

        return section.Slice((int)offset, (int)length).ToArray();
    }

    private static byte[] Decompress(byte[] payload, byte compression)
        => compression switch
        {
            1 => payload,
            2 => GunzipCore(payload),
            _ => throw new InvalidDataException(
                $"PMTiles compression code {compression} is not supported by this reader."),
        };

    private static byte[] GunzipCore(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Deserializes a PMTiles v3 directory: a <c>num_entries</c> varint followed by four varint
    /// columns (tile-id deltas, run lengths, lengths, offsets).
    /// </summary>
    private static List<PMTilesDirectoryEntry> DeserializeEntries(byte[] directory)
    {
        var position = 0;
        var count = (int)ReadVarint(directory, ref position);
        if (count < 0)
        {
            throw new InvalidDataException("PMTiles directory declares a negative entry count.");
        }

        var tileIds = new ulong[count];
        var runLengths = new uint[count];
        var lengths = new uint[count];
        var offsets = new ulong[count];

        var lastTileId = 0UL;
        for (var i = 0; i < count; i++)
        {
            lastTileId += ReadVarint(directory, ref position);
            tileIds[i] = lastTileId;
        }

        for (var i = 0; i < count; i++)
        {
            runLengths[i] = (uint)ReadVarint(directory, ref position);
        }

        for (var i = 0; i < count; i++)
        {
            lengths[i] = (uint)ReadVarint(directory, ref position);
        }

        for (var i = 0; i < count; i++)
        {
            var encoded = ReadVarint(directory, ref position);
            offsets[i] = encoded == 0 && i > 0
                ? offsets[i - 1] + lengths[i - 1]
                : encoded - 1;
        }

        if (position != directory.Length)
        {
            throw new InvalidDataException(
                $"PMTiles directory has {directory.Length - position} trailing bytes after {count} entries.");
        }

        var entries = new List<PMTilesDirectoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(new PMTilesDirectoryEntry(tileIds[i], offsets[i], lengths[i], runLengths[i]));
        }

        return entries;
    }

    private static ulong ReadVarint(byte[] data, ref int position)
    {
        var result = 0UL;
        var shift = 0;
        while (true)
        {
            if (position >= data.Length)
            {
                throw new InvalidDataException("PMTiles varint runs past the end of the directory.");
            }

            var current = data[position++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("PMTiles varint is longer than 10 bytes.");
            }
        }
    }

    private readonly record struct PMTilesDirectoryEntry(ulong TileId, ulong Offset, uint Length, uint RunLength);
}

/// <summary>The fields of a PMTiles v3 header this reader exposes.</summary>
/// <param name="RootDirectoryOffset">Byte offset of the root directory.</param>
/// <param name="RootDirectoryLength">Byte length of the root directory.</param>
/// <param name="JsonMetadataOffset">Byte offset of the JSON metadata section.</param>
/// <param name="JsonMetadataLength">Byte length of the JSON metadata section.</param>
/// <param name="LeafDirectoryOffset">Byte offset of the leaf-directory section.</param>
/// <param name="LeafDirectoryLength">Byte length of the leaf-directory section.</param>
/// <param name="TileDataOffset">Byte offset of the tile-data section.</param>
/// <param name="TileDataLength">Byte length of the tile-data section.</param>
/// <param name="AddressedTilesCount">Number of addressed tiles.</param>
/// <param name="TileEntriesCount">Number of directory entries.</param>
/// <param name="TileContentsCount">Number of distinct tile blobs.</param>
/// <param name="Clustered">Whether tile data is ordered by tile id.</param>
/// <param name="InternalCompression">Compression code for directories and metadata.</param>
/// <param name="TileCompression">Compression code for tile blobs.</param>
/// <param name="TileType">Tile content type code (1 = MVT).</param>
/// <param name="MinZoom">Minimum zoom level.</param>
/// <param name="MaxZoom">Maximum zoom level.</param>
public sealed record PMTilesArchiveHeader(
    ulong RootDirectoryOffset,
    ulong RootDirectoryLength,
    ulong JsonMetadataOffset,
    ulong JsonMetadataLength,
    ulong LeafDirectoryOffset,
    ulong LeafDirectoryLength,
    ulong TileDataOffset,
    ulong TileDataLength,
    ulong AddressedTilesCount,
    ulong TileEntriesCount,
    ulong TileContentsCount,
    bool Clustered,
    byte InternalCompression,
    byte TileCompression,
    byte TileType,
    byte MinZoom,
    byte MaxZoom);

/// <summary>One tile blob as a PMTiles client would slice it out of the archive.</summary>
/// <param name="TileId">The Hilbert-curve tile id the directory entry addresses.</param>
/// <param name="RunLength">How many consecutive tile ids share this blob.</param>
/// <param name="Data">The decompressed tile payload.</param>
public sealed record PMTilesArchiveTile(ulong TileId, uint RunLength, byte[] Data);
