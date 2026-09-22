// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// Regression for honua-server#4792: every mosaic operation unions the layer's rasters, and
/// PostGIS <c>ST_Union</c> rejects inputs that do not share one pixel grid
/// (<c>rt_raster_from_two_rasters: The two rasters provided do not have the same alignment</c>).
/// A layer that mixes native resolutions or grid origins therefore failed service statistics,
/// histograms, exportImage, identify and the clipped analysis paths.
/// </summary>
/// <remarks>
/// <para>
/// Nested fixture (exact expectations): <c>coarse</c> is 2x2 at 1.0 over x[0,2] y[0,2], value 20,
/// acquired first; <c>fine</c> is 2x2 at 0.5 over x[1,2] y[1,2], value 5, acquired later. The fine
/// raster has the smaller pixel area, so it is the reference grid; the coarse raster lands on it
/// as a 4x4 block of 20 (every fine grid line falls on a coarse pixel edge, so nearest-neighbour
/// is exact). The newest-wins union is therefore 16 pixels at 0.5: four of 5 (the fine raster's
/// quadrant x[1,2] y[1,2]) and twelve of 20, so min 5, max 20, mean (4*5 + 12*20) / 16 = 16.25.
/// </para>
/// <para>
/// Off-grid fixture: two disjoint 2x2 rasters at 1.0 whose origins are half a pixel apart, the
/// issue's "origins off a common grid" shape. Pixel counts are not asserted there: which boundary
/// pixel a half-pixel snap keeps is GDAL's nearest-neighbour edge rule, not a Honua contract. What
/// is asserted is that no fill value reaches the mosaic — each raster still reports its own value
/// and the range stays [20, 40].
/// </para>
/// </remarks>
[Collection("Database")]
public sealed class PostgresRasterStoreUnalignedMosaicTests(PostgresFixture fixture)
{
    private const int LayerId = 4792;

    [IntegrationTest]
    public async Task GetMosaicStatisticsAsync_WithMixedResolutionRasters_ReturnsStatisticsOfTheAlignedUnion()
    {
        await WithNestedMosaicAsync(async (store, ids) =>
        {
            var stats = await store.GetMosaicStatisticsAsync(LayerId, ids, RasterMergeStrategy.Newest);

            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(5);
            stats[0].MaxValue.Should().Be(20);
            stats[0].MeanValue.Should().BeApproximately(16.25, 1e-9);
            stats[0].ValidPixelCount.Should().Be(16);
        });
    }

