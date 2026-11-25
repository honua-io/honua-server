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
    private const int MaxDirectoryEntries = 1_000_000;

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
        var decoder = new DirectoryDecoder(data);
        var numEntries = decoder.ReadEntryCount();
        if (numEntries == 0)
        {
            return [];
        }

        var entries = new PMTilesEntry[numEntries];
        decoder.ReadTileIds(entries);
        decoder.ReadRunLengths(entries);
        decoder.ReadLengths(entries);
        decoder.ReadOffsets(entries);
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

    private ref struct DirectoryDecoder
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public DirectoryDecoder(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public int ReadEntryCount()
        {
            var rawCount = ReadVarint();
            var maxEntriesFromPayload = (ulong)(_data.Length - _pos) / 4UL;
            if (rawCount > int.MaxValue || rawCount > MaxDirectoryEntries || rawCount > maxEntriesFromPayload)
            {
                throw new InvalidDataException($"PMTiles directory entry count {rawCount} exceeds allowed bounds.");
            }

            return (int)rawCount;
        }

        public void ReadTileIds(PMTilesEntry[] entries)
        {
            var lastTileId = 0UL;
            for (var i = 0; i < entries.Length; i++)
            {
                var delta = ReadVarint();
                if (delta > ulong.MaxValue - lastTileId)
                {
                    throw new InvalidDataException("PMTiles tile ID delta overflows 64-bit unsigned integer.");
                }

                lastTileId += delta;
                entries[i] = new PMTilesEntry(lastTileId, 0, 0, 0);
            }
        }

        public void ReadRunLengths(PMTilesEntry[] entries)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var runLength = ReadUInt32("PMTiles directory entry run length exceeds 32-bit unsigned range.");
                entries[i] = entries[i] with { RunLength = runLength };
            }
        }

        public void ReadLengths(PMTilesEntry[] entries)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var length = ReadUInt32("PMTiles directory entry length exceeds 32-bit unsigned range.");
                entries[i] = entries[i] with { Length = length };
            }
        }

        public void ReadOffsets(PMTilesEntry[] entries)
        {
            // Offset encoding per PMTiles v3 spec: 0 (when i > 0) means contiguous; otherwise v - 1.
            for (var i = 0; i < entries.Length; i++)
            {
                var v = ReadVarint();
                var offset = ResolveEntryOffset(entries, i, v);
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
                throw new InvalidDataException("First PMTiles directory entry cannot use contiguous offset encoding.");
            }

            var previous = entries[index - 1];
            if (previous.Offset > ulong.MaxValue - previous.Length)
            {
                throw new InvalidDataException("PMTiles contiguous entry offset overflows 64-bit unsigned integer.");
            }

            return previous.Offset + previous.Length;
        }

        private uint ReadUInt32(string overflowMessage)
        {
            var rawValue = ReadVarint();
            if (rawValue > uint.MaxValue)
            {
                throw new InvalidDataException(overflowMessage);
            }

            return (uint)rawValue;
        }

        private ulong ReadVarint()
        {
            var result = 0UL;
            var shift = 0;
            while (shift < 64)
            {
                if (_pos >= _data.Length)
                {
                    throw new InvalidDataException("Unexpected end of PMTiles directory while reading varint.");
                }

                var b = _data[_pos++];
                if (shift == 63 && (b & 0x7E) != 0)
                {
                    throw new InvalidDataException("Varint exceeds 64-bit unsigned integer range.");
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

    internal const int DefaultMaxDecompressedDirectoryBytes = 4 * 1024 * 1024;

    internal static byte[] Decompress(
        byte[] data,
        PMTilesCompression compression,
        int maxDecompressedBytes = DefaultMaxDecompressedDirectoryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDecompressedBytes, 1);

        if (compression == PMTilesCompression.None)
        {
            if (data.Length > maxDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"PMTiles directory exceeds maximum allowed size of {maxDecompressedBytes} bytes.");
            }

            return data;
        }

        if (compression == PMTilesCompression.Gzip)
        {
            return DecompressGzip(data, maxDecompressedBytes);
        }

        throw new NotSupportedException($"Compression type {compression} is not supported for directory deserialization.");
    }

    private static byte[] DecompressGzip(byte[] data, int maxDecompressedBytes)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var totalBytes = 0;
        int bytesRead;
        while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytesRead > maxDecompressedBytes - totalBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed PMTiles directory exceeds maximum allowed size of {maxDecompressedBytes} bytes.");
            }

            totalBytes += bytesRead;
            output.Write(buffer, 0, bytesRead);
        }

        return output.ToArray();
    }
}
