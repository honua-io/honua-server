// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterImportServiceTests(PostgresFixture fixture)
{
    private const int LayerId = 522;
    private const string WorldFileContent = """
        1
        0
        0
        -1
        0.5
        0.5
        """;

    [IntegrationTest]
    public async Task ImportAsync_WithMismatchedLayerSrid_ReturnsFailureAndRollsBack()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);
            await InsertConstantRasterAsync(schemaName, "existing-3857", srid: 3857, bandCount: 1);

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("requires raster homogeneity");
            result.ErrorMessage.Should().Contain("Expected SRID=3857");
            result.ErrorMessage.Should().Contain("upload has SRID=4326");
            (await CountLayerRastersAsync(schemaName)).Should().Be(1);
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ImportAsync_WithMismatchedLayerBandCount_ReturnsFailureAndRollsBack()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);
            await InsertConstantRasterAsync(schemaName, "existing-two-band", srid: 4326, bandCount: 2);

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("requires raster homogeneity");
            result.ErrorMessage.Should().Contain("Expected SRID=4326, BandCount=2");
            result.ErrorMessage.Should().Contain("upload has SRID=4326, BandCount=1");
            (await CountLayerRastersAsync(schemaName)).Should().Be(1);
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ImportAsync_WhenLayerAlreadyContainsHeterogeneousRasters_ReturnsFailureAndRollsBack()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);
            await InsertConstantRasterAsync(schemaName, "existing-4326", srid: 4326, bandCount: 1);
            await InsertConstantRasterAsync(schemaName, "existing-3857", srid: 3857, bandCount: 1);

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("already contains heterogeneous rasters");
            result.ErrorMessage.Should().Contain("SRID range 3857..4326");
            (await CountLayerRastersAsync(schemaName)).Should().Be(2);
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ImportAsync_WhenLayerAlreadyContainsHeterogeneousBandCounts_ReturnsFailureAndRollsBack()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);
            await InsertConstantRasterAsync(schemaName, "existing-one-band", srid: 4326, bandCount: 1);
            await InsertConstantRasterAsync(schemaName, "existing-two-band", srid: 4326, bandCount: 2);

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326));

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("already contains heterogeneous rasters");
            result.ErrorMessage.Should().Contain("BandCount range 1..2");
            (await CountLayerRastersAsync(schemaName)).Should().Be(2);
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private PostgresRasterImportService CreateService(string schemaName)
    {
        var crsDetectionService = Substitute.For<ICrsDetectionService>();
        return new PostgresRasterImportService(
            new FixtureConnectionProvider(fixture.DataSource),
            crsDetectionService,
            NullLogger<PostgresRasterImportService>.Instance,
            schemaName);
    }

    private static RasterImportRequest CreateRequest(string filePath, int srid)
    {
        return new RasterImportRequest
        {
            LayerId = LayerId,
            Name = "incoming",
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Format = SupportedRasterFormat.PngWorldFile,
            Srid = srid,
            WorldFileContent = WorldFileContent,
            TileZoomLevels = []
        };
    }

    private async Task CreateRasterImportSchemaAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS layers (
                layer_id INTEGER PRIMARY KEY,
                layer_name TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL REFERENCES layers(layer_id) ON DELETE CASCADE,
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

            CREATE TABLE IF NOT EXISTS raster_tiles (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL REFERENCES raster_data(id) ON DELETE CASCADE,
                zoom_level INTEGER NOT NULL,
                tile_x INTEGER NOT NULL,
                tile_y INTEGER NOT NULL,
                tile_data BYTEA NOT NULL,
                content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertLayerAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO layers (layer_id, layer_name)
            VALUES (@layerId, 'Raster import homogeneity test')
            ON CONFLICT (layer_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertConstantRasterAsync(
        string schemaName,
        string name,
        int srid,
        int bandCount)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO raster_data (layer_id, name, raster)
            SELECT @layerId, @name, {BuildConstantRasterSql(bandCount)};
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("srid", srid);
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildConstantRasterSql(int bandCount)
    {
        return bandCount switch
        {
            1 => """
                 ST_AddBand(
                     ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, @srid),
                     '8BUI'::text,
                     7,
                     NULL
                 )
                 """,
            2 => """
                 ST_AddBand(
                     ST_AddBand(
                         ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, @srid),
                         '8BUI'::text,
                         7,
                         NULL
                     ),
                     '8BUI'::text,
                     9,
                     NULL
                 )
                 """,
            _ => throw new ArgumentOutOfRangeException(nameof(bandCount), bandCount, "Only one- and two-band test rasters are supported.")
        };
    }

    private async Task<long> CountLayerRastersAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raster_data WHERE layer_id = @layerId;";
        command.Parameters.AddWithValue("layerId", LayerId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> CreatePngRasterFileAsync()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_AsGDALRaster(
                ST_AddBand(
                    ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text,
                    5,
                    NULL
                ),
                'PNG'
            );
            """;

        var bytes = (byte[])(await command.ExecuteScalarAsync())!;
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-raster-import-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(filePath, bytes);
        return filePath;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FixtureConnectionProvider(NpgsqlDataSource dataSource) : IDatabaseConnectionProvider
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
