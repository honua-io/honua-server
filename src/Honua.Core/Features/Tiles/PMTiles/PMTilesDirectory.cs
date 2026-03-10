// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// A single entry in a PMTiles v3 directory.
/// </summary>
internal readonly record struct PMTilesEntry(ulong TileId, ulong Offset, uint Length, uint RunLength)
{
    /// <summary>Whether this entry points to a leaf directory rather than tile data.</summary>
    public bool IsLeaf => RunLength == 0;
}

/// <summary>
/// Serializes and deserializes PMTiles v3 directories using varint encoding.
/// </summary>
internal static class PMTilesDirectory
{
    private const int MaxRootDirectoryEntries = 16384;

    /// <summary>
    /// Builds root and leaf directory bytes from a sorted list of entries.
    /// If entries fit within the root directory, leafBytes will be empty.
    /// </summary>
    public static (byte[] RootBytes, byte[] LeafBytes) BuildDirectories(
        IReadOnlyList<PMTilesEntry> entries,
        PMTilesCompression internalCompression)
    {
        if (entries.Count <= MaxRootDirectoryEntries)
        {
            var rootBytes = SerializeEntries(entries);
            var compressedRoot = Compress(rootBytes, internalCompression);
            return (compressedRoot, []);
        }

        // Split into leaf directories, root directory points to them
        var leafEntriesPerGroup = Math.Max(1, (entries.Count + MaxRootDirectoryEntries - 1) / MaxRootDirectoryEntries);
        var rootEntries = new List<PMTilesEntry>();
        using var leafStream = new MemoryStream();

        for (var i = 0; i < entries.Count; i += leafEntriesPerGroup)
        {
            var count = Math.Min(leafEntriesPerGroup, entries.Count - i);
            var group = new List<PMTilesEntry>(count);
            for (var j = 0; j < count; j++)
            {
                group.Add(entries[i + j]);
            }

            var leafBytes = SerializeEntries(group);
            var compressedLeaf = Compress(leafBytes, internalCompression);

            rootEntries.Add(new PMTilesEntry(
                TileId: entries[i].TileId,
                Offset: (ulong)leafStream.Position,
                Length: (uint)compressedLeaf.Length,
                RunLength: 0 // RunLength=0 means this is a leaf directory pointer
            ));

            leafStream.Write(compressedLeaf);
        }

        var rootBytes2 = SerializeEntries(rootEntries);
        var compressedRoot2 = Compress(rootBytes2, internalCompression);
        return (compressedRoot2, leafStream.ToArray());
    }

    /// <summary>
    /// Serializes directory entries to bytes using varint encoding per PMTiles v3 spec.
    /// Layout: num_entries varint, then four columns: tile_ids, run_lengths, lengths, offsets.
    /// </summary>
    internal static byte[] SerializeEntries(IReadOnlyList<PMTilesEntry> entries)
    {
        using var stream = new MemoryStream();

        // num_entries prefix (required by spec)
        WriteVarint(stream, (ulong)entries.Count);

        // Column 1: tile_id deltas
        var lastTileId = 0UL;
        foreach (var entry in entries)
        {
            WriteVarint(stream, entry.TileId - lastTileId);
            lastTileId = entry.TileId;
        }

        // Column 2: run_length
        foreach (var entry in entries)
        {
            WriteVarint(stream, entry.RunLength);
        }

        // Column 3: length
        foreach (var entry in entries)
        {
            WriteVarint(stream, entry.Length);
        }

        // Column 4: offset encoding per PMTiles v3 spec:
        //   First entry always writes offset + 1.
        //   Subsequent entries: if contiguous (offset == prev.offset + prev.length), write 0.
        //   Otherwise write offset + 1 (absolute, not delta).
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i > 0 && entry.Offset == entries[i - 1].Offset + entries[i - 1].Length)
            {
                WriteVarint(stream, 0);
            }
            else
            {
                WriteVarint(stream, entry.Offset + 1);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Deserializes directory entries from varint-encoded column data.
    /// Reads the num_entries prefix, then four columns.
    /// </summary>
    internal static PMTilesEntry[] DeserializeEntries(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var numEntries = (int)ReadVarint(data, ref pos);
        var entries = new PMTilesEntry[numEntries];

        // Column 1: tile_id deltas
        var lastTileId = 0UL;
        for (var i = 0; i < numEntries; i++)
        {
            var delta = ReadVarint(data, ref pos);
            lastTileId += delta;
            entries[i] = new PMTilesEntry(lastTileId, 0, 0, 0);
        }

        // Column 2: run_length
        for (var i = 0; i < numEntries; i++)
        {
            var runLength = (uint)ReadVarint(data, ref pos);
            entries[i] = entries[i] with { RunLength = runLength };
        }

        // Column 3: length
        for (var i = 0; i < numEntries; i++)
        {
            var length = (uint)ReadVarint(data, ref pos);
            entries[i] = entries[i] with { Length = length };
        }

        // Column 4: offset decoding per PMTiles v3 spec:
        //   0 (when i > 0) means contiguous (offset = prev.offset + prev.length).
        //   Otherwise offset = v - 1 (absolute).
        for (var i = 0; i < numEntries; i++)
        {
            var v = ReadVarint(data, ref pos);
            ulong offset;
            if (v == 0 && i > 0)
            {
                offset = entries[i - 1].Offset + entries[i - 1].Length;
            }
            else
            {
                offset = v - 1;
            }
            entries[i] = entries[i] with { Offset = offset };
        }

        return entries;
    }

    internal static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        var result = 0UL;
        var shift = 0;
        for (var bytesRead = 0; bytesRead < 10; bytesRead++)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
        throw new InvalidDataException("Varint exceeds 10 bytes.");
    }

    internal static byte[] Compress(byte[] data, PMTilesCompression compression)
    {
        if (compression == PMTilesCompression.None)
        {
            return data;
        }

        if (compression == PMTilesCompression.Gzip)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(data);
            }
            return output.ToArray();
        }

        throw new NotSupportedException($"Compression type {compression} is not supported for directory serialization.");
    }

    internal static byte[] Decompress(byte[] data, PMTilesCompression compression)
    {
        if (compression == PMTilesCompression.None)
        {
            return data;
        }

        if (compression == PMTilesCompression.Gzip)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        throw new NotSupportedException($"Compression type {compression} is not supported for directory deserialization.");
    }
}
