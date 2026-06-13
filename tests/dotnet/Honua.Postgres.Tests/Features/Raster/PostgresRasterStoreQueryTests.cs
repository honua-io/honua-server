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
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterStoreQueryTests(PostgresFixture fixture)
{
    private const int LayerId = 9002;

    [IntegrationTest]
    public async Task QueryRastersAsync_WithTimestampAndGeometry_UsesLayerSnapshotBeforeGeometryFilter()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName,
                "older-local",
                DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture),
                upperLeftX: 0,
                upperLeftY: 1);
            await InsertRasterAsync(
                schemaName,
                "newer-remote",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                upperLeftX: 10,
                upperLeftY: 1);

            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                schemaName);
            var localSelection = new RasterSelectionQuery
            {
                Geometry = CreateEnvelopeWkb(0, 0, 1, 1),
                GeometrySrid = 4326
            };

            var currentSelection = await store.QueryRastersAsync(LayerId, localSelection).ConfigureAwait(false);
            currentSelection.Should().ContainSingle().Which.Name.Should().Be("older-local");

            var temporalSelection = await store.QueryRastersAsync(
                LayerId,
                localSelection with
                {
                    Timestamp = DateTimeOffset.Parse("2024-04-01T00:00:00Z", CultureInfo.InvariantCulture)
                }).ConfigureAwait(false);

            temporalSelection.Should().BeEmpty(
                "the newest layer snapshot before the timestamp has no raster in the requested geometry");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithBandSelection_ExportsRequestedBandCount()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertMultiBandRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Bands = [2]
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();
            result.BandCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithMinMaxStretch_RescalesFloatRasterTo8Bit()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await CreateRasterStatisticsTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Stretch = new RasterStretch { StretchType = RasterStretchType.MinMax },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();

            // Re-import the exported GeoTIFF and confirm the stretch produced an 8-bit
            // band spanning the full 0..255 display range (0,10,20,30 -> 0,85,170,255).
            var (pixelType, min, max) = await SummarizeExportedRasterAsync(schemaName, result.Data);
            pixelType.Should().Be("8BUI");
            min.Should().Be(0);
            max.Should().Be(255);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetClippedStatisticsAsync_RestrictsAnalysisToAoiEnvelope()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                schemaName);

            // Raster pixels: x[0,1] holds 0 (top) and 20 (bottom); x[1,2] holds 10/30.
            // Clip to the left column only -> the analysis must see just {0, 20}.
            var leftColumn = CreateEnvelopeWkb(0, 0, 1, 2);

            var stats = await store.GetClippedStatisticsAsync(LayerId, rasterId, leftColumn, 4326)
                .ConfigureAwait(false);

            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(0);
            stats[0].MaxValue.Should().Be(20);
            stats[0].ValidPixelCount.Should().Be(2);

            var histograms = await store.GetClippedHistogramsAsync(LayerId, rasterId, leftColumn, 4326, binCount: 4)
                .ConfigureAwait(false);

            histograms.Should().ContainSingle();
            histograms[0].Counts.Sum().Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithColormap_ProducesRgbaImage()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await CreateRasterStatisticsTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Colormap = new RasterColormap
                        {
                            Entries =
                            [
                                new RasterColormapEntry(0, 0, 0, 0, 255),
                                new RasterColormapEntry(30, 255, 255, 255, 255),
                            ],
                        },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();

            // ST_ColorMap maps the single band to a 4-band RGBA image.
            var bandCount = await GetExportedBandCountAsync(schemaName, result.Data);
            bandCount.Should().Be(4);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task<int> GetExportedBandCountAsync(string schemaName, byte[] exportedRaster)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ST_NumBands(ST_FromGDALRaster(@data));";
        command.Parameters.AddWithValue("data", exportedRaster);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task CreateRasterStatisticsTableAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_statistics (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertFloatRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'float-stretch',
                   ST_SetValue(
                       ST_SetValue(
                           ST_SetValue(
                               ST_SetValue(
                                   ST_AddBand(
                                       ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                                       '32BF'::text, 0, NULL),
                                   1, 1, 1, 0::double precision),
                               1, 2, 1, 10::double precision),
                           1, 1, 2, 20::double precision),
                       1, 2, 2, 30::double precision),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<(string PixelType, double Min, double Max)> SummarizeExportedRasterAsync(
        string schemaName,
        byte[] exportedRaster)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_BandPixelType(rast, 1) AS pixel_type,
                   (stats).min AS min_value,
                   (stats).max AS max_value
            FROM (
                SELECT rast, ST_SummaryStats(rast, 1, false) AS stats
                FROM (SELECT ST_FromGDALRaster(@data) AS rast) decoded
            ) summarized;
            """;
        command.Parameters.AddWithValue("data", exportedRaster);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2));
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
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertRasterAsync(
        string schemaName,
        string name,
        DateTimeOffset acquisitionDate,
        double upperLeftX,
        double upperLeftY)
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
                       7,
                       NULL
                   ),
                   @acquisitionDate,
                   @createdAt;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("upperLeftY", upperLeftY);
        command.Parameters.AddWithValue("acquisitionDate", acquisitionDate.UtcDateTime);
        command.Parameters.AddWithValue("createdAt", acquisitionDate.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertMultiBandRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'multi-band',
                   ST_AddBand(
                       ST_AddBand(
                           ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                           '8BUI'::text,
                           10,
                           NULL
                       ),
                       '8BUI'::text,
                       20,
                       NULL
                   ),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        var factory = new GeometryFactory();
        return new WKBWriter().Write(factory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]));
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
