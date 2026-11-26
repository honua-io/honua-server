// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterMapRendererTests(PostgresFixture fixture)
{
    private const int LayerId = 9001;

    [IntegrationTest]
    public async Task RenderCollectionMapAsync_WhenRasterTableIsMissingWithRasterStorageInstalled_Fails()
    {
        // The shared fixture has postgis_raster, so a missing raster table is a broken schema.
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            var renderer = new PostgresRasterMapRenderer(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterMapRenderer>.Instance,
                schemaName);

            var render = () => renderer.RenderCollectionMapAsync(0, WorldPngRequest());

            (await render.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RenderCollectionMapAsync_WithInstantUpperBound_SelectsNewestRasterBeforeTimestamp()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName,
                "february",
                DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture),
                11);
            await InsertRasterAsync(
                schemaName,
                "march",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                22);

            var renderer = new PostgresRasterMapRenderer(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterMapRenderer>.Instance,
                schemaName);

            var result = await renderer.RenderCollectionMapAsync(
                LayerId,
                new MapRenderRequest
                {
                    BoundingBox = new[] { 0d, 0d, 1d, 1d },
                    BoundingBoxCrs = 4326,
                    Crs = 4326,
                    Width = 1,
                    Height = 1,
                    Format = RasterFormat.PNG,
                    DateTime = DateTimeOffset.Parse("2024-02-15T00:00:00Z", CultureInfo.InvariantCulture),
                    DateTimeFrom = null
                });

            result.Data.Should().NotBeEmpty();
            result.ContentType.Should().Be("image/png");
            result.Width.Should().Be(1);
            result.Height.Should().Be(1);
            result.Srid.Should().Be(4326);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RenderCollectionMapAsync_WithDatetimeAndBbox_UsesUnwindowedLayerSnapshot()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName,
                "older-local",
                DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture),
                11,
                upperLeftX: 0,
                upperLeftY: 1);
            await InsertRasterAsync(
                schemaName,
                "newer-remote",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                22,
                upperLeftX: 10,
                upperLeftY: 1);

            var renderer = new PostgresRasterMapRenderer(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterMapRenderer>.Instance,
                schemaName);

            var result = await renderer.RenderCollectionMapAsync(
                LayerId,
                new MapRenderRequest
                {
                    BoundingBox = new[] { 0d, 0d, 1d, 1d },
                    BoundingBoxCrs = 4326,
                    Crs = 4326,
                    Width = 1,
                    Height = 1,
                    Format = RasterFormat.PNG,
                    DateTime = DateTimeOffset.Parse("2024-04-01T00:00:00Z", CultureInfo.InvariantCulture),
                    DateTimeFrom = null
                });

            result.Data.Should().BeEmpty(
                "the newest layer snapshot before the datetime has no raster in the requested bbox");
            result.ContentType.Should().Be("image/png");
            result.Width.Should().Be(1);
            result.Height.Should().Be(1);
            result.Srid.Should().Be(4326);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RenderDatasetMapAsync_WithMisalignedRasterLayers_ReturnsRenderedMap()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName,
                "aligned-layer",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                11,
                layerId: LayerId,
                upperLeftX: 0,
                upperLeftY: 1);
            await InsertRasterAsync(
                schemaName,
                "offset-layer",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                22,
                layerId: LayerId + 1,
                upperLeftX: 0.25,
                upperLeftY: 1);

            var renderer = new PostgresRasterMapRenderer(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterMapRenderer>.Instance,
                schemaName);

            var result = await renderer.RenderDatasetMapAsync(
                [LayerId, LayerId + 1],
                new MapRenderRequest
                {
                    BoundingBox = [0d, 0d, 1.25d, 1d],
                    BoundingBoxCrs = 4326,
                    Crs = 4326,
                    Width = 64,
                    Height = 64,
                    Format = RasterFormat.PNG
                });

            result.Data.Should().NotBeEmpty(
                "dataset maps must normalize heterogeneous source grids before mosaicking (#2487)");
            result.ContentType.Should().Be("image/png");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RenderCollectionMapAsync_WithPartialOverlapBbox_FramesOntoRequestedExtentWithoutStretching()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName, "left-half", DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                200, layerId: LayerId, upperLeftX: 0, upperLeftY: 1);

            var result = await CreateRenderer(schemaName).RenderCollectionMapAsync(
                LayerId,
                new MapRenderRequest
                {
                    BoundingBox = [0d, 0d, 2d, 1d],
                    BoundingBoxCrs = 4326,
                    Crs = 4326,
                    Width = 2,
                    Height = 1,
                    Format = RasterFormat.TIFF
                });

            result.Data.Should().NotBeEmpty();
            result.Extent.Should().Be(new RasterExtent { XMin = 0d, YMin = 0d, XMax = 2d, YMax = 1d, Srid = 4326 });

            var (leftVal, rightIsNull, width, height) = await InspectTwoByOneGdalRasterAsync(schemaName, result.Data);
            leftVal.Should().Be(200d);
            rightIsNull.Should().BeTrue("uncovered half of bbox must be NoData rather than stretched raster pixels");
            width.Should().Be(2);
            height.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RenderDatasetMapAsync_PreservesRequestedLayerIdsZOrder()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var newerLayer = LayerId;
            var olderLayer = LayerId + 1;
            await InsertRasterAsync(
                schemaName, "newer-layer", DateTimeOffset.Parse("2024-06-01T00:00:00Z", CultureInfo.InvariantCulture),
                111, layerId: newerLayer);
            await InsertRasterAsync(
                schemaName, "older-layer", DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                222, layerId: olderLayer);

            var renderer = CreateRenderer(schemaName);
            var request = new MapRenderRequest
            {
                BoundingBox = [0d, 0d, 1d, 1d],
                BoundingBoxCrs = 4326,
                Crs = 4326,
                Width = 1,
                Height = 1,
                Format = RasterFormat.TIFF
            };

            var olderOnTop = await renderer.RenderDatasetMapAsync([newerLayer, olderLayer], request);
            var newerOnTop = await renderer.RenderDatasetMapAsync([olderLayer, newerLayer], request);

            (await ReadTopLeftPixelAsync(schemaName, olderOnTop.Data))
                .Should().Be(222d, "second layer in layerIds must paint on top regardless of older acquisition date");
            (await ReadTopLeftPixelAsync(schemaName, newerOnTop.Data))
                .Should().Be(111d, "reversing layerIds must reverse compositing z-order");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private PostgresRasterMapRenderer CreateRenderer(string schemaName)
        => new(new FixtureConnectionProvider(fixture.DataSource), NullLogger<PostgresRasterMapRenderer>.Instance, schemaName);

    private async Task<(double LeftValue, bool RightIsNull, int Width, int Height)> InspectTwoByOneGdalRasterAsync(
        string schemaName,
        byte[] data)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            WITH r AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_Value(rast, 1, 1, 1), ST_Value(rast, 1, 2, 1), ST_Width(rast), ST_Height(rast)
            FROM r
            """;
        inspect.Parameters.AddWithValue("data", data);
        await using var reader = await inspect.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetDouble(0), reader.IsDBNull(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private async Task<double> ReadTopLeftPixelAsync(string schemaName, byte[] data)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ST_Value(ST_FromGDALRaster(@data), 1, 1, 1)";
        command.Parameters.AddWithValue("data", data);
        return Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task CreateRasterTableAsync(string schemaName)
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
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertRasterAsync(
        string schemaName,
        string name,
        DateTimeOffset acquisitionDate,
        int value,
        int layerId = LayerId,
        double upperLeftX = 0,
        double upperLeftY = 1)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, 1, -1, 0, 0, 4326),
                       '8BUI'::text,
                       @value,
                       NULL
                   ),
                   @acquisitionDate,
                   @createdAt;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("upperLeftY", upperLeftY);
        command.Parameters.AddWithValue("acquisitionDate", acquisitionDate.UtcDateTime);
        command.Parameters.AddWithValue("createdAt", acquisitionDate.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }

    internal sealed class FixtureConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
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

    internal static MapRenderRequest WorldPngRequest() => new()
    {
        BoundingBox = new[] { -180d, -90d, 180d, 90d },
        BoundingBoxCrs = 4326,
        Crs = 4326,
        Width = 256,
        Height = 256,
        Format = RasterFormat.PNG
    };
}
