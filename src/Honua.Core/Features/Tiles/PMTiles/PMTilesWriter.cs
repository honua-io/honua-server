// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// Writes a PMTiles v3 archive from tile data.
/// Tiles are collected in memory, sorted by Hilbert curve tile ID,
/// and written as a complete archive to the output stream.
/// </summary>
public sealed class PMTilesWriter
{
    private readonly List<TileEntry> _tiles = [];
    private readonly PMTilesCompression _tileCompression;
    private readonly PMTilesCompression _internalCompression;

    /// <summary>
    /// Gets the number of tiles added to the writer.
    /// </summary>
    public int TileCount => _tiles.Count;

    /// <summary>
    /// Initializes a new PMTiles writer.
    /// </summary>
    /// <param name="tileCompression">Compression applied to tile data. Use <see cref="PMTilesCompression.None"/> if tiles are already compressed.</param>
    /// <param name="internalCompression">Compression for internal directories. Defaults to Gzip.</param>
    public PMTilesWriter(
        PMTilesCompression tileCompression = PMTilesCompression.None,
        PMTilesCompression internalCompression = PMTilesCompression.Gzip)
    {
        _tileCompression = tileCompression;
        _internalCompression = internalCompression;
    }

    /// <summary>
    /// Adds a tile to the archive.
    /// </summary>
    /// <param name="z">Zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <param name="data">Raw tile data bytes.</param>
    public void AddTile(int z, int x, int y, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return;
        }

        var tileId = HilbertCurve.XYZToTileId(z, x, y);
        _tiles.Add(new TileEntry(tileId, data));
    }

    /// <summary>
    /// Writes the complete PMTiles v3 archive to the output stream.
    /// </summary>
    /// <param name="output">The stream to write the archive to.</param>
    /// <param name="metadata">Archive metadata (bounds, zoom, attribution).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of bytes written.</returns>
    public async Task<long> WriteAsync(Stream output, PMTilesArchiveMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);

        // Sort tiles by Hilbert curve tile ID
        _tiles.Sort(static (a, b) => a.TileId.CompareTo(b.TileId));

        // Phase 1: Write tile data to a buffer and build directory entries
        using var tileDataStream = new MemoryStream();
        var entries = new List<PMTilesEntry>(_tiles.Count);
        var uniqueContents = new HashSet<int>();

        foreach (var tile in _tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tileBytes = _tileCompression != PMTilesCompression.None
                ? PMTilesDirectory.Compress(tile.Data, _tileCompression)
                : tile.Data;

            var offset = (ulong)tileDataStream.Position;
            tileDataStream.Write(tileBytes);
            uniqueContents.Add(ContentHash(tile.Data));

            entries.Add(new PMTilesEntry(
                TileId: tile.TileId,
                Offset: offset,
                Length: (uint)tileBytes.Length,
                RunLength: 1));
        }

        // Phase 2: Build JSON metadata
        var jsonMetadata = BuildJsonMetadata(metadata);
        var compressedMetadata = PMTilesDirectory.Compress(jsonMetadata, _internalCompression);

        // Phase 3: Build directories
        var (rootDirBytes, leafDirBytes) = PMTilesDirectory.BuildDirectories(entries, _internalCompression);

        // Phase 4: Calculate offsets
        // Layout: [header 127] [root_dir] [json_metadata] [leaf_dirs] [tile_data]
        var rootDirOffset = (ulong)PMTilesHeader.HeaderSize;
        var rootDirLength = (ulong)rootDirBytes.Length;

        var jsonMetadataOffset = rootDirOffset + rootDirLength;
        var jsonMetadataLength = (ulong)compressedMetadata.Length;

        var leafDirOffset = jsonMetadataOffset + jsonMetadataLength;
        var leafDirLength = (ulong)leafDirBytes.Length;

        var tileDataOffset = leafDirOffset + leafDirLength;
        var tileDataLength = (ulong)tileDataStream.Length;

        // Phase 5: Build header
        var header = new PMTilesHeader
        {
            RootDirectoryOffset = rootDirOffset,
            RootDirectoryLength = rootDirLength,
            JsonMetadataOffset = jsonMetadataOffset,
            JsonMetadataLength = jsonMetadataLength,
            LeafDirectoryOffset = leafDirOffset,
            LeafDirectoryLength = leafDirLength,
            TileDataOffset = tileDataOffset,
            TileDataLength = tileDataLength,
            AddressedTilesCount = (ulong)_tiles.Count,
            TileEntriesCount = (ulong)entries.Count,
            TileContentsCount = (ulong)uniqueContents.Count,
            Clustered = true,
            InternalCompression = _internalCompression,
            TileCompression = _tileCompression,
            TileType = PMTilesTileType.Mvt,
            MinZoom = (byte)metadata.MinZoom,
            MaxZoom = (byte)metadata.MaxZoom,
            MinLonE7 = ToE7(metadata.MinLon),
            MinLatE7 = ToE7(metadata.MinLat),
            MaxLonE7 = ToE7(metadata.MaxLon),
            MaxLatE7 = ToE7(metadata.MaxLat),
            CenterZoom = (byte)(metadata.CenterZoom ?? (metadata.MinZoom + metadata.MaxZoom) / 2),
            CenterLonE7 = ToE7(metadata.CenterLon ?? (metadata.MinLon + metadata.MaxLon) / 2.0),
            CenterLatE7 = ToE7(metadata.CenterLat ?? (metadata.MinLat + metadata.MaxLat) / 2.0),
        };

        // Phase 6: Write everything
        Span<byte> headerBytes = stackalloc byte[PMTilesHeader.HeaderSize];
        header.WriteTo(headerBytes);

        await output.WriteAsync(headerBytes.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(rootDirBytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(compressedMetadata, cancellationToken).ConfigureAwait(false);
        if (leafDirBytes.Length > 0)
        {
            await output.WriteAsync(leafDirBytes, cancellationToken).ConfigureAwait(false);
        }
        tileDataStream.Position = 0;
        await tileDataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return (long)(tileDataOffset + tileDataLength);
    }

    private static byte[] BuildJsonMetadata(PMTilesArchiveMetadata metadata)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        if (metadata.Attribution is not null)
        {
            writer.WriteString("attribution", metadata.Attribution);
        }

        if (metadata.Description is not null)
        {
            writer.WriteString("description", metadata.Description);
        }

        writer.WriteString("type", "overlay");
        writer.WriteString("format", "pbf");

        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    private static int ToE7(double value) => (int)(value * 10_000_000);

    private static int ContentHash(byte[] data)
    {
        var hash = new HashCode();
        hash.AddBytes(data);
        return hash.ToHashCode();
    }

    private readonly record struct TileEntry(ulong TileId, byte[] Data);
}
