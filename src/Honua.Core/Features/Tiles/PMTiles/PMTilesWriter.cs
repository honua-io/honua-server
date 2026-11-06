// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Tiles.PMTiles;

public sealed class PMTilesWriter
{
    private readonly List<TileEntry> _tiles = [];
    private readonly PMTilesCompression _tileCompression;
    private readonly PMTilesCompression _internalCompression;

    public int TileCount => _tiles.Count;

    public PMTilesWriter(
        PMTilesCompression tileCompression = PMTilesCompression.None,
        PMTilesCompression internalCompression = PMTilesCompression.Gzip)
    {
        _tileCompression = tileCompression;
        _internalCompression = internalCompression;
    }

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

    // WriteAsync consumes each tile buffer, so a writer can be written only once.
    public async Task<long> WriteAsync(Stream output, PMTilesArchiveMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);

        // Sort tiles by Hilbert curve tile ID
        _tiles.Sort(static (a, b) => a.TileId.CompareTo(b.TileId));

        // Phase 1: Calculate tile offsets and lengths without materialising the tile section.
        // Compressed tiles are encoded once here to determine their lengths and once while
        // streaming them below. This keeps peak memory proportional to the largest tile rather
        // than the complete archive and avoids MemoryStream's Int32 capacity ceiling.
        var entries = new List<PMTilesEntry>(_tiles.Count);
        ulong tileDataLength = 0;

        for (var i = 0; i < _tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tile = _tiles[i];
            var tileBytes = GetTileBytes(tile.Data);
            var offset = tileDataLength;
            tileDataLength = checked(tileDataLength + (ulong)tileBytes.Length);

            entries.Add(new PMTilesEntry(
                TileId: tile.TileId,
                Offset: offset,
                Length: checked((uint)tileBytes.Length),
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
            // Every directory entry writes one blob. Identical payloads are not deduplicated by
            // this writer, so this is the number of blobs in the tile section, not a hash count.
            TileContentsCount = (ulong)entries.Count,
            Clustered = true,
            InternalCompression = _internalCompression,
            TileCompression = _tileCompression,
            TileType = PMTilesTileType.Mvt,
            MinZoom = (byte)metadata.MinZoom,
            MaxZoom = (byte)metadata.MaxZoom,
            MinLonE7 = ToE7Lon(metadata.MinLon),
            MinLatE7 = ToE7Lat(metadata.MinLat),
            MaxLonE7 = ToE7Lon(metadata.MaxLon),
            MaxLatE7 = ToE7Lat(metadata.MaxLat),
            CenterZoom = (byte)(metadata.CenterZoom ?? (metadata.MinZoom + metadata.MaxZoom) / 2),
            CenterLonE7 = ToE7Lon(metadata.CenterLon ?? (metadata.MinLon + metadata.MaxLon) / 2.0),
            CenterLatE7 = ToE7Lat(metadata.CenterLat ?? (metadata.MinLat + metadata.MaxLat) / 2.0),
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
        // Phase 6: stream tile blobs directly to the caller. Do not stage the complete tile
        // section in a MemoryStream: large batch archives can exceed its 2 GiB limit.
        for (var i = 0; i < _tiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tileBytes = GetTileBytes(_tiles[i].Data);
            await output.WriteAsync(tileBytes, cancellationToken).ConfigureAwait(false);

            // Release source and any transient compressed bytes as soon as this tile is written.
            _tiles[i] = new TileEntry(_tiles[i].TileId, []);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        return (long)(tileDataOffset + tileDataLength);
    }

    private static byte[] BuildJsonMetadata(PMTilesArchiveMetadata metadata)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        writer.WriteString("name", string.IsNullOrWhiteSpace(metadata.Name) ? "Honua" : metadata.Name);

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

        writer.WritePropertyName("vector_layers");
        writer.WriteStartArray();
        foreach (var layer in metadata.VectorLayers)
        {
            writer.WriteStartObject();
            writer.WriteString("id", layer.Id);
            if (layer.Description is not null)
            {
                writer.WriteString("description", layer.Description);
            }
            if (layer.MinZoom is { } minZoom)
            {
                writer.WriteNumber("minzoom", minZoom);
            }
            if (layer.MaxZoom is { } maxZoom)
            {
                writer.WriteNumber("maxzoom", maxZoom);
            }
            writer.WritePropertyName("fields");
            writer.WriteStartObject();
            foreach (var field in layer.Fields)
            {
                writer.WriteString(field.Key, field.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    private static int ToE7Lon(double value)
    {
        var scaled = Math.Clamp(value, -180.0, 180.0) * 10_000_000;
        return (int)Math.Round(scaled);
    }

    private static int ToE7Lat(double value)
    {
        var scaled = Math.Clamp(value, -90.0, 90.0) * 10_000_000;
        return (int)Math.Round(scaled);
    }

    private byte[] GetTileBytes(byte[] data)
        => _tileCompression != PMTilesCompression.None
            ? PMTilesDirectory.Compress(data, _tileCompression)
            : data;

    private readonly record struct TileEntry(ulong TileId, byte[] Data);
}
