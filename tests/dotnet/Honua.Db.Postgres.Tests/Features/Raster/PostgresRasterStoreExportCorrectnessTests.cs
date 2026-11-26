// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterStoreExportCorrectnessTests(PostgresFixture fixture)
{
    private const int LayerId = 9015;

    [IntegrationTest]
    public async Task ExportImageAsync_WithPixelSize_PreservesNorthUpOrientation()
    {
        var schemaName = await CreateIsolatedRasterSchemaAsync();
        try
        {
            var rasterId = await InsertDistinctRowValuesRasterAsync(schemaName);
            var exported = await CreateStore(schemaName).ExportImageAsync(
                LayerId,
                rasterId,
                new RasterQuery
                {
                    OutputFormat = RasterFormat.TIFF,
                    PixelSize = new PixelSize { Width = 0.5, Height = 0.5 },
                    ResamplingAlgorithm = ResamplingAlgorithm.NearestNeighbor
                });

            var (scaleY, topLeft, bottomLeft) = await ReadScaleAndCornersAsync(schemaName, exported.Data);
            scaleY.Should().BeNegative("north-up raster must keep negative ScaleY after ST_Rescale");
            topLeft.Should().Be(10d, "top row pixel must stay on top");
            bottomLeft.Should().Be(30d, "bottom row pixel must stay on bottom");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithBilinearResamplingAndOutputDimensions_InterpolatesPixels()
    {
        var schemaName = await CreateIsolatedRasterSchemaAsync();
        try
        {
            var rasterId = await InsertDistinctRowValuesRasterAsync(schemaName);
            var bilinear = await CreateStore(schemaName).ExportImageAsync(
                LayerId,
                rasterId,
                new RasterQuery
                {
                    OutputFormat = RasterFormat.TIFF,
                    OutputWidth = 4,
                    OutputHeight = 4,
                    ResamplingAlgorithm = ResamplingAlgorithm.Bilinear
                });

            var (horizontalMid, verticalMid) = await ReadBilinearMidpointsAsync(schemaName, bilinear.Data);
            (horizontalMid > 10d && horizontalMid < 20d || verticalMid > 10d && verticalMid < 30d)
                .Should().BeTrue("Bilinear resize must interpolate intermediate values rather than hardcoding NearestNeighbor");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithCoverClipExtentAndNullNoData_PreservesNullNoDataAndZeroPixels()
    {
        var schemaName = await CreateIsolatedRasterSchemaAsync();
        try
        {
            var rasterId = await InsertZeroAndNonZeroNullNoDataRasterAsync(schemaName);
            var exported = await CreateStore(schemaName).ExportImageAsync(
                LayerId,
                rasterId,
                new RasterQuery
                {
                    OutputFormat = RasterFormat.TIFF,
                    ClipRegion = new RasterClipRegion { Geometry = CreateEnvelopeWkb((0, 0, 2, 2)), Srid = 4326 },
                    CoverClipExtent = true,
                    OutputWidth = 2,
                    OutputHeight = 2
                });

            var (noDataIsNull, firstIsNull, firstVal, secondVal) = await ReadNoDataAndSamplesAsync(schemaName, exported.Data);
            noDataIsNull.Should().BeTrue("bands without declared NoData must keep NoData IS NULL after clip framing");
            firstIsNull.Should().BeFalse("valid black (0) pixel must remain data rather than becoming NoData");
            firstVal.Should().Be(0d);
            secondVal.Should().Be(200d);
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
            FixtureBypassDatabaseSchemaGuard.Instance,
            schemaName);

    private async Task<string> CreateIsolatedRasterSchemaAsync()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreExportCorrectnessTests));
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
        return schemaName;
    }

    private async Task<long> InsertDistinctRowValuesRasterAsync(string schemaName)
        => await InsertGridRasterAsync(
            schemaName,
            "distinct-rows",
            ("'32BF'::text, 0, NULL", "ARRAY[ARRAY[10.0, 20.0], ARRAY[30.0, 40.0]]::double precision[][]"));

    private async Task<long> InsertZeroAndNonZeroNullNoDataRasterAsync(string schemaName)
        => await InsertGridRasterAsync(
            schemaName,
            "null-nodata-zero",
            ("'8BUI'::text, 0, NULL", "ARRAY[ARRAY[0.0, 200.0], ARRAY[100.0, 200.0]]::double precision[][]"));

    private async Task<long> InsertGridRasterAsync(
        string schemaName,
        string name,
        (string BandSpecSql, string ValuesSql) grid)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_SetValues(
                       ST_AddBand(
                           ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                           {grid.BandSpecSql}),
                       1, 1, 1,
                       {grid.ValuesSql}),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(double ScaleY, double TopLeft, double BottomLeft)> ReadScaleAndCornersAsync(
        string schemaName,
        byte[] data)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            WITH r AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_ScaleY(rast), ST_Value(rast, 1, 1, 1), ST_Value(rast, 1, 1, ST_Height(rast))
            FROM r
            """;
        inspect.Parameters.AddWithValue("data", data);
        await using var reader = await inspect.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
    }

    private async Task<(double HorizontalMid, double VerticalMid)> ReadBilinearMidpointsAsync(
        string schemaName,
        byte[] data)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            WITH r AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_Value(rast, 1, 2, 1), ST_Value(rast, 1, 1, 2)
            FROM r
            """;
        inspect.Parameters.AddWithValue("data", data);
        await using var reader = await inspect.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    private async Task<(bool NoDataIsNull, bool FirstIsNull, double FirstVal, double SecondVal)> ReadNoDataAndSamplesAsync(
        string schemaName,
        byte[] data)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            WITH r AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_BandNoDataValue(rast, 1) IS NULL, ST_Value(rast, 1, 1, 1, FALSE), ST_Value(rast, 1, 2, 1, FALSE)
            FROM r
            """;
        inspect.Parameters.AddWithValue("data", data);
        await using var reader = await inspect.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetBoolean(0), reader.IsDBNull(1), reader.GetDouble(1), reader.GetDouble(2));
    }

    private static byte[] CreateEnvelopeWkb((double MinX, double MinY, double MaxX, double MaxY) box)
    {
        var factory = new GeometryFactory();
        return new WKBWriter().Write(factory.CreatePolygon(
        [
            new Coordinate(box.MinX, box.MinY),
            new Coordinate(box.MaxX, box.MinY),
            new Coordinate(box.MaxX, box.MaxY),
            new Coordinate(box.MinX, box.MaxY),
            new Coordinate(box.MinX, box.MinY)
        ]));
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
