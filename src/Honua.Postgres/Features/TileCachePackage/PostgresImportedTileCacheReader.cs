// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.TileCachePackage.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.TileCachePackage;

/// <summary>
/// Postgres-backed serving binding for tiles imported by the tile-cache package
/// importer (#1269). Reads tiles out of <c>honua.tile_caches</c> /
/// <c>honua.tile_cache_entries</c> so imported <c>.tpk</c>/<c>.tpkx</c>/<c>.vtpk</c>
/// caches serve through Honua tile endpoints without re-rendering.
/// </summary>
internal sealed class PostgresImportedTileCacheReader : IImportedTileCacheReader
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresImportedTileCacheReader> _logger;

    public PostgresImportedTileCacheReader(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresImportedTileCacheReader> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ImportedTileCacheInfo?> GetTileCacheAsync(
        string tileCacheId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileCacheId);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT layer_identifier, tile_matrix_set, tile_format, data_type, min_zoom, max_zoom, tileset_title
            FROM honua.tile_caches
            WHERE tile_cache_id = @tile_cache_id;
            """;
        cmd.Parameters.AddWithValue("tile_cache_id", tileCacheId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportedTileCacheInfo
        {
            TileCacheId = tileCacheId,
            LayerIdentifier = reader.GetString(0),
            TileMatrixSet = reader.GetString(1),
            TileFormat = reader.GetString(2),
            DataType = reader.GetString(3),
            MinZoom = reader.GetInt32(4),
            MaxZoom = reader.GetInt32(5),
            Title = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    /// <inheritdoc />
    public async Task<ImportedTile?> GetTileAsync(
        string tileCacheId,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileCacheId);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT content_type, content
            FROM honua.tile_cache_entries
            WHERE tile_cache_id = @tile_cache_id
              AND zoom_level = @zoom_level
              AND tile_column = @tile_column
              AND tile_row = @tile_row;
            """;
        cmd.Parameters.AddWithValue("tile_cache_id", tileCacheId);
        cmd.Parameters.AddWithValue("zoom_level", z);
        cmd.Parameters.AddWithValue("tile_column", x);
        cmd.Parameters.AddWithValue("tile_row", y);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportedTile
        {
            ContentType = reader.GetString(0),
            Content = (byte[])reader.GetValue(1)
        };
    }
}
