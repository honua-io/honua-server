// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Testcontainers fixture that boots a single throwaway PostgreSQL container for the ADR-0060
/// migration-gate lane (#2166, #2457) and the canonical core-schema verification lane (#3899).
/// The PostGIS image lets the latter execute the real numbered migration roots; synthetic migration
/// gate scenarios continue to use plain PostgreSQL objects.
///
/// When Docker is unavailable the container fails to start, <see cref="Available"/> stays false, and the
/// dependent <c>[SkippableFact]</c> tests skip — mirroring <see cref="LocalStackFixture"/>.
/// </summary>
public sealed class LocalSubstratePostgresFixture : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6";

    private PostgreSqlContainer? _container;

    /// <summary>True when the PostgreSQL container started, so the migration-gate tests can run.</summary>
    public bool Available { get; private set; }

    /// <summary>Admin connection string to the container's default database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
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
            Available = false;
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>
    /// Creates a fresh, uniquely named database and returns a connection string targeting it. Each
    /// migration scenario runs against its own database so the DbUp journal and applied objects never
    /// cross-contaminate between tests.
    /// </summary>
    public async Task<string> CreateFreshDatabaseAsync(bool enableSpatialExtensions = false)
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
            Database = databaseName
        }.ConnectionString;

        if (enableSpatialExtensions)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE EXTENSION IF NOT EXISTS postgis;
                CREATE EXTENSION IF NOT EXISTS postgis_raster;
                """;
            await command.ExecuteNonQueryAsync();
        }

        return connectionString;
    }
}
