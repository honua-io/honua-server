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
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed class PostgresRasterDiscoveryTests(ITestOutputHelper output)
{
    [IntegrationTest]
    public async Task GetPrimaryRasterInfoAsync_WithoutOptionalRasterExtension_ReturnsNull()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.GetConnectionString());
        // Module readiness checks TCP inside the container. Observe real SQL
        // through the mapped client connection before testing raster discovery.
        try
        {
            await WaitForMappedDatabaseAsync(dataSource);
        }
        catch
        {
            await WriteStartupDiagnosticsAsync(database);
            throw;
        }

        output.WriteLine($"Mapped SQL ready: container={database.Id}, host={database.Hostname}, port={database.GetMappedPublicPort(5432)}, state={database.State}");
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(async call => (DbConnection)await dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var store = new PostgresRasterStore(provider, NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance, "honua");

        (await store.GetPrimaryRasterInfoAsync(2110)).Should().BeNull();
    }

    private async Task WriteStartupDiagnosticsAsync(PostgreSqlContainer database)
    {
        // Retain evidence before await-using removes the owned container. Diagnostic
        // failures must not replace the original mapped SQL readiness failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            output.WriteLine($"Mapped SQL unavailable: container={database.Id}, host={database.Hostname}, port={database.GetMappedPublicPort(5432)}, reportedState={database.State}");
            var readiness = await database.ExecAsync(
                ["pg_isready", "--host", "127.0.0.1", "--dbname", "postgres", "--username", "postgres"], deadline.Token);
            output.WriteLine($"Container IPv4 readiness: exit={readiness.ExitCode}, stdout={readiness.Stdout}, stderr={readiness.Stderr}");
            var (stdout, stderr) = await database.GetLogsAsync(timestampsEnabled: true, ct: deadline.Token);
            const int maximumLogCharacters = 16384;
            output.WriteLine($"Container stdout (tail): {stdout[^Math.Min(stdout.Length, maximumLogCharacters)..]}");
            output.WriteLine($"Container stderr (tail): {stderr[^Math.Min(stderr.Length, maximumLogCharacters)..]}");
        }
        catch (Exception exception)
        {
            output.WriteLine($"Startup diagnostics unavailable: {exception.GetType().Name}: {exception.Message}");
        }
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
