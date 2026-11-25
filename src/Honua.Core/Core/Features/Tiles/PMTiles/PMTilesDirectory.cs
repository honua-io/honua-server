// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;

namespace Honua.Core.Features.Tiles.PMTiles;

internal readonly record struct PMTilesEntry(ulong TileId, ulong Offset, uint Length, uint RunLength)
{
    public bool IsLeaf => RunLength == 0;
}

internal static class PMTilesDirectory
{
    // PMTiles v3 requires the header plus the compressed root directory to fit in the first
    // 16 KiB. The header occupies 127 bytes, leaving this many bytes for the root itself.
    private const int MaxRootDirectoryBytes = (16 * 1024) - PMTilesHeader.HeaderSize;
    private const int MaxRootDirectoryEntries = 16384;
    internal const int DefaultMaxDecompressedBytes = 64 * 1024 * 1024;
    private const int MaxDirectoryEntries = 1_000_000;
    private const int MinBytesPerEntry = 4;

    public static (byte[] RootBytes, byte[] LeafBytes) BuildDirectories(
        IReadOnlyList<PMTilesEntry> entries,
        PMTilesCompression internalCompression)
    {
        if (entries.Count <= MaxRootDirectoryEntries)
        {
            var rootBytes = SerializeEntries(entries);
            var compressedRoot = Compress(rootBytes, internalCompression);
            if (compressedRoot.Length <= MaxRootDirectoryBytes)
            {
                return (compressedRoot, []);
            }
        }

        // Split into leaf directories, root directory points to them. The old entry-count limit
        // is only an upper bound: root pointers have variable-length varints and must be sized by
        // their compressed byte count. Doubling the leaf size keeps this bounded to O(log N)
        // directory builds while retaining small leaves for ordinary archives.
        var leafEntriesPerGroup = Math.Max(1, (entries.Count + MaxRootDirectoryEntries - 1) / MaxRootDirectoryEntries);
        while (true)
        {
            var (rootBytes, leafBytes) = BuildLeafDirectories(entries, leafEntriesPerGroup, internalCompression);
            if (rootBytes.Length <= MaxRootDirectoryBytes)
            {
                return (rootBytes, leafBytes);
            }

            if (leafEntriesPerGroup >= entries.Count)
            {
                throw new InvalidOperationException("Unable to fit the PMTiles root directory within the 16 KiB header window.");
            }

            leafEntriesPerGroup = Math.Min(
                entries.Count,
                Math.Max(leafEntriesPerGroup + 1, checked(leafEntriesPerGroup * 2)));
        }
    }

