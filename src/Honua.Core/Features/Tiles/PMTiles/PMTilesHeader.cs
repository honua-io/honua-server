// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// PMTiles v3 archive header (127 bytes).
/// See: https://github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md
/// </summary>
internal struct PMTilesHeader
{
    public const int HeaderSize = 127;
    public const byte SpecVersion = 3;
    public static readonly byte[] MagicBytes = "PMTiles"u8.ToArray();

    public ulong RootDirectoryOffset { get; set; }
    public ulong RootDirectoryLength { get; set; }
    public ulong JsonMetadataOffset { get; set; }
    public ulong JsonMetadataLength { get; set; }
    public ulong LeafDirectoryOffset { get; set; }
    public ulong LeafDirectoryLength { get; set; }
    public ulong TileDataOffset { get; set; }
    public ulong TileDataLength { get; set; }
    public ulong AddressedTilesCount { get; set; }
    public ulong TileEntriesCount { get; set; }
    public ulong TileContentsCount { get; set; }
    public bool Clustered { get; set; }
    public PMTilesCompression InternalCompression { get; set; }
    public PMTilesCompression TileCompression { get; set; }
    public PMTilesTileType TileType { get; set; }
    public byte MinZoom { get; set; }
    public byte MaxZoom { get; set; }
    public int MinLonE7 { get; set; }
    public int MinLatE7 { get; set; }
    public int MaxLonE7 { get; set; }
    public int MaxLatE7 { get; set; }
    public byte CenterZoom { get; set; }
    public int CenterLonE7 { get; set; }
    public int CenterLatE7 { get; set; }

    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes.", nameof(buffer));
        }

        buffer.Clear();

        // Magic number (7 bytes)
        MagicBytes.CopyTo(buffer);

        // Version (1 byte)
        buffer[7] = SpecVersion;

        // Root directory offset and length (8+8 bytes)
        BitConverter.TryWriteBytes(buffer[8..], RootDirectoryOffset);
        BitConverter.TryWriteBytes(buffer[16..], RootDirectoryLength);

        // JSON metadata offset and length (8+8 bytes)
        BitConverter.TryWriteBytes(buffer[24..], JsonMetadataOffset);
        BitConverter.TryWriteBytes(buffer[32..], JsonMetadataLength);

        // Leaf directories offset and length (8+8 bytes)
        BitConverter.TryWriteBytes(buffer[40..], LeafDirectoryOffset);
        BitConverter.TryWriteBytes(buffer[48..], LeafDirectoryLength);

        // Tile data offset and length (8+8 bytes)
        BitConverter.TryWriteBytes(buffer[56..], TileDataOffset);
        BitConverter.TryWriteBytes(buffer[64..], TileDataLength);

        // Addressed tiles count (8 bytes)
        BitConverter.TryWriteBytes(buffer[72..], AddressedTilesCount);

        // Tile entries count (8 bytes)
        BitConverter.TryWriteBytes(buffer[80..], TileEntriesCount);

        // Tile contents count (8 bytes)
        BitConverter.TryWriteBytes(buffer[88..], TileContentsCount);

        // Clustered (1 byte)
        buffer[96] = Clustered ? (byte)1 : (byte)0;

        // Internal compression (1 byte)
        buffer[97] = (byte)InternalCompression;

        // Tile compression (1 byte)
        buffer[98] = (byte)TileCompression;

        // Tile type (1 byte)
        buffer[99] = (byte)TileType;

        // Min zoom (1 byte)
        buffer[100] = MinZoom;

        // Max zoom (1 byte)
        buffer[101] = MaxZoom;

        // Min position (4+4 bytes, E7 encoding)
        BitConverter.TryWriteBytes(buffer[102..], MinLonE7);
        BitConverter.TryWriteBytes(buffer[106..], MinLatE7);

        // Max position (4+4 bytes)
        BitConverter.TryWriteBytes(buffer[110..], MaxLonE7);
        BitConverter.TryWriteBytes(buffer[114..], MaxLatE7);

        // Center zoom (1 byte)
        buffer[118] = CenterZoom;

        // Center position (4+4 bytes)
        BitConverter.TryWriteBytes(buffer[119..], CenterLonE7);
        BitConverter.TryWriteBytes(buffer[123..], CenterLatE7);
    }

    public static PMTilesHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException($"Buffer must be at least {HeaderSize} bytes.", nameof(buffer));
        }

        // Validate magic
        if (!buffer[..7].SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid PMTiles magic bytes.");
        }

        if (buffer[7] != SpecVersion)
        {
            throw new InvalidDataException($"Unsupported PMTiles version: {buffer[7]}.");
        }

        return new PMTilesHeader
        {
            RootDirectoryOffset = BitConverter.ToUInt64(buffer[8..]),
            RootDirectoryLength = BitConverter.ToUInt64(buffer[16..]),
            JsonMetadataOffset = BitConverter.ToUInt64(buffer[24..]),
            JsonMetadataLength = BitConverter.ToUInt64(buffer[32..]),
            LeafDirectoryOffset = BitConverter.ToUInt64(buffer[40..]),
            LeafDirectoryLength = BitConverter.ToUInt64(buffer[48..]),
            TileDataOffset = BitConverter.ToUInt64(buffer[56..]),
            TileDataLength = BitConverter.ToUInt64(buffer[64..]),
            AddressedTilesCount = BitConverter.ToUInt64(buffer[72..]),
            TileEntriesCount = BitConverter.ToUInt64(buffer[80..]),
            TileContentsCount = BitConverter.ToUInt64(buffer[88..]),
            Clustered = buffer[96] == 1,
            InternalCompression = (PMTilesCompression)buffer[97],
            TileCompression = (PMTilesCompression)buffer[98],
            TileType = (PMTilesTileType)buffer[99],
            MinZoom = buffer[100],
            MaxZoom = buffer[101],
            MinLonE7 = BitConverter.ToInt32(buffer[102..]),
            MinLatE7 = BitConverter.ToInt32(buffer[106..]),
            MaxLonE7 = BitConverter.ToInt32(buffer[110..]),
            MaxLatE7 = BitConverter.ToInt32(buffer[114..]),
            CenterZoom = buffer[118],
            CenterLonE7 = BitConverter.ToInt32(buffer[119..]),
            CenterLatE7 = BitConverter.ToInt32(buffer[123..]),
        };
    }
}

/// <summary>
/// PMTiles v3 compression types.
/// </summary>
public enum PMTilesCompression : byte
{
    /// <summary>Unknown compression.</summary>
    Unknown = 0,
    /// <summary>No compression.</summary>
    None = 1,
    /// <summary>Gzip compression.</summary>
    Gzip = 2,
    /// <summary>Brotli compression.</summary>
    Brotli = 3,
    /// <summary>Zstd compression.</summary>
    Zstd = 4
}

/// <summary>
/// PMTiles v3 tile content types.
/// </summary>
public enum PMTilesTileType : byte
{
    /// <summary>Unknown tile type.</summary>
    Unknown = 0,
    /// <summary>Mapbox Vector Tile (MVT).</summary>
    Mvt = 1,
    /// <summary>PNG image.</summary>
    Png = 2,
    /// <summary>JPEG image.</summary>
    Jpeg = 3,
    /// <summary>WebP image.</summary>
    Webp = 4,
    /// <summary>AVIF image.</summary>
    Avif = 5
}
