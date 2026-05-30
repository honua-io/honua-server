// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Idempotent Postgres-backed sink for the WMTS tile-cache exporter
/// (#1016 slice 4). Writes tile bytes into <c>honua.tile_caches</c> and
/// <c>honua.tile_cache_entries</c>, deriving stable cache identifiers from
/// the descriptor so repeated exports converge on the same row set.
/// </summary>
internal sealed partial class PostgresOgcTileCacheSink : IOgcTileCacheSink
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresOgcTileCacheSink> _logger;

    public PostgresOgcTileCacheSink(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresOgcTileCacheSink> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> EnsureTileCacheAsync(
        OgcTileCacheDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var cacheId = BuildTileCacheId(descriptor);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO honua.tile_caches (
                tile_cache_id,
                layer_identifier,
                tile_matrix_set,
                source_service_url,
                tile_format,
                style_identifier,
                min_zoom,
                max_zoom)
            VALUES (
                @tile_cache_id,
                @layer_identifier,
                @tile_matrix_set,
                @source_service_url,
                @tile_format,
                @style_identifier,
                @min_zoom,
                @max_zoom)
            ON CONFLICT (tile_cache_id) DO UPDATE
                SET min_zoom   = LEAST(honua.tile_caches.min_zoom, EXCLUDED.min_zoom),
                    max_zoom   = GREATEST(honua.tile_caches.max_zoom, EXCLUDED.max_zoom),
                    updated_at = NOW();
            """;
        cmd.Parameters.AddWithValue("tile_cache_id", cacheId);
        cmd.Parameters.AddWithValue("layer_identifier", descriptor.LayerIdentifier);
        cmd.Parameters.AddWithValue("tile_matrix_set", descriptor.TileMatrixSetIdentifier);
        cmd.Parameters.AddWithValue("source_service_url", descriptor.SourceServiceUrl);
        cmd.Parameters.AddWithValue("tile_format", descriptor.TileFormat);
        cmd.Parameters.AddWithValue("style_identifier", descriptor.StyleIdentifier);
        cmd.Parameters.AddWithValue("min_zoom", descriptor.MinZoom);
        cmd.Parameters.AddWithValue("max_zoom", descriptor.MaxZoom);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Log.CacheEnsured(_logger, cacheId, descriptor.LayerIdentifier, descriptor.TileMatrixSetIdentifier);

        return cacheId;
    }

    /// <inheritdoc />
    public async Task<OgcTileCacheWriteStatus> WriteTileAsync(
        OgcTileCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO honua.tile_cache_entries (
                tile_cache_id,
                zoom_level,
                tile_column,
                tile_row,
                content_type,
                content,
                source_url)
            VALUES (
                @tile_cache_id,
                @zoom_level,
                @tile_column,
                @tile_row,
                @content_type,
                @content,
                @source_url)
            ON CONFLICT (tile_cache_id, zoom_level, tile_column, tile_row) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("tile_cache_id", record.TileCacheId);
        cmd.Parameters.AddWithValue("zoom_level", record.Z);
        cmd.Parameters.AddWithValue("tile_column", record.X);
        cmd.Parameters.AddWithValue("tile_row", record.Y);
        cmd.Parameters.AddWithValue("content_type", record.ContentType);
        cmd.Parameters.Add("content", NpgsqlDbType.Bytea).Value = record.Content;
        cmd.Parameters.AddWithValue("source_url", record.SourceUrl);

        var inserted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 1)
        {
            return OgcTileCacheWriteStatus.Inserted;
        }

        return OgcTileCacheWriteStatus.AlreadyPresent;
    }

    /// <summary>
    /// Build a deterministic tile-cache identifier from the descriptor. The
    /// identifier collapses long URLs and identifiers to a short fingerprint
    /// so the row count in <c>honua.tile_caches</c> stays bounded by the
    /// number of distinct (layer, tile-matrix-set, style, format, source URL)
    /// tuples.
    /// </summary>
    internal static string BuildTileCacheId(OgcTileCacheDescriptor descriptor)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{descriptor.LayerIdentifier}|{descriptor.TileMatrixSetIdentifier}|{descriptor.StyleIdentifier}|{descriptor.TileFormat}|{descriptor.SourceServiceUrl}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var fingerprint = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return $"tilecache:{SanitizeSlug(descriptor.LayerIdentifier)}:{SanitizeSlug(descriptor.TileMatrixSetIdentifier)}:{fingerprint}";
    }

    private static string SanitizeSlug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.AsSpan())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "_" : slug;
    }

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information,
            "Tile cache {TileCacheId} ensured for layer {Layer} tile-matrix-set {TileMatrixSet}")]
        public static partial void CacheEnsured(ILogger logger, string tileCacheId, string layer, string tileMatrixSet);
    }
}