    private static (byte[] RootBytes, byte[] LeafBytes) BuildLeafDirectories(
        IReadOnlyList<PMTilesEntry> entries,
        int leafEntriesPerGroup,
        PMTilesCompression internalCompression)
    {
        var rootEntries = new List<PMTilesEntry>((entries.Count + leafEntriesPerGroup - 1) / leafEntriesPerGroup);
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
                Length: checked((uint)compressedLeaf.Length),
                RunLength: 0));

            leafStream.Write(compressedLeaf);
        }

        var rootBytes = Compress(SerializeEntries(rootEntries), internalCompression);
        return (rootBytes, leafStream.ToArray());
    }

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

    internal static PMTilesEntry[] DeserializeEntries(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var rawNumEntries = ReadVarint(data, ref pos);
        var maxPossibleEntries = (ulong)((data.Length - pos) / MinBytesPerEntry);
        if (rawNumEntries > maxPossibleEntries || rawNumEntries > MaxDirectoryEntries)
        {
            throw new InvalidDataException(
                $"PMTiles directory declares {rawNumEntries} entries, exceeding bounds for a {data.Length}-byte buffer.");
        }

        var numEntries = (int)rawNumEntries;
        if (numEntries == 0)
        {
            return [];
        }

        var entries = new PMTilesEntry[numEntries];
        pos = ReadTileIds(data, pos, entries);
        pos = ReadRunLengths(data, pos, entries);
        pos = ReadLengths(data, pos, entries);
        ReadOffsets(data, pos, entries);
        return entries;
    }

    private static int ReadTileIds(ReadOnlySpan<byte> data, int pos, PMTilesEntry[] entries)
    {
        var lastTileId = 0UL;
        for (var i = 0; i < entries.Length; i++)
        {
            var delta = ReadVarint(data, ref pos);
            if (delta > ulong.MaxValue - lastTileId)
            {
                throw new InvalidDataException("PMTiles tile ID delta overflows 64-bit unsigned integer.");
            }

            lastTileId += delta;
            entries[i] = new PMTilesEntry(lastTileId, 0, 0, 0);
        }

        return pos;
    }

    private static int ReadRunLengths(ReadOnlySpan<byte> data, int pos, PMTilesEntry[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            var runLength = ReadVarint(data, ref pos);
            if (runLength > uint.MaxValue)
            {
                throw new InvalidDataException("PMTiles entry run length exceeds 32-bit unsigned range.");
            }

            entries[i] = entries[i] with { RunLength = (uint)runLength };
        }

        return pos;
    }

    private static int ReadLengths(ReadOnlySpan<byte> data, int pos, PMTilesEntry[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            var length = ReadVarint(data, ref pos);
            if (length > uint.MaxValue)
            {
                throw new InvalidDataException("PMTiles entry length exceeds 32-bit unsigned range.");
            }

            entries[i] = entries[i] with { Length = (uint)length };
        }

        return pos;
    }

    private static void ReadOffsets(ReadOnlySpan<byte> data, int pos, PMTilesEntry[] entries)
    {
        // Column 4: offset decoding per PMTiles v3 spec:
        //   0 (when i > 0) means contiguous (offset = prev.offset + prev.length).
        //   Otherwise offset = v - 1 (absolute).
        for (var i = 0; i < entries.Length; i++)
        {
            var value = ReadVarint(data, ref pos);
            var offset = ResolveEntryOffset(entries, i, value);
            entries[i] = entries[i] with { Offset = offset };
        }
    }

    private static ulong ResolveEntryOffset(PMTilesEntry[] entries, int index, ulong encodedValue)
    {
        if (encodedValue != 0)
        {
            return encodedValue - 1;
        }

        if (index == 0)
        {
            throw new InvalidDataException("PMTiles first directory entry cannot use a relative zero offset.");
        }

        var previous = entries[index - 1];
        if (previous.Offset > ulong.MaxValue - previous.Length)
        {
            throw new InvalidDataException("PMTiles contiguous entry offset overflows 64-bit unsigned integer.");
        }

        return previous.Offset + previous.Length;
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
            if (pos >= data.Length)
            {
                throw new InvalidDataException("Unexpected end of PMTiles varint.");
            }

            var b = data[pos++];
            if (bytesRead == 9 && b > 1)
            {
                throw new InvalidDataException("PMTiles varint overflows 64-bit unsigned integer.");
            }

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

    internal static byte[] Decompress(
        byte[] data,
        PMTilesCompression compression,
        int maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecompressedBytes);

        if (compression == PMTilesCompression.None)
        {
            if (data.Length > maxDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"PMTiles payload length {data.Length} exceeds the {maxDecompressedBytes}-byte limit.");
            }

            return data;
        }

        if (compression == PMTilesCompression.Gzip)
        {
            return DecompressGzipBounded(data, maxDecompressedBytes);
        }

        throw new NotSupportedException($"Compression type {compression} is not supported for directory deserialization.");
    }

    private static byte[] DecompressGzipBounded(byte[] data, int maxDecompressedBytes)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var totalWritten = 0;

        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (read > maxDecompressedBytes - totalWritten)
            {
                throw new InvalidDataException(
                    $"PMTiles gzip payload decompressed beyond the {maxDecompressedBytes}-byte limit.");
            }

            output.Write(buffer, 0, read);
            totalWritten += read;
        }
    }
}
