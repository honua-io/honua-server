// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

/// <summary>
/// Verifies the compute-once-then-persist statistics behavior behind ImageServer/WCS
/// service metadata (#1639): statistics must be served from persisted rows and never
/// recomputed per request once a backfill has run.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreStatisticsTests(PostgresFixture fixture)
{
    private const int LayerId = 1639;

    [IntegrationTest]
    public async Task GetStatisticsAsync_WithoutPersistedRows_BackfillsAndServesPersistedValues()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertConstantRasterAsync(schemaName, "single", value: 7, upperLeftX: 0);
            var store = CreateStore(schemaName);

            var first = await store.GetStatisticsAsync(LayerId, rasterId);

            first.Should().ContainSingle();
            first[0].Band.Should().Be(1);
            first[0].MinValue.Should().Be(7);
            first[0].MaxValue.Should().Be(7);
            first[0].MeanValue.Should().Be(7);
            first[0].ValidPixelCount.Should().Be(4);
            first[0].NoDataPixelCount.Should().Be(0);

            (await CountAsync(schemaName, "SELECT COUNT(*) FROM raster_statistics WHERE raster_data_id = " + rasterId))
                .Should().Be(1, "the first read must persist the computed statistics");

            // Tamper with the persisted row: a second read must serve the tampered value,
            // proving it does not recompute from the raster pixels.
            await ExecuteAsync(schemaName, $"UPDATE raster_statistics SET min_value = -123 WHERE raster_data_id = {rasterId}");

            var second = await store.GetStatisticsAsync(LayerId, rasterId);

            second.Should().ContainSingle();
            second[0].MinValue.Should().Be(-123);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetStatisticsAsync_ConcurrentColdReads_PersistExactlyOneRowSetPerBand()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertTwoBandRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            var results = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => store.GetStatisticsAsync(LayerId, rasterId)));

            foreach (var result in results)
            {
                result.Should().HaveCount(2);
                result[0].MeanValue.Should().Be(10);
                result[1].MeanValue.Should().Be(20);
            }

            (await CountAsync(schemaName, "SELECT COUNT(*) FROM raster_statistics WHERE raster_data_id = " + rasterId))
                .Should().Be(2, "the advisory lock must guard against a thundering herd of writers");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetMosaicStatisticsAsync_WithoutPersistedRows_BackfillsLayerStatisticsAndServesPersistedValues()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreStatisticsTests));
        try
        {
            // raster_layer_statistics is intentionally not created here: the store must
            // self-provision it (lazy backfill on deployments without the 003 migration).
            await CreateRasterTablesAsync(schemaName);
            var west = await InsertConstantRasterAsync(schemaName, "west", value: 10, upperLeftX: 0);
            var east = await InsertConstantRasterAsync(schemaName, "east", value: 30, upperLeftX: 2);
            var store = CreateStore(schemaName);

            var first = await store.GetMosaicStatisticsAsync(LayerId, [west, east], RasterMergeStrategy.Newest);

            first.Should().ContainSingle();
            first[0].MinValue.Should().Be(10);
            first[0].MaxValue.Should().Be(30);
            first[0].MeanValue.Should().Be(20);

            (await CountAsync(schemaName, $"SELECT COUNT(*) FROM raster_layer_statistics WHERE layer_id = {LayerId}"))
                .Should().Be(1, "the first mosaic read must persist the layer-level statistics");

            await ExecuteAsync(schemaName, $"UPDATE raster_layer_statistics SET max_value = 999 WHERE layer_id = {LayerId}");

            var second = await store.GetMosaicStatisticsAsync(LayerId, [west, east], RasterMergeStrategy.Newest);

            second.Should().ContainSingle();
            second[0].MaxValue.Should().Be(999, "repeat reads must serve the persisted snapshot, not recompute the mosaic");

            // The band filter must also be applied to persisted rows.
            var filtered = await store.GetMosaicStatisticsAsync(LayerId, [west, east], RasterMergeStrategy.Newest, bands: [2]);
            filtered.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetMosaicStatisticsAsync_WhenRasterSetChanges_RecomputesAndPrunesStaleRows()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var west = await InsertConstantRasterAsync(schemaName, "west", value: 10, upperLeftX: 0);
            var east = await InsertConstantRasterAsync(schemaName, "east", value: 30, upperLeftX: 2);
            var store = CreateStore(schemaName);

            var initial = await store.GetMosaicStatisticsAsync(LayerId, [west, east], RasterMergeStrategy.Newest);
            initial.Should().ContainSingle().Which.MaxValue.Should().Be(30);

            // A new raster joins the layer: the signature changes, the persisted snapshot
            // must be recomputed and stale rows for the old raster set pruned.
            var north = await InsertConstantRasterAsync(schemaName, "north", value: 50, upperLeftX: 4);

            var updated = await store.GetMosaicStatisticsAsync(LayerId, [west, east, north], RasterMergeStrategy.Newest);

            updated.Should().ContainSingle();
            updated[0].MaxValue.Should().Be(50);

            (await CountAsync(schemaName, $"SELECT COUNT(DISTINCT raster_signature) FROM raster_layer_statistics WHERE layer_id = {LayerId}"))
                .Should().Be(1, "stale signatures must be pruned when a new snapshot is persisted");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private PostgresRasterStore CreateStore(string schemaName)
        => new(
            new FixtureConnectionProvider(fixture.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            schemaName);

    private async Task CreateRasterTablesAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
            );

            CREATE TABLE IF NOT EXISTS raster_statistics (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL REFERENCES raster_data(id) ON DELETE CASCADE,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertConstantRasterAsync(string schemaName, string name, double value, double upperLeftX)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(2, 2, @upperLeftX, 2, 1, -1, 0, 0, 4326),
                       '32BF'::text,
                       @value,
                       NULL
                   ),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> InsertTwoBandRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'two-band',
                   ST_AddBand(
                       ST_AddBand(
                           ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                           '32BF'::text,
                           10,
                           NULL
                       ),
                       '32BF'::text,
                       20,
                       NULL
                   ),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string schemaName, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string schemaName, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixtureConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

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

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
