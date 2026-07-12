// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

/// <summary>
/// Provider-backed numerical fixtures for the class-statistics pixel read
/// (<see cref="PostgresRasterStore.ReadClippedBandVectorsAsync"/>, #2662): seeds a two-band raster
/// with known per-pixel values, reads the aligned band vectors inside an AOI clip, and asserts the
/// class signature (count/mean/covariance) computed from them matches hand-computed values.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreClassStatisticsTests(PostgresFixture fixture)
{
    private const int LayerId = 2662;

    [IntegrationTest]
    public async Task ReadClippedBandVectors_FullAoi_ReturnsAlignedVectorsForSignature()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreClassStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertTwoBandSignatureRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            // Envelope covering the whole 2x2 raster (0..2, 0..2).
            var clip = await MakeEnvelopeAsync(schemaName, 0, 0, 2, 2);

            var vectors = await store.ReadClippedBandVectorsAsync(
                LayerId, [rasterId], RasterMergeStrategy.Newest, clip, 4326, bands: null, maxPixels: 4_000_000);

            vectors.ExceededPixelBudget.Should().BeFalse();
            vectors.Bands.Should().Equal(1, 2);
            vectors.Pixels.Should().HaveCount(4);

            // Band1 = {1,2,3,4}, Band2 = {2,4,6,8}; the pixel vectors pair them per grid cell.
            var signature = RasterClassStatisticsCalculator.Compute(1, "veg", vectors);
            signature.PixelCount.Should().Be(4);
            signature.Mean[0].Should().BeApproximately(2.5, 1e-9);
            signature.Mean[1].Should().BeApproximately(5.0, 1e-9);
            signature.Covariance[0][0].Should().BeApproximately(5.0 / 3.0, 1e-9);
            signature.Covariance[0][1].Should().BeApproximately(10.0 / 3.0, 1e-9);
            signature.Covariance[1][1].Should().BeApproximately(20.0 / 3.0, 1e-9);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ReadClippedBandVectors_PartialAoi_ReturnsOnlyPixelsInsideClip()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreClassStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertTwoBandSignatureRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            // Clip to the left column only (x 0..0.9): pixels (col1,row1) and (col1,row2).
            var clip = await MakeEnvelopeAsync(schemaName, 0, 0, 0.9, 2);

            var vectors = await store.ReadClippedBandVectorsAsync(
                LayerId, [rasterId], RasterMergeStrategy.Newest, clip, 4326, bands: null, maxPixels: 4_000_000);

            vectors.Pixels.Should().HaveCount(2);
            // Left column band1 values are 1 and 3, band2 values are 2 and 6.
            var band1 = vectors.Pixels.Select(p => p[0]).OrderBy(v => v).ToArray();
            var band2 = vectors.Pixels.Select(p => p[1]).OrderBy(v => v).ToArray();
            band1.Should().Equal(1.0, 3.0);
            band2.Should().Equal(2.0, 6.0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ReadClippedBandVectors_ExceedsBudget_RejectsWithoutMaterializing()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreClassStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertTwoBandSignatureRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            var clip = await MakeEnvelopeAsync(schemaName, 0, 0, 2, 2);

            // A budget of 1 pixel is smaller than the 2x2 clip bounding box (4 pixels).
            var vectors = await store.ReadClippedBandVectorsAsync(
                LayerId, [rasterId], RasterMergeStrategy.Newest, clip, 4326, bands: null, maxPixels: 1);

            vectors.ExceededPixelBudget.Should().BeTrue();
            vectors.Pixels.Should().BeEmpty();
            vectors.BoundingPixelCount.Should().Be(4);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ReadClippedBandVectors_BandSubset_ReturnsOnlyRequestedBand()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreClassStatisticsTests));
        try
        {
            await CreateRasterTablesAsync(schemaName);
            var rasterId = await InsertTwoBandSignatureRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            var clip = await MakeEnvelopeAsync(schemaName, 0, 0, 2, 2);

            var vectors = await store.ReadClippedBandVectorsAsync(
                LayerId, [rasterId], RasterMergeStrategy.Newest, clip, 4326, bands: [2], maxPixels: 4_000_000);

            vectors.Bands.Should().Equal(2);
            vectors.Pixels.Should().OnlyContain(p => p.Length == 1);
            vectors.Pixels.Select(p => p[0]).OrderBy(v => v).Should().Equal(2.0, 4.0, 6.0, 8.0);
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

    // A 2x2 two-band raster. Band1 rows = [[1,2],[3,4]], Band2 rows = [[2,4],[6,8]] so the paired
    // per-pixel vectors are (1,2), (2,4), (3,6), (4,8).
    private async Task<long> InsertTwoBandSignatureRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'signature',
                   ST_SetValues(
                       ST_SetValues(
                           ST_AddBand(
                               ST_AddBand(
                                   ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                                   '32BF'::text, 0, NULL),
                               '32BF'::text, 0, NULL),
                           1, 1, 1,
                           ARRAY[ARRAY[1::double precision, 2::double precision],
                                 ARRAY[3::double precision, 4::double precision]]),
                       2, 1, 1,
                       ARRAY[ARRAY[2::double precision, 4::double precision],
                             ARRAY[6::double precision, 8::double precision]]),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<byte[]> MakeEnvelopeAsync(string schemaName, double xmin, double ymin, double xmax, double ymax)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ST_AsBinary(ST_MakeEnvelope(@xmin, @ymin, @xmax, @ymax, 4326))";
        command.Parameters.AddWithValue("xmin", xmin);
        command.Parameters.AddWithValue("ymin", ymin);
        command.Parameters.AddWithValue("xmax", xmax);
        command.Parameters.AddWithValue("ymax", ymax);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

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