    [IntegrationTest]
    public async Task GetMosaicHistogramsAsync_WithMixedResolutionRasters_CountsEveryAlignedPixel()
    {
        await WithNestedMosaicAsync(async (store, ids) =>
        {
            var histograms = await store.GetMosaicHistogramsAsync(LayerId, ids, RasterMergeStrategy.Newest, binCount: 3);

            histograms.Should().ContainSingle();
            histograms[0].Counts.Sum().Should().Be(16);
            histograms[0].Counts.First().Should().Be(4, "the fine raster's four pixels hold the minimum");
            histograms[0].Counts.Last().Should().Be(12, "the coarse raster fills the other twelve");
        });
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_WithMixedResolutionRasters_ExportsNewestValuesAtTheFinestResolution()
    {
        await WithNestedMosaicAsync(async (store, ids) =>
        {
            var result = await store.ExportMosaicAsync(
                LayerId, ids, RasterMergeStrategy.Newest, new RasterQuery { OutputFormat = RasterFormat.TIFF });

            result.Data.Should().NotBeEmpty();
            var probe = await ProbeAsync(result.Data, (1.25, 1.75), (0.25, 0.25), (1.75, 0.25));
            probe.Width.Should().Be(4, "the mosaic keeps the finest native pixel size (0.5) across x[0,2]");
            probe.Height.Should().Be(4);
            probe.Values.Should().Equal(5, 20, 20);
        });
    }

    [IntegrationTest]
    public async Task IdentifyMosaicAsync_WithMixedResolutionRasters_ReturnsTheNewestRasterValue()
    {
        await WithNestedMosaicAsync(async (store, ids) =>
        {
            var inFine = await store.IdentifyMosaicAsync(LayerId, ids, RasterMergeStrategy.Newest, 1.25, 1.75, 4326);
            var coarseOnly = await store.IdentifyMosaicAsync(LayerId, ids, RasterMergeStrategy.Newest, 0.25, 0.25, 4326);

            Band1(inFine).Should().Be(5);
            Band1(coarseOnly).Should().Be(20);
        });
    }

    [IntegrationTest]
    public async Task GetClippedMosaicStatisticsAndHistogramsAsync_WithMixedResolutionRasters_AnalyseTheAlignedUnion()
    {
        await WithNestedMosaicAsync(async (store, ids) =>
        {
            // The right half x[1,2] holds the fine quadrant (four 5s) above four coarse 20s.
            var rightHalf = await MakeEnvelopeAsync(1, 0, 2, 2);

            var stats = await store.GetClippedMosaicStatisticsAsync(
                LayerId, ids, RasterMergeStrategy.Newest, rightHalf, 4326);
            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(5);
            stats[0].MaxValue.Should().Be(20);
            stats[0].ValidPixelCount.Should().Be(8);

            var histograms = await store.GetClippedMosaicHistogramsAsync(
                LayerId, ids, RasterMergeStrategy.Newest, rightHalf, 4326, binCount: 2);
            histograms.Should().ContainSingle();
            histograms[0].Counts.Should().Equal(4L, 4L);
        });
    }

    [IntegrationTest]
    public async Task MosaicOperations_WithRastersOffACommonGridOrigin_DoNotFail()
    {
        var schemaName = await CreateSchemaAsync();
        try
        {
            var west = await InsertConstantRasterAsync(schemaName, "west", 0, 2, 1.0, 20, Day(1));
            var shifted = await InsertConstantRasterAsync(schemaName, "shifted", 2.5, 2, 1.0, 40, Day(2));
            long[] ids = [west, shifted];
            var store = CreateStore(schemaName);

            var stats = await store.GetMosaicStatisticsAsync(LayerId, ids, RasterMergeStrategy.Newest);
            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(20);
            stats[0].MaxValue.Should().Be(40);

            var export = await store.ExportMosaicAsync(
                LayerId, ids, RasterMergeStrategy.Newest, new RasterQuery { OutputFormat = RasterFormat.TIFF });
            export.Data.Should().NotBeEmpty();

            Band1(await store.IdentifyMosaicAsync(LayerId, ids, RasterMergeStrategy.Newest, 0.5, 1.5, 4326))
                .Should().Be(20);
            Band1(await store.IdentifyMosaicAsync(LayerId, ids, RasterMergeStrategy.Newest, 3.5, 1.5, 4326))
                .Should().Be(40, "the snapped raster keeps its own values, and the fill margin is NoData");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetMosaicStatisticsAsync_WithAlignedRasters_IsUnchangedByTheAlignmentStep()
    {
        var schemaName = await CreateSchemaAsync();
        try
        {
            // Two aligned 2x2 rasters side by side: every input already shares the reference
            // grid, so the union and its statistics are exactly the pre-#4792 result.
            var west = await InsertConstantRasterAsync(schemaName, "west", 0, 2, 1.0, 10, Day(1));
            var east = await InsertConstantRasterAsync(schemaName, "east", 2, 2, 1.0, 30, Day(2));
            var store = CreateStore(schemaName);

            var stats = await store.GetMosaicStatisticsAsync(LayerId, [west, east], RasterMergeStrategy.Newest);

            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(10);
            stats[0].MaxValue.Should().Be(30);
            stats[0].MeanValue.Should().Be(20);
            stats[0].ValidPixelCount.Should().Be(8);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task WithNestedMosaicAsync(Func<PostgresRasterStore, long[], Task> assert)
    {
        var schemaName = await CreateSchemaAsync();
        try
        {
            var coarse = await InsertConstantRasterAsync(schemaName, "coarse", 0, 2, 1.0, 20, Day(1));
            var fine = await InsertConstantRasterAsync(schemaName, "fine", 1, 2, 0.5, 5, Day(2));
            _schemaName = schemaName;
            await assert(CreateStore(schemaName), [coarse, fine]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private string? _schemaName;

    private static double Band1(PixelValueResult result)
    {
        result.HasData.Should().BeTrue();
        return Convert.ToDouble(result.BandValues[1], CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Day(int day) => new(2024, 1, day, 0, 0, 0, TimeSpan.Zero);

    private PostgresRasterStore CreateStore(string schemaName)
        => new(
            new FixtureConnectionProvider(fixture.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance,
            schemaName);

    private async Task<string> CreateSchemaAsync()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreUnalignedMosaicTests));
        await using (var connection = await fixture.GetConnectionAsync(schemaName))
        await using (var command = connection.CreateCommand())
        {
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

        await CoreMigrationTestFixture.ApplyRasterLayerStatisticsAsync(fixture, schemaName);
        return schemaName;
    }

    // A square-pixel 2x2 constant raster whose upper-left corner is (upperLeftX, upperLeftY).
    private async Task<long> InsertConstantRasterAsync(
        string schemaName,
        string name,
        double upperLeftX,
        double upperLeftY,
        double pixelSize,
        double value,
        DateTimeOffset acquisition)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(2, 2, @upperLeftX, @upperLeftY, @pixelSize, -@pixelSize, 0, 0, 4326),
                       '32BF'::text,
                       @value,
                       NULL
                   ),
                   @acquisition,
                   @acquisition
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("upperLeftY", upperLeftY);
        command.Parameters.AddWithValue("pixelSize", pixelSize);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("acquisition", acquisition.UtcDateTime);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<byte[]> MakeEnvelopeAsync(double xmin, double ymin, double xmax, double ymax)
    {
        await using var connection = await fixture.GetConnectionAsync(_schemaName!);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ST_AsBinary(ST_MakeEnvelope(@xmin, @ymin, @xmax, @ymax, 4326))";
        command.Parameters.AddWithValue("xmin", xmin);
        command.Parameters.AddWithValue("ymin", ymin);
        command.Parameters.AddWithValue("xmax", xmax);
        command.Parameters.AddWithValue("ymax", ymax);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private sealed record RasterProbe(int Width, int Height, double[] Values);

    private async Task<RasterProbe> ProbeAsync(byte[] exported, params (double X, double Y)[] points)
    {
        await using var connection = await fixture.GetConnectionAsync(_schemaName!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH r AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_Width(rast),
                   ST_Height(rast),
                   ARRAY(SELECT ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(p.x, p.y), ST_SRID(rast)))
                         FROM unnest(@xs, @ys) WITH ORDINALITY AS p(x, y, ord)
                         ORDER BY p.ord)
            FROM r;
            """;
        command.Parameters.AddWithValue("data", exported);
        command.Parameters.AddWithValue("xs", points.Select(p => p.X).ToArray());
        command.Parameters.AddWithValue("ys", points.Select(p => p.Y).ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new RasterProbe(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetFieldValue<double[]>(2));
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
