// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Date-line polygons are stored on the short side and therefore only tile the Pacific.
/// A NAD27 draw matches the catalog pipeline within a metre when the NADCON grid is installed,
/// and fails clearly when it is not.
/// </summary>
[Collection("Database")]
public sealed class DatumAndAntimeridianIntegrationTests(PostgresFixture fixture)
{
    private const string Pacific =
        "MULTIPOLYGON(((170 0, 180 0, 180 10, 170 10, 170 0)),((-180 0, -170 0, -170 10, -180 10, -180 0)))";

    private const double WebMercatorExtent = 20037508.342789244;
    private const string NadconProbe =
        "SELECT ST_TransformPipeline(ST_SetSRID(ST_MakePoint(-100, 40), 4267), " +
        "'+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=hgridshift " +
        "+grids=us_noaa_conus.tif +step +proj=unitconvert +xy_in=rad +xy_out=deg', 4269)";

    [IntegrationTest]
    public async Task PacificMultipolygon_IsOnlyInTilesThatMeetIt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PacificMultipolygon_IsOnlyInTilesThatMeetIt));
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    layer_id integer NOT NULL,
                    objectid bigint NOT NULL,
                    geometry geometry,
                    attributes jsonb
                );
                INSERT INTO {schema}.features (layer_id, objectid, geometry, attributes)
                VALUES (1, 1, ST_GeomFromText('{Pacific}', 4326), jsonb_build_object());
                """);

            await AssertPacificOnlyAsync(schema, geographic: true, zoom: 1);
            await AssertPacificOnlyAsync(schema, geographic: false, zoom: 2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Nad27Tile_MissingGrid_FailsInsteadOfNullTransform()
    {
        if (await PipelineSucceedsAsync(fixture.ConnectionString))
        {
            // The shared database already has us_noaa_conus.tif. The failure contract is
            // the grid-absent case, matching DatumGridProvisioningTests.
            return;
        }

        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(Nad27Tile_MissingGrid_FailsInsteadOfNullTransform));
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    layer_id integer NOT NULL,
                    objectid bigint NOT NULL,
                    geometry geometry,
                    attributes jsonb
                );
                INSERT INTO {schema}.features (layer_id, objectid, geometry, attributes)
                VALUES (1, 1, ST_GeomFromText('POINT(-100 40)', 4267), jsonb_build_object());
                """);

            // Zoom 8 tile 56,101 is inside the CONUS NAD27 area, so the catalog selects
            // us_noaa_conus.tif. A world tile matches no single area and stays on ST_Transform.
            var act = () => ReadTileAsync(
                fixture.ConnectionString, schema, geographic: false, zoom: 8, x: 56, y: 101, layerSrid: 4267);

            var error = await act.Should().ThrowAsync<ValidationException>();
            error.Which.Message.Should().Contain("grid");
            error.Which.Message.Should().Contain("null transform");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Nad27TileAndIdentify_WithGrid_MatchCatalogPipelineWithinOneMetre()
    {
        await using var grid = new DatumGridPostgresFixture();
        await grid.InitializeAsync(includeNadconGrid: true);
        var schema = "datum5194";
        await using (var connection = await grid.DataSource.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE EXTENSION IF NOT EXISTS postgis_raster;
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.features (
                    layer_id integer NOT NULL,
                    objectid bigint NOT NULL,
                    geometry geometry,
                    attributes jsonb
                );
                INSERT INTO {schema}.features (layer_id, objectid, geometry, attributes)
                VALUES (1, 1, ST_GeomFromText('POINT(-100 40)', 4267), jsonb_build_object());
                CREATE TABLE {schema}.raster_data (
                    id bigserial PRIMARY KEY,
                    layer_id integer NOT NULL,
                    raster raster NOT NULL
                );
                INSERT INTO {schema}.raster_data (layer_id, raster)
                SELECT 1, ST_AddBand(
                    ST_MakeEmptyRaster(1, 1, -100.0001, 40.0001, 0.0002, -0.0002, 0, 0, 4267),
                    '8BUI'::text, 7, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var selection = DatumOutputSelection.Resolve(EsriDatumTransformationCatalog.Create(), 4267, 3857);
        selection.Should().NotBeNull();
        var pipeline = ProjPipelineInverter.Orient(selection!.ProjPipeline!, selection.TransformForward);
        double oracleX;
        double oracleY;
        await using (var connection = await grid.DataSource.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ST_X(g), ST_Y(g)
                FROM (
                    SELECT ST_Transform(
                        ST_TransformPipeline(ST_SetSRID(ST_MakePoint(-100, 40), 4267), @pipeline, 4326),
                        3857) AS g
                ) q
                """;
            command.Parameters.AddWithValue("pipeline", pipeline);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            oracleX = reader.GetDouble(0);
            oracleY = reader.GetDouble(1);
        }

        const int zoom = 16;
        var span = WebMercatorExtent * 2d / Math.Pow(2, zoom);
        var tileX = (int)Math.Floor((oracleX + WebMercatorExtent) / span);
        var tileY = (int)Math.Floor((WebMercatorExtent - oracleY) / span);
        var tile = await ReadTileAsync(
            grid.ConnectionString, schema, geographic: false, zoom, tileX, tileY, layerSrid: 4267);
        tile.Should().NotBeNullOrEmpty();
        var point = MvtTileDecoder.Decode(tile!).Layer("layer").Features.Should().ContainSingle().Subject.Points.Should().ContainSingle().Subject;
        var worldX = -WebMercatorExtent + tileX * span + (point.X / 4096d) * span;
        var worldY = WebMercatorExtent - tileY * span - (point.Y / 4096d) * span;
        worldX.Should().BeApproximately(oracleX, 1d);
        worldY.Should().BeApproximately(oracleY, 1d);

        var store = new PostgresRasterStore(
            new GridConnectionProvider(grid.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance,
            schema);
        var identified = await store.IdentifyAsync(1, 1, oracleX, oracleY, 3857);
        identified.HasData.Should().BeTrue();
        identified.BandValues.Should().ContainKey(1);
        Convert.ToDouble(identified.BandValues[1], CultureInfo.InvariantCulture).Should().Be(7d);

        double identifiedX;
        double identifiedY;
        var sample = DatumSamplePointSql.Build(3857, 4267);
        await using (var connection = await grid.DataSource.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT ST_X(p), ST_Y(p) FROM (SELECT {sample} AS p) q";
            command.Parameters.AddWithValue("x", oracleX);
            command.Parameters.AddWithValue("y", oracleY);
            command.Parameters.AddWithValue("pointSrid", 3857);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            identifiedX = reader.GetDouble(0);
            identifiedY = reader.GetDouble(1);
        }

        // One metre is about 1.1e-5 degrees. The inverse catalog pipeline returns the NAD27 point.
        identifiedX.Should().BeApproximately(-100d, 1.1e-5);
        identifiedY.Should().BeApproximately(40d, 1.1e-5);
    }

    private async Task AssertPacificOnlyAsync(string schema, bool geographic, int zoom)
    {
        var columns = geographic ? 2 * (1 << zoom) : 1 << zoom;
        var rows = 1 << zoom;
        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                var meetsPacific = x == 0 || x == columns - 1;
                var tile = await ReadTileAsync(fixture.ConnectionString, schema, geographic, zoom, x, y);
                var count = tile is { Length: > 0 } ? MvtTileDecoder.Decode(tile).FeatureCount : 0;
                if (meetsPacific && y == (geographic ? 0 : rows / 2 - 1))
                {
                    count.Should().Be(1, $"tile {x},{y} meets the Pacific portion");
                }
                else if (!meetsPacific)
                {
                    count.Should().Be(0, $"tile {x},{y} does not meet the Pacific portion");
                }
            }
        }
    }

    private static async Task<byte[]?> ReadTileAsync(
        string connectionString,
        string schema,
        bool geographic,
        int zoom,
        int x,
        int y,
        int layerSrid = 4326)
    {
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
        var query = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema).BuildMvtTileQuery(
            1, x, y, zoom,
            new FeatureQuery
            {
                SpatialReferenceSrid = layerSrid,
                OutputSrid = geographic ? 4326 : null
            },
            new TileOptions { TileBuffer = 0, TileExtent = 4096, SimplifyZoom = -1 },
            new TileLimits());
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query.Sql, connection);
        foreach (var parameter in query.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await PostgresMvtReader.ReadAsync(command, 1_000_000, CancellationToken.None);
    }

    private static async Task<bool> PipelineSucceedsAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(NadconProbe, connection);
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private sealed class GridConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
