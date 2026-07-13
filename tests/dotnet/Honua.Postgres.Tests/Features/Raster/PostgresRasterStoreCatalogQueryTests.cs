// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
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
/// Provider integration + scale tests for the raster-catalog spatial pushdown (#2666). These prove
/// PostGIS performs the indexed footprint (envelope-intersects) filter and paging in SQL before
/// materialization, that only a bounded number of rows are read on a large catalog, and that result
/// count, extent, paging, and envelope-intersects semantics match the neutral contract.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreCatalogQueryTests(PostgresFixture fixture)
{
    private const int LayerId = 9100;

    [IntegrationTest]
    public async Task QueryCatalogAsync_EnvelopeFilter_ReturnsOnlyIntersectingFootprints()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCatalogQueryTests));
        try
        {
            await CreateIndexedRasterTableAsync(schema);
            var west = await InsertRasterAsync(schema, "west", 0, 10);   // extent [0,10]x[0,10]
            var east = await InsertRasterAsync(schema, "east", 20, 30);  // extent [20,30]x[0,10]
            var store = CreateStore(schema);

            var query = new RasterCatalogQuery
            {
                SpatialPredicate = RasterCatalogSpatialPredicate.Create(21, 1, 29, 9, 4326),
                IncludeTotalCount = true,
                IncludeAggregateExtent = true,
                Limit = 10,
            };

            var page = await store.QueryCatalogAsync(LayerId, query);

            page.PredicatePushedDown.Should().BeTrue();
            page.Rasters.Select(r => r.Id).Should().Equal(east);
            page.TotalCount.Should().Be(1);
            _ = west;
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task QueryCatalogAsync_EnvelopeFilter_IsEdgeInclusive()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCatalogQueryTests));
        try
        {
            await CreateIndexedRasterTableAsync(schema);
            var raster = await InsertRasterAsync(schema, "tile", 0, 10);
            var store = CreateStore(schema);

            // Filter box whose left edge exactly meets the footprint's right edge (x = 10) must match
            // — the same inclusive envelope-intersects the in-memory contract guarantees.
            var query = new RasterCatalogQuery
            {
                SpatialPredicate = RasterCatalogSpatialPredicate.Create(10, 0, 20, 10, 4326),
                Limit = 10,
            };

            var page = await store.QueryCatalogAsync(LayerId, query);

            page.Rasters.Select(r => r.Id).Should().Equal(raster);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task QueryCatalogAsync_Paging_ReturnsBoundedWindowWithTotalCount()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCatalogQueryTests));
        try
        {
            await CreateIndexedRasterTableAsync(schema);
            for (var i = 0; i < 5; i++)
            {
                await InsertRasterAsync(schema, $"tile-{i}", i * 10.0, (i * 10.0) + 5);
            }

            var store = CreateStore(schema);
            var query = new RasterCatalogQuery { Offset = 1, Limit = 2, IncludeTotalCount = true };

            var page = await store.QueryCatalogAsync(LayerId, query);

            page.Rasters.Should().HaveCount(2);
            page.TotalCount.Should().Be(5);
            page.RowsReturned.Should().Be(2);
            page.RowsScanned.Should().Be(5);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task QueryCatalogAsync_AggregateExtent_UnionsFilteredSet()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCatalogQueryTests));
        try
        {
            await CreateIndexedRasterTableAsync(schema);
            await InsertRasterAsync(schema, "west", 0, 10);
            await InsertRasterAsync(schema, "east", 20, 30);
            var store = CreateStore(schema);

            var query = new RasterCatalogQuery { IncludeAggregateExtent = true, IncludeTotalCount = true, Limit = 10 };

            var page = await store.QueryCatalogAsync(LayerId, query);

            page.AggregateExtent.Should().NotBeNull();
            var aggregateExtent = page.AggregateExtent is { } extent
                ? extent
                : throw new InvalidOperationException("Expected an aggregate extent.");
            aggregateExtent.XMin.Should().Be(0);
            aggregateExtent.XMax.Should().Be(30);
            page.TotalCount.Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    /// <summary>
    /// Scale test: seed a large catalog, then prove the selective footprint query is served by the
    /// GiST envelope index (EXPLAIN plan) and reads only a bounded number of rows — never the full
    /// catalog — while returning the correct small matched set and total count.
    /// </summary>
    [ScaleTest]
    public async Task QueryCatalogAsync_LargeCatalog_UsesFootprintIndexAndBoundsReads()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCatalogQueryTests));
        try
        {
            await CreateIndexedRasterTableAsync(schema);

            // 40 x 40 = 1600 footprints tiled across a wide area; only a 3x3 pocket sits inside the
            // query envelope, so a full-catalog scan would read ~1600 rows to answer a ~9-row query.
            const int grid = 40;
            await SeedGridAsync(schema, grid, cell: 10);
            await AnalyzeAsync(schema);

            var store = CreateStore(schema);
            var query = new RasterCatalogQuery
            {
                // Covers footprints whose lower-left corner is at x,y in {110,120,130} => 9 tiles.
                SpatialPredicate = RasterCatalogSpatialPredicate.Create(111, 111, 139, 139, 4326),
                IncludeTotalCount = true,
                IncludeAggregateExtent = true,
                Limit = 100,
            };

            var plan = await store.ExplainCatalogQueryAsync(LayerId, query);
            var page = await store.QueryCatalogAsync(LayerId, query);

            // Query-plan evidence: the footprint predicate is served by the GiST envelope index,
            // not a full sequential scan of the catalog.
            plan.Should().Contain("idx_raster_data_envelope");
            plan.Should().MatchRegex("Index Scan|Bitmap Index Scan|Bitmap Heap Scan");

            // Bounded reads: far fewer than the full 1600-row catalog were matched/returned.
            page.TotalCount.Should().BeLessThan(50);
            page.TotalCount.Should().BeGreaterThan(0);
            page.Rasters.Count.Should().Be((int)page.TotalCount);
            page.RowsReturned.Should().BeLessThan(50);
            page.PredicatePushedDown.Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresRasterStore CreateStore(string schema)
        => new(new FixtureConnectionProvider(fixture.DataSource), NullLogger<PostgresRasterStore>.Instance, schema);

    private async Task SeedGridAsync(string schema, int grid, double cell)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        // One INSERT ... SELECT generating grid*grid tiny 1x1 rasters via generate_series.
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, created_at)
            SELECT @layerId,
                   'tile-' || gx || '-' || gy,
                   ST_AddBand(
                       ST_MakeEmptyRaster(1, 1, gx * @cell, (gy * @cell) + @cell, @cell, -@cell, 0, 0, 4326),
                       '8BUI'::text, 1, NULL),
                   NOW()
            FROM generate_series(0, @grid - 1) AS gx,
                 generate_series(0, @grid - 1) AS gy;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("grid", grid);
        command.Parameters.AddWithValue("cell", cell);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AnalyzeAsync(string schema)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = "ANALYZE raster_data;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertRasterAsync(string schema, string name, double minX, double maxX)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        // A single-cell raster spanning [minX,maxX] x [0,10]; scale derived from the span.
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(1, 1, @minX, 10, @maxX - @minX, -10, 0, 0, 4326),
                       '8BUI'::text, 1, NULL),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("minX", minX);
        command.Parameters.AddWithValue("maxX", maxX);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task CreateIndexedRasterTableAsync(string schema)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        // Mirrors migration 001 including the GiST envelope expression index the pushdown relies on.
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
            CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON raster_data(layer_id);
            CREATE INDEX IF NOT EXISTS idx_raster_data_envelope ON raster_data USING GIST (ST_Envelope(raster));
            """;
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
            Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
