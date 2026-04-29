// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class RasterSurfaceServiceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await EnsureRasterTablesAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ComputeSlopeAsync_WritesDerivedRasterAndReturnsQueryableOutput()
    {
        const int sourceLayerId = 7101;
        const int outputLayerId = 7102;
        var sourceRasterId = await InsertTestRasterAsync(sourceLayerId, "slope-source");

        var surfaceService = CreateSurfaceService();
        var rasterStore = CreateRasterStore();

        var result = await surfaceService.ComputeSlopeAsync(
            new SurfaceAnalysisRequest
            {
                SourceLayerId = sourceLayerId,
                SourceRasterId = sourceRasterId,
                OutputLayerId = outputLayerId,
                OutputName = "slope-output"
            },
            SlopeUnits.Degrees,
            zFactor: 1.0);

        result.LayerId.Should().Be(outputLayerId);
        result.Width.Should().Be(3);
        result.Height.Should().Be(3);
        result.Srid.Should().Be(4326);

        var info = await rasterStore.GetRasterInfoAsync(outputLayerId, result.RasterId);
        info.Should().NotBeNull();
        info!.Value.BandCount.Should().Be(1);

        var identify = await rasterStore.IdentifyAsync(outputLayerId, result.RasterId, 1.5, 1.5, 4326);
        identify.HasData.Should().BeTrue();
        identify.BandValues.Should().ContainKey(1);
        identify.BandValues[1].Should().BeOfType<double>();
        ((double)identify.BandValues[1]!).Should().BeGreaterThan(0d);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ComputeZonalStatisticsAsync_ReturnsOneAggregateRowPerZone()
    {
        const int sourceLayerId = 7201;
        const int zonesLayerId = 7202;
        var sourceRasterId = await InsertTestRasterAsync(sourceLayerId, "zonal-source");
        await InsertZoneFeaturesAsync(zonesLayerId);

        var rasterStore = CreateRasterStore();

        var rows = await rasterStore.ComputeZonalStatisticsAsync(
            sourceLayerId,
            sourceRasterId,
            zonesLayerId,
            band: 1,
            statistics: ["count", "mean", "max"]);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.Band == 1 && row.PixelCount > 0);
        rows.Should().AllSatisfy(row =>
        {
            row.Stats.Should().ContainKeys("count", "mean", "max");
            row.Stats["mean"].Should().NotBeNull();
            row.Stats["max"].Should().NotBeNull();
        });

        rows[0].Stats["mean"]!.Value.Should().BeLessThan(rows[1].Stats["mean"]!.Value);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ComputeZonalStatisticsAsync_ReprojectsZoneGeometriesWhenSridDiffersFromRaster()
    {
        const int sourceLayerId = 7301;
        const int zonesLayerId = 7302;
        var sourceRasterId = await InsertTestRasterAsync(sourceLayerId, "zonal-srid-mismatch");
        // Zones in EPSG:3857 (Web Mercator) against a raster in EPSG:4326.
        // ST_Transform on each zone geometry projects them back into the raster CRS so
        // ST_Intersects / ST_Clip operate in matched coordinates.
        await InsertZoneFeaturesInSridAsync(
            zonesLayerId,
            3857,
            [
                // Zone 1: covers raster pixels (0,0)..(2,3) in EPSG:4326 after reprojection.
                "POLYGON((0 0, 222638.98 0, 222638.98 334111.17, 0 334111.17, 0 0))",
                // Zone 2: covers raster pixels (2,0)..(3,3) in EPSG:4326 after reprojection.
                "POLYGON((222638.98 0, 333958.47 0, 333958.47 334111.17, 222638.98 334111.17, 222638.98 0))",
            ]);

        var rasterStore = CreateRasterStore();

        var rows = await rasterStore.ComputeZonalStatisticsAsync(
            sourceLayerId,
            sourceRasterId,
            zonesLayerId,
            band: 1,
            statistics: ["count", "mean"]);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.PixelCount > 0);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ComputeZonalStatisticsAsync_WhenSourceRasterMissing_ThrowsInvalidOperation()
    {
        const int sourceLayerId = 7401;
        const int zonesLayerId = 7402;
        // Seed zones but no raster under sourceLayerId/rasterId 999.
        await InsertZoneFeaturesAsync(zonesLayerId);

        var rasterStore = CreateRasterStore();

        var act = async () => await rasterStore.ComputeZonalStatisticsAsync(
            sourceLayerId,
            rasterId: 999,
            zonesLayerId,
            band: 1,
            statistics: ["count", "mean"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found*");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ComputeZonalStatisticsAsync_WhenSourceRasterSridIsUnknown_ThrowsInvalidOperation()
    {
        const int sourceLayerId = 7501;
        const int zonesLayerId = 7502;
        // Raster import leaves SRID=0 when CRS detection fails (see PostgresRasterImportService).
        // ST_Transform(geometry, 0) aborts the zonal query; the store must fail fast instead.
        var sourceRasterId = await InsertTestRasterWithSridAsync(sourceLayerId, "zonal-unknown-srid", srid: 0);
        await InsertZoneFeaturesAsync(zonesLayerId);

        var rasterStore = CreateRasterStore();

        var act = async () => await rasterStore.ComputeZonalStatisticsAsync(
            sourceLayerId,
            sourceRasterId,
            zonesLayerId,
            band: 1,
            statistics: ["count", "mean"]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown SRID*");
    }

    private async Task EnsureRasterTablesAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                description TEXT,
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
                raster_data_id BIGINT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS raster_tiles (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL,
                zoom_level INTEGER NOT NULL,
                tile_x INTEGER NOT NULL,
                tile_y INTEGER NOT NULL,
                tile_data BYTEA NOT NULL,
                content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertTestRasterAsync(int layerId, string name)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, description, raster)
            VALUES (
                @layerId,
                @name,
                'test-dem',
                ST_SetValues(
                    ST_AddBand(
                        ST_MakeEmptyRaster(3, 3, 0, 3, 1, -1, 0, 0, 4326),
                        1,
                        '32BF',
                        0,
                        -9999),
                    1,
                    1,
                    1,
                    ARRAY[
                        ARRAY[1.0::float8, 2.0::float8, 3.0::float8],
                        ARRAY[4.0::float8, 5.0::float8, 6.0::float8],
                        ARRAY[7.0::float8, 8.0::float8, 9.0::float8]
                    ]))
            RETURNING id;
            """;
        command.Parameters.Add(new NpgsqlParameter("@layerId", layerId));
        command.Parameters.Add(new NpgsqlParameter("@name", name));

        var result = await command.ExecuteScalarAsync();
        return (long)result!;
    }

    private async Task<long> InsertTestRasterWithSridAsync(int layerId, string name, int srid)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, description, raster)
            VALUES (
                @layerId,
                @name,
                'test-dem',
                ST_SetValues(
                    ST_AddBand(
                        ST_MakeEmptyRaster(3, 3, 0, 3, 1, -1, 0, 0, @srid),
                        1,
                        '32BF',
                        0,
                        -9999),
                    1,
                    1,
                    1,
                    ARRAY[
                        ARRAY[1.0::float8, 2.0::float8, 3.0::float8],
                        ARRAY[4.0::float8, 5.0::float8, 6.0::float8],
                        ARRAY[7.0::float8, 8.0::float8, 9.0::float8]
                    ]))
            RETURNING id;
            """;
        command.Parameters.Add(new NpgsqlParameter("@layerId", layerId));
        command.Parameters.Add(new NpgsqlParameter("@name", name));
        command.Parameters.Add(new NpgsqlParameter("@srid", srid));

        var result = await command.ExecuteScalarAsync();
        return (long)result!;
    }

    private async Task InsertZoneFeaturesAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES
                (@layerId, ST_GeomFromText('POLYGON((0 0, 2 0, 2 3, 0 3, 0 0))', 4326), '{}'::jsonb),
                (@layerId, ST_GeomFromText('POLYGON((2 0, 3 0, 3 3, 2 3, 2 0))', 4326), '{}'::jsonb);
            """;
        command.Parameters.Add(new NpgsqlParameter("@layerId", layerId));
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertZoneFeaturesInSridAsync(int layerId, int srid, IReadOnlyList<string> wkts)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        foreach (var wkt in wkts)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO features (layer_id, geometry, attributes)
                VALUES (@layerId, ST_GeomFromText(@wkt, @srid), '{}'::jsonb);
                """;
            command.Parameters.Add(new NpgsqlParameter("@layerId", layerId));
            command.Parameters.Add(new NpgsqlParameter("@wkt", wkt));
            command.Parameters.Add(new NpgsqlParameter("@srid", srid));
            await command.ExecuteNonQueryAsync();
        }
    }

    private PostgresRasterStore CreateRasterStore()
        => new PostgresRasterStore(
            CreateConnectionProvider(),
            NullLogger<PostgresRasterStore>.Instance,
            _fixture.CurrentSchema);

    private PostgresSurfaceAnalysisService CreateSurfaceService()
        => new PostgresSurfaceAnalysisService(
            CreateConnectionProvider(),
            NullLogger<PostgresSurfaceAnalysisService>.Instance,
            _fixture.CurrentSchema);

    private PostgresDatabaseConnectionProvider CreateConnectionProvider()
        => new(
            _fixture.Postgres.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
}
