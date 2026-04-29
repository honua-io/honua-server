// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterMapRendererTests(PostgresFixture fixture)
{
    private const int LayerId = 9001;

    [IntegrationTest]
    public async Task RenderCollectionMapAsync_WhenRasterTableIsMissing_ReturnsEmptyRasterResult()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterMapRendererTests));
        try
        {
            var renderer = new PostgresRasterMapRenderer(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterMapRenderer>.Instance,
                schemaName);

            var result = await renderer.RenderCollectionMapAsync(
                0,
                new MapRenderRequest
                {
                    BoundingBox = new[] { -180d, -90d, 180d, 90d },
                    BoundingBoxCrs = 4326,
                    Crs = 4326,
                    Width = 256,
                    Height = 256,
                    Format = RasterFormat.PNG
                });

            result.Data.Should().BeEmpty();
            result.ContentType.Should().Be("image/png");
            result.Width.Should().Be(256);
            result.Height.Should().Be(256);
            result.Srid.Should().Be(4326);
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
        int value)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                       '8BUI'::text,
                       @value,
                       NULL
                   ),
                   @acquisitionDate,
                   @createdAt;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("acquisitionDate", acquisitionDate.UtcDateTime);
        command.Parameters.AddWithValue("createdAt", acquisitionDate.UtcDateTime);
        await command.ExecuteNonQueryAsync();
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
