// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using Testcontainers.PostgreSql;
using DotNet.Testcontainers.Containers;
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

    /// <summary>
    /// The running container's id, so a caller can shell out to <c>docker exec</c> against it (e.g. to
    /// run the real <c>pg_dump</c>/<c>pg_restore</c> client tools bundled in the image) from a
    /// <c>MigrationSafetyOptions.BackupCommand</c> executed on the host.
    /// </summary>
    public string ContainerId => _container?.Id ?? throw new InvalidOperationException("The PostgreSQL container is not available.");

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
            Database = databaseName
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

    /// <summary>
    /// Runs a command inside the running PostgreSQL container (e.g. the real <c>pg_dump</c>/
    /// <c>pg_restore</c> client tools bundled in the <c>postgis/postgis</c> image), so a migration
    /// safety test can prove a backup hook produces a genuinely restorable dump without requiring
    /// PostgreSQL client tools on the test-runner host itself.
    /// </summary>
    public async Task<ExecResult> ExecInContainerAsync(IList<string> command)
    {
        var container = _container ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
        var result = await container.ExecAsync(command);
        return new ExecResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
    }

    public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);
}
