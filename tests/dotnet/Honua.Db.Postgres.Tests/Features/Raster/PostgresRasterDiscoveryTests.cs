// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterDiscoveryTests
{
    [IntegrationTest]
    public async Task GetPrimaryRasterInfoAsync_WithoutOptionalRasterExtension_ReturnsNull()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await using var dataSource = await StartDatabaseAsync(database);
        var store = CreateStore(dataSource);

        (await store.GetPrimaryRasterInfoAsync(2110)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task RasterLookups_WithoutOptionalRasterExtension_ReportNoRasters()
    {
        // ImageServer service info, legend, tile and exportImage read these; on a
        // feature-only install they answered 500 (relation raster_data does not exist).
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await using var dataSource = await StartDatabaseAsync(database);
        var store = CreateStore(dataSource);

        (await store.ListRastersAsync(2110)).Should().BeEmpty();
        (await store.QueryRastersAsync(2110, new RasterSelectionQuery { TimeStart = DateTimeOffset.UnixEpoch })).Should().BeEmpty();
        (await store.GetRasterInfoAsync(2110, 1)).Should().BeNull();
        var empty = await store.ExportEmptyExtentAsync(2110, EmptyExtentQuery());
        empty.Data.Should().BeEmpty();
    }

    private static RasterQuery EmptyExtentQuery() => new()
    {
        OutputFormat = RasterFormat.PNG,
        OutputWidth = 256,
        OutputHeight = 256,
        CoverClipExtent = true,
        ClipRegion = new RasterClipRegion
        {
            Geometry = new WKBWriter().Write(new GeometryFactory().ToGeometry(new Envelope(0, 1, 0, 1))),
            Srid = 3857,
        },
    };

    private static async Task<NpgsqlDataSource> StartDatabaseAsync(PostgreSqlContainer database)
    {
        await database.StartAsync();
        var dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        // Module readiness checks TCP inside the container. Observe real SQL
        // through the mapped client connection before testing raster discovery.
        await WaitForMappedDatabaseAsync(dataSource);
        return dataSource;
    }

    private static PostgresRasterStore CreateStore(NpgsqlDataSource dataSource)
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(async call => (DbConnection)await dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        return new PostgresRasterStore(provider, NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance, "honua");
    }

    private static async Task WaitForMappedDatabaseAsync(NpgsqlDataSource dataSource)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Exception? lastFailure = null;
        try
        {
            while (true)
            {
                try
                {
                    await using var connection = await dataSource.OpenConnectionAsync(deadline.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    await command.ExecuteScalarAsync(deadline.Token);
                    return;
                }
                catch (NpgsqlException exception) when (exception.IsTransient)
                {
                    lastFailure = exception;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The owned PostgreSQL test database did not accept SQL through its mapped connection within the setup deadline.",
                lastFailure ?? exception);
        }
    }

}
