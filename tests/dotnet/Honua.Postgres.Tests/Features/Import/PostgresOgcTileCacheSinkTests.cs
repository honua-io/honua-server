// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Import;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for <see cref="PostgresOgcTileCacheSink"/> backed by a
/// real Postgres + PostGIS container.
/// </summary>
[Collection("Database")]
public sealed class PostgresOgcTileCacheSinkTests(PostgresFixture fixture)
{
    private const string SourceServiceUrl = "https://wmts.example.test/wmts";

    private async Task EnsureTileCacheSchemaAsync()
    {
        await using var conn = await fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE IF NOT EXISTS honua.tile_caches (
                tile_cache_id      TEXT PRIMARY KEY,
                layer_identifier   TEXT NOT NULL,
                tile_matrix_set    TEXT NOT NULL,
                source_service_url TEXT NOT NULL,
                tile_format        TEXT NOT NULL DEFAULT 'image/png',
                style_identifier   TEXT NOT NULL DEFAULT 'default',
                min_zoom           INTEGER NOT NULL,
                max_zoom           INTEGER NOT NULL,
                created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (layer_identifier, tile_matrix_set, style_identifier, tile_format, source_service_url)
            );
            CREATE TABLE IF NOT EXISTS honua.tile_cache_entries (
                tile_cache_id  TEXT NOT NULL REFERENCES honua.tile_caches (tile_cache_id) ON DELETE CASCADE,
                zoom_level     INTEGER NOT NULL,
                tile_column    INTEGER NOT NULL,
                tile_row       INTEGER NOT NULL,
                content_type   TEXT NOT NULL,
                content        BYTEA NOT NULL,
                source_url     TEXT NOT NULL,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (tile_cache_id, zoom_level, tile_column, tile_row)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task WriteTileAsync_Inserts_Then_RepeatedWrite_IsAlreadyPresent()
    {
        await EnsureTileCacheSchemaAsync();

        var sink = new PostgresOgcTileCacheSink(
            new FixtureConnectionProvider(fixture),
            NullLogger<PostgresOgcTileCacheSink>.Instance);

        var descriptor = new OgcTileCacheDescriptor
        {
            LayerIdentifier = "basemap",
            TileMatrixSetIdentifier = "WebMercatorQuad",
            SourceServiceUrl = SourceServiceUrl,
            TileFormat = "image/png",
            StyleIdentifier = "default",
            MinZoom = 0,
            MaxZoom = 1
        };
        var cacheId = await sink.EnsureTileCacheAsync(descriptor);
        cacheId.Should().StartWith("tilecache:basemap:webmercatorquad:");

        var bytes = Encoding.UTF8.GetBytes("tile-content-z0-x0-y0");
        var record = new OgcTileCacheRecord
        {
            TileCacheId = cacheId,
            Z = 0,
            X = 0,
            Y = 0,
            ContentType = "image/png",
            Content = bytes,
            SourceUrl = SourceServiceUrl
        };

        var firstStatus = await sink.WriteTileAsync(record);
        firstStatus.Should().Be(OgcTileCacheWriteStatus.Inserted);

        var secondStatus = await sink.WriteTileAsync(record);
        secondStatus.Should().Be(OgcTileCacheWriteStatus.AlreadyPresent);

        var (rowCount, persistedContent) = await ReadTileAsync(cacheId, 0, 0, 0);
        rowCount.Should().Be(1, "repeated writes must not duplicate the row");
        persistedContent.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task EnsureTileCacheAsync_IsIdempotent_AndExpandsZoomRange()
    {
        await EnsureTileCacheSchemaAsync();

        var sink = new PostgresOgcTileCacheSink(
            new FixtureConnectionProvider(fixture),
            NullLogger<PostgresOgcTileCacheSink>.Instance);

        var descriptor = new OgcTileCacheDescriptor
        {
            LayerIdentifier = "topo",
            TileMatrixSetIdentifier = "WebMercatorQuad",
            SourceServiceUrl = SourceServiceUrl,
            TileFormat = "image/png",
            StyleIdentifier = "default",
            MinZoom = 1,
            MaxZoom = 2
        };
        var firstId = await sink.EnsureTileCacheAsync(descriptor);
        var expanded = descriptor with { MinZoom = 0, MaxZoom = 3 };
        var secondId = await sink.EnsureTileCacheAsync(expanded);
        secondId.Should().Be(firstId, "the descriptor fingerprint must be deterministic");

        var (min, max) = await ReadCacheZoomRangeAsync(firstId);
        min.Should().Be(0);
        max.Should().Be(3);
    }

    private async Task<(int RowCount, byte[] Content)> ReadTileAsync(string cacheId, int z, int x, int y)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT content FROM honua.tile_cache_entries
            WHERE tile_cache_id = @cache AND zoom_level = @z AND tile_column = @x AND tile_row = @y;
            """;
        cmd.Parameters.AddWithValue("cache", cacheId);
        cmd.Parameters.AddWithValue("z", z);
        cmd.Parameters.AddWithValue("x", x);
        cmd.Parameters.AddWithValue("y", y);

        var bytes = Array.Empty<byte>();
        var count = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
            bytes = (byte[])reader.GetValue(0);
        }

        return (count, bytes);
    }

    private async Task<(int Min, int Max)> ReadCacheZoomRangeAsync(string cacheId)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT min_zoom, max_zoom FROM honua.tile_caches WHERE tile_cache_id = @cache;";
        cmd.Parameters.AddWithValue("cache", cacheId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => postgresFixture.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await postgresFixture.DataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
