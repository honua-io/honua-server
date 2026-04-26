// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
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
