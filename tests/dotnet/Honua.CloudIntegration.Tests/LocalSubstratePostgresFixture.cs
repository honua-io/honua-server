// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Npgsql;
using Testcontainers.PostgreSql;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.CloudIntegration.Tests;

public sealed class LocalSubstratePostgresFixture : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6";

    private PostgreSqlContainer? _container;

    public bool Available { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public string ContainerId => _container?.Id ?? throw new InvalidOperationException("The PostgreSQL container is not available.");

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage(Image)
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            Available = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can contain container credentials and connection strings.
            Console.Error.WriteLine($"PostgreSQL fixture startup failed ({ex.GetType().Name}).");
            Available = false;
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    public async Task<string> CreateFreshDatabaseAsync(
        bool enablePostGis = false,
        bool enablePostGisRaster = false)
    {
        var databaseName = $"honua_ci_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
            // Each scenario owns a distinct short-lived database. Keeping a pool per database
            // retains idle server connections until pruning and can exhaust the shared container.
            Pooling = false
        }.ConnectionString;

        if (enablePostGis || enablePostGisRaster)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = enablePostGisRaster
                ? """
                    CREATE EXTENSION IF NOT EXISTS postgis;
                    CREATE EXTENSION IF NOT EXISTS postgis_raster;
                    """
                : "CREATE EXTENSION IF NOT EXISTS postgis;";
            await command.ExecuteNonQueryAsync();
        }

        return connectionString;
    }

    // A fresh PostGIS database with the real server core-schema migrations applied, for an in-process
    // server host whose durable stores (for example the deploy workflow-operation store) need a system
    // database. TestWebApplicationFactory sets HONUA_SKIP_MIGRATIONS, so the caller owns schema preparation.
    public async Task<string> CreateMigratedServerDatabaseAsync()
    {
        var connectionString = await CreateFreshDatabaseAsync(enablePostGis: true);
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"The server core-schema migrations failed on a fresh system database: {result.ErrorMessage}");
        }

        return connectionString;
    }

    public async Task<ExecResult> ExecInContainerAsync(IList<string> command)
    {
        var container = _container ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
        var result = await container.ExecAsync(command);
        return new ExecResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);
}
