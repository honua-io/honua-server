// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
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
        await database.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        // Module readiness checks TCP inside the container. Observe real SQL
        // through the mapped client connection before testing raster discovery.
        await WaitForMappedDatabaseAsync(dataSource);
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(async call => (DbConnection)await dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var store = new PostgresRasterStore(provider, NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance, "honua");

        (await store.GetPrimaryRasterInfoAsync(2110)).Should().BeNull();
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
