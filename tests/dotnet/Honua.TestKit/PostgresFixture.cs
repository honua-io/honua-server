// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net.Sockets;
using Honua.TestKit.Seeding;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared PostgreSQL + PostGIS fixture for integration tests.
/// Uses Testcontainers to manage container lifecycle.
/// Supports schema-based test isolation for parallel execution.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly ConcurrentDictionary<string, int> _schemaCounters = new();
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static PostgreSqlContainer? _sharedContainer;
    private static NpgsqlDataSource? _sharedDataSource;
    private static string? _sharedConnectionString;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;
    private const string ExternalConnectionStringEnv = "HONUA_TEST_DB_URL";
    private const string SeedPathEnv = "HONUA_TEST_DB_SEED_PATH";
    private const string SeedProfileEnv = "HONUA_TEST_DB_SEED_PROFILE";
    private const int DropSchemaCommandTimeoutSeconds = 30;
    private const int DropSchemaMaxAttempts = 3;
    private const int InitializationMaxAttempts = 5;
    private string? _connectionString;

    public PostgresFixture()
    {
    }

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                try
                {
                    var externalConnectionString = Environment.GetEnvironmentVariable(ExternalConnectionStringEnv);
                    if (string.IsNullOrWhiteSpace(externalConnectionString))
                    {
                        _sharedContainer = new PostgreSqlBuilder()
                            .WithImage("postgis/postgis:18-3.6")
                            .WithDatabase("honua_test")
                            .WithUsername("test")
                            .WithPassword("test")
                            .WithEnvironment("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
                            .WithCommand("-c", "max_connections=200")
                            .Build();
                        await _sharedContainer.StartAsync();
                        _sharedConnectionString = _sharedContainer.GetConnectionString();
                    }
                    else
                    {
                        _sharedConnectionString = externalConnectionString;
                    }

                    _sharedDataSource = NpgsqlDataSource.Create(_sharedConnectionString);

                    await ExecuteWithInitializationRetryAsync(async () =>
                    {
                        await using var conn = await _sharedDataSource.OpenConnectionAsync().ConfigureAwait(false);
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);

                    _sharedInitialized = true;
                }
                catch
                {
                    await ResetSharedStateAsync().ConfigureAwait(false);
                    throw;
                }
            }

            _sharedRefCount++;
            _connectionString = _sharedConnectionString;
            DataSource = _sharedDataSource ?? throw new InvalidOperationException("Shared data source not initialized.");
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (_sharedRefCount > 0)
            {
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                await ResetSharedStateAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    /// <summary>
    /// Creates an isolated schema for a test.
    /// Schema names are unique per test class to support parallel execution.
    /// </summary>
    /// <param name="testClassName">Name of the test class (for schema naming)</param>
    /// <returns>Schema name to use for the test</returns>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        return await CreateIsolatedSchemaInternalAsync(testClassName, applySeed: true).ConfigureAwait(false);
    }

    internal async Task<string> CreateIsolatedSchemaInternalAsync(string testClassName, bool applySeed)
    {
        var counter = _schemaCounters.AddOrUpdate(testClassName, 1, (_, c) => c + 1);
        var schemaName = $"test_{SanitizeSchemaName(testClassName)}_{counter}_{Guid.NewGuid():N}".ToLowerInvariant();

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA {schemaName};
            SET search_path TO {schemaName}, public;
            """;
        await cmd.ExecuteNonQueryAsync();

        var seedPath = applySeed ? Environment.GetEnvironmentVariable(SeedPathEnv) : null;
        if (!string.IsNullOrWhiteSpace(seedPath))
        {
            var profile = Environment.GetEnvironmentVariable(SeedProfileEnv);
            await SeedRunner.ApplyAsync(DataSource, seedPath, schemaName, profile);
        }

        return schemaName;
    }

    /// <summary>
    /// Drops an isolated schema created for a test.
    /// </summary>
    public async Task DropSchemaAsync(string schemaName)
    {
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= DropSchemaMaxAttempts; attempt++)
        {
            try
            {
                await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP SCHEMA IF EXISTS {schemaName} CASCADE;";
                cmd.CommandTimeout = DropSchemaCommandTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransientDropSchemaFailure(ex))
            {
                lastTransient = ex;
                if (attempt == DropSchemaMaxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to drop schema '{schemaName}' after {DropSchemaMaxAttempts} attempts.",
            lastTransient);
    }

    /// <summary>
    /// Get a connection configured for a specific schema.
    /// </summary>
    public async Task<NpgsqlConnection> GetConnectionAsync(string? schemaName = null)
    {
        var conn = await DataSource.OpenConnectionAsync();

        if (schemaName is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO {schemaName}, public;";
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }

    /// <summary>
    /// Execute raw SQL for test setup in a specific schema.
    /// </summary>
    public async Task ExecuteAsync(string sql, string? schemaName = null)
    {
        await using var conn = await GetConnectionAsync(schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Clean up test data in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await ExecuteAsync("""
            DO $$
            DECLARE
                r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename != 'spatial_ref_sys') LOOP
                    EXECUTE 'TRUNCATE TABLE ' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);
    }

    private static string SanitizeSchemaName(string name)
    {
        return new string(name
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
    }

    private static bool IsTransientDropSchemaFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException;
    }

    internal static async Task ExecuteWithInitializationRetryAsync(
        Func<Task> operation,
        int maxAttempts = InitializationMaxAttempts,
        TimeSpan? baseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var delay = baseDelay ?? TimeSpan.FromMilliseconds(250);
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransientInitializationFailure(ex))
            {
                lastTransient = ex;
                if (attempt == maxAttempts)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * attempt)).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to initialize PostgreSQL fixture after {maxAttempts} attempts.",
            lastTransient);
    }

    private static async Task ResetSharedStateAsync()
    {
        if (_sharedDataSource is not null)
        {
            await _sharedDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_sharedContainer is not null)
        {
            await _sharedContainer.DisposeAsync().ConfigureAwait(false);
        }

        _sharedDataSource = null;
        _sharedContainer = null;
        _sharedConnectionString = null;
        _sharedInitialized = false;
    }

    private static bool IsTransientInitializationFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException or SocketException;
    }
}
