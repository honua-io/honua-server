// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Raster;

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
            result.ErrorMessage.Should().Be("Import failed.");
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
            result.ErrorMessage.Should().Be("Import failed.");
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
            result.ErrorMessage.Should().Be("Import failed.");
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
            result.ErrorMessage.Should().Be("Import failed.");
            (await CountLayerRastersAsync(schemaName)).Should().Be(2);
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ImportAsync_WhenMigration055Applied_PreservesExternalToastStorage()
    {
        // Regression guard for #1625: the monolithic raster payload must be stored EXTERNAL
        // (out-of-line, uncompressed) so dynamic tile/terrain/statistics reads fetch only the
        // chunks they touch instead of detoasting and decompressing the whole row. Migration
        // 055 owns that policy; the import path must consume it without issuing its own ALTER.
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);

            (await GetRasterColumnStorageAsync(schemaName)).Should().Be(
                'e',
                "the test fixture applies canonical migration 055 before ordinary writes");

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326));

            result.Success.Should().BeTrue();
            (await GetRasterColumnStorageAsync(schemaName)).Should().Be(
                'e',
                "an ordinary import must not replace the migration-owned storage policy");
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ImportAsync_WithZoomLevels_PreGeneratesTilesForIndexedLookups()
    {
        // Regression guard for #1625: importing with tile zoom levels must populate raster_tiles
        // so the hot tile-serving path is an indexed lookup rather than an ST_Clip over the
        // detoasted monolith. (The API import endpoint defaults to zooms 0..8.)
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = await CreatePngRasterFileAsync();
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);

            var service = CreateService(schemaName);
            var result = await service.ImportAsync(CreateRequest(filePath, srid: 4326, tileZoomLevels: [0, 1]));

            result.Success.Should().BeTrue();
            result.RasterId.Should().NotBeNull();
            result.TilesGenerated.Should().BeGreaterThan(0);
            (await CountTilesAsync(schemaName, result.RasterId!.Value)).Should().BeGreaterThan(0);

            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT zoom_level, ST_Width(ST_FromGDALRaster(tile_data)), ST_Height(ST_FromGDALRaster(tile_data)),
                       renderer_version, renderer_payload_sha256 = sha256(tile_data)
                FROM raster_tiles WHERE raster_data_id = @id;
                """;
            command.Parameters.AddWithValue("id", result.RasterId!.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var zooms = new HashSet<int>();
            while (await reader.ReadAsync())
            {
                zooms.Add(reader.GetInt32(0));
                reader.GetInt32(1).Should().Be(256, "cached PNG bytes must span the complete tile grid");
                reader.GetInt32(2).Should().Be(256);
                reader.GetInt32(3).Should().Be(RasterCorrectedNearestSql.RendererVersion,
                    "the importer uses the corrected nearest-grid sampler");
                reader.GetBoolean(4).Should().BeTrue("the stored digest must bind the exact generated PNG bytes");
            }
            zooms.Should().BeEquivalentTo(new[] { 0, 1 });
        }
        finally
        {
            TryDeleteFile(filePath);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ImportAsync_NumericPngOpacity_UsesValidityAndRetainsScientificSource(int bands)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterImportServiceTests));
        var filePath = Path.Join(Path.GetTempPath(), $"honua-numeric-opacity-{Guid.NewGuid():N}.tif");
        try
        {
            await CreateRasterImportSchemaAsync(schemaName);
            await InsertLayerAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH first_band AS (
                    SELECT ST_SetValues(ST_AddBand(
                        ST_MakeEmptyRaster(2, 2, 1000, 3000, 1000, -1000, 0, 0, 3857),
                        '32BF'::text, -9999, -9999), 1, 1, 1,
                        ARRAY[[-17.25, 101], [-9999, 0]]::double precision[][]) AS rast
                )
                SELECT ST_AsGDALRaster(CASE WHEN @bands = 1 THEN rast ELSE
                    ST_SetValues(ST_AddBand(rast, '32BF'::text, -9999, -9999), 2, 1, 1,
                        ARRAY[[-9999, -4.5], [600, 1100]]::double precision[][])
                    END, 'GTiff') FROM first_band;
                """;
            command.Parameters.AddWithValue("bands", bands);
            await File.WriteAllBytesAsync(filePath, (byte[])(await command.ExecuteScalarAsync())!);
            var result = await CreateService(schemaName).ImportAsync(new RasterImportRequest
            {
                LayerId = LayerId,
                Name = "numeric-opacity",
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Format = SupportedRasterFormat.GeoTiff,
                TileZoomLevels = [10]
            });
            result.Success.Should().BeTrue(result.ErrorMessage);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("id", result.RasterId!.Value);
            command.CommandText = """
                WITH image AS (
                    SELECT ST_FromGDALRaster(tile_data) AS rast, renderer_version,
                           renderer_payload_sha256 = sha256(tile_data) AS digest_ok
                    FROM raster_tiles WHERE raster_data_id = @id
                      AND zoom_level = 10 AND tile_x = 512 AND tile_y = 511
                ), reference AS (
                    SELECT ST_MakeEmptyRaster(256, 256, 0, 40075016.68557849 / 1024,
                        40075016.68557849 / 1024 / 256, -40075016.68557849 / 1024 / 256,
                        0, 0, 3857) AS rast
                )
                SELECT renderer_version, digest_ok,
                    ST_Value(i.rast, 1, ST_WorldToRasterCoordX(r.rast, x), ST_WorldToRasterCoordY(r.rast, y), FALSE),
                    ST_Value(i.rast, ST_NumBands(i.rast), ST_WorldToRasterCoordX(r.rast, x), ST_WorldToRasterCoordY(r.rast, y), FALSE),
                    expected_gray, expected_alpha
                FROM image i, reference r, (VALUES
                    (1500.0, 2500.0, 0.0, 255.0), (2500.0, 2500.0, 255.0, 255.0),
                    (1500.0, 1500.0, 0.0, 0.0), (2500.0, 1500.0, 37.0, 255.0)
                ) v(x, y, expected_gray, expected_alpha);
                """;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var count = 0;
                while (await reader.ReadAsync())
                {
                    count++;
                    reader.GetInt32(0).Should().Be(RasterCorrectedNearestSql.RendererVersion);
                    reader.GetBoolean(1).Should().BeTrue();
                    reader.GetDouble(2).Should().Be((double)reader.GetDecimal(4));
                    reader.GetDouble(3).Should().Be((double)reader.GetDecimal(5));
                }
                count.Should().Be(4);
            }
            command.CommandText = """
                SELECT ST_NumBands(raster), ST_Value(raster, 1, 1, 1),
                       CASE WHEN ST_NumBands(raster) = 2 THEN ST_Value(raster, 2, 1, 2) ELSE NULL END
                FROM raster_data WHERE id = @id;
                """;
            await using var source = await command.ExecuteReaderAsync();
            (await source.ReadAsync()).Should().BeTrue();
            source.GetInt32(0).Should().Be(bands);
            source.GetDouble(1).Should().Be(-17.25);
            if (bands == 2) source.GetDouble(2).Should().Be(600);
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
            FixtureBypassDatabaseSchemaGuard.Instance,
            schemaName);
    }

    private static RasterImportRequest CreateRequest(string filePath, int srid, int[]? tileZoomLevels = null)
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
            TileZoomLevels = tileZoomLevels ?? []
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
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED,
                second_band_is_alpha BOOLEAN NOT NULL DEFAULT FALSE
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
                renderer_version INTEGER NOT NULL DEFAULT 0,
                renderer_payload_sha256 BYTEA,
                content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
            );
            """;
        await command.ExecuteNonQueryAsync();
        await CoreMigrationTestFixture.ApplyRasterExternalStorageAsync(fixture, schemaName);
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

    private async Task<char> GetRasterColumnStorageAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        // pg_attribute.attstorage exposes the TOAST strategy: 'p' plain, 'm' main,
        // 'e' external, 'x' extended. Resolve raster_data within the test's search_path.
        command.CommandText = """
            SELECT a.attstorage
            FROM pg_attribute a
            WHERE a.attrelid = 'raster_data'::regclass
              AND a.attname = 'raster';
            """;

        return (char)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountTilesAsync(string schemaName, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raster_tiles WHERE raster_data_id = @rasterId;";
        command.Parameters.AddWithValue("rasterId", rasterId);

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
        // The filename segment is a fixed literal + GUID token and can never be rooted, so
        // Path.Combine cannot drop the temp-path segment here (cs/path-combine false positive).
        var filePath = Path.Join(Path.GetTempPath(), $"honua-raster-import-{Guid.NewGuid():N}.png");
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
            // Best-effort temp-file cleanup: another process (e.g. an antivirus scanner or a
            // still-open handle) may transiently hold the file. Leaving it behind does not fail
            // the test, so the failure is intentionally swallowed here.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp-file cleanup: permissions races on the OS temp directory should
            // not fail the test. Intentionally swallowed.
        }
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
