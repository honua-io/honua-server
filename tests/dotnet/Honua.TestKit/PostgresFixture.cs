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
    /// <remarks>
    /// honua-server#1568 follow-up (#2020): <c>DROP SCHEMA ... CASCADE</c> takes
    /// <c>ACCESS EXCLUSIVE</c> locks on every dependent object and churns the global
    /// <c>pg_catalog</c> (<c>pg_class</c>/<c>pg_namespace</c>/<c>pg_depend</c>). When this
    /// universal teardown runs concurrently with another collection's locked seed/migration
    /// (which also mutate the catalog while holding the schema-mutation advisory lock), the
    /// two acquire catalog locks in interleaved order and deadlock (<c>40P01</c>). #1968
    /// serialized the DDL <em>setup</em> paths but left teardown a non-participant, so it
    /// kept racing the catalog. Serialize the drop on the same advisory lock so it orders
    /// behind in-flight schema mutation instead of deadlocking it.
    /// </remarks>
    public async Task DropSchemaAsync(string schemaName)
    {
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= DropSchemaMaxAttempts; attempt++)
        {
            try
            {
                await RunUnderSchemaMutationLockAsync(async () =>
                {
                    await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DROP SCHEMA IF EXISTS {schemaName} CASCADE;";
                    cmd.CommandTimeout = DropSchemaCommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
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
    /// Runs a schema-mutating action (e.g. a raw DbUp upgrade) while holding the session-level
    /// Postgres advisory lock shared with <see cref="Seeding.SeedRunner"/>, retrying on a
    /// transient <c>40P01</c> deadlock / <c>40001</c> serialization failure.
    /// </summary>
    /// <remarks>
    /// DbUp DDL (the <c>schema_versions</c> journal, <c>CREATE TABLE/INDEX</c>, extensions) takes
    /// locks on the global <c>pg_catalog</c>, which per-test schema isolation does not protect.
    /// Serializing every schema-mutating setup path on one advisory lock removes the catalog-level
    /// lock-ordering deadlocks that flake parallel integration runs (honua-server#1568).
    /// </remarks>
    /// <param name="action">The schema-mutating work to run while the lock is held.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunUnderSchemaMutationLockAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            await using var lockConnection = await GetConnectionAsync().ConfigureAwait(false);
            await ExecuteAdvisoryLockCommandAsync(lockConnection, "SELECT pg_advisory_lock(@key);", cancellationToken).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (attempt < maxAttempts && IsTransientLockFailure(ex))
            {
                // Deadlock / serialization victim: its transaction is fully rolled back, so retry
                // is safe. The lock is released in the finally block before the next attempt.
            }
            finally
            {
                await ExecuteAdvisoryLockCommandAsync(lockConnection, "SELECT pg_advisory_unlock(@key);", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Applies a raw SQL seed/setup script that mutates the literal, process-global
    /// <c>honua</c>/<c>honua_data</c> schemas (e.g. <c>CREATE SCHEMA IF NOT EXISTS honua</c>,
    /// <c>ALTER TABLE honua.layers ...</c>, <c>INSERT INTO honua.scene_datasets ...</c>) while
    /// holding the shared <see cref="Seeding.SeedRunner.SeedApplicationLockKey"/> advisory lock,
    /// optionally scoping the connection's <c>search_path</c> to <paramref name="schemaName"/>.
    /// </summary>
    /// <remarks>
    /// honua-server#1568 (signature 2 — <c>40P01: deadlock detected</c>): the
    /// <c>mobile-offline-demo-v1.sql</c> and <c>base-schema.sql</c> seeds run idempotent DDL
    /// (<c>CREATE TABLE/INDEX</c>, repeated <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c>) and
    /// inserts against the literal, schema-qualified <c>honua</c> tables. Those tables are NOT
    /// scoped by the per-test <c>search_path</c> isolation, so several <c>[Collection("Database")]</c>
    /// tests applying the seed in parallel take <c>ACCESS EXCLUSIVE</c> catalog/table locks on the
    /// same global <c>honua.layers</c>/<c>honua.services</c>/<c>honua.scene_datasets</c> objects in
    /// interleaved order and deadlock. <see cref="Seeding.SeedRunner"/> already serializes its YAML
    /// seeds on this advisory lock; raw <c>.sql</c> seed application must use the same lock so all
    /// global-schema mutation serializes on one ordering rather than racing the catalog.
    /// </remarks>
    /// <param name="sql">The raw seed/setup SQL to apply.</param>
    /// <param name="schemaName">Optional schema to set on <c>search_path</c> before applying the SQL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ApplyGlobalSeedSqlAsync(string sql, string? schemaName = null, CancellationToken cancellationToken = default)
        => ApplyGlobalSeedSqlAsync(sql, configureCommand: null, schemaName, cancellationToken);

    /// <summary>
    /// Parameterized overload of <see cref="ApplyGlobalSeedSqlAsync(string, string?, CancellationToken)"/>.
    /// Applies a global-<c>honua</c>-schema-mutating statement under the shared
    /// <see cref="Seeding.SeedRunner.SeedApplicationLockKey"/> advisory lock (with
    /// <c>40P01</c>/<c>40001</c> retry), letting the caller bind <see cref="NpgsqlParameter"/>s
    /// via <paramref name="configureCommand"/>.
    /// </summary>
    /// <remarks>
    /// honua-server#1568 follow-up (#2020): in-test <c>UPDATE/INSERT/DELETE</c> helpers and
    /// <c>finally</c>/<c>DisposeAsync</c> cleanups that mutate literal <c>honua.*</c> tables
    /// frequently need parameters, so they were written against raw
    /// <c>GetConnectionAsync()+CreateCommand()</c> and bypassed the advisory lock entirely —
    /// the exact non-participant pattern that keeps deadlocking the catalog under parallel
    /// collections. This overload gives those paths a locked, parameterizable route.
    /// </remarks>
    /// <param name="sql">The raw seed/setup SQL to apply.</param>
    /// <param name="configureCommand">Optional callback to bind parameters / tune the command.</param>
    /// <param name="schemaName">Optional schema to set on <c>search_path</c> before applying the SQL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ApplyGlobalSeedSqlAsync(
        string sql,
        Action<NpgsqlCommand>? configureCommand,
        string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return RunUnderSchemaMutationLockAsync(
            async () =>
            {
                await using var connection = await GetConnectionAsync(schemaName).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                configureCommand?.Invoke(command);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs the full embedded DbUp migration set against <paramref name="schemaName"/> while holding
    /// the shared <see cref="Seeding.SeedRunner.SeedApplicationLockKey"/> advisory lock, retrying on a
    /// transient <c>40P01</c>/<c>40001</c>.
    /// </summary>
    /// <remarks>
    /// honua-server#1568 follow-up: <c>001_CreateHonuaSchema.sql</c> runs <c>CREATE SCHEMA IF NOT
    /// EXISTS honua; CREATE TABLE honua.services/layers ...</c> against the literal, process-global
    /// <c>honua</c> schema, which the per-test <c>search_path</c> isolation does NOT scope. Tests that
    /// run the embedded migration set directly via <c>DeployChanges...PerformUpgrade()</c> must take
    /// this same advisory lock; otherwise they are non-participants that race every locked seeder's
    /// <c>ACCESS EXCLUSIVE</c> catalog/table locks on the same global <c>honua.*</c> objects and
    /// deadlock the parallel <c>[Collection("Database")]</c> run. The original #1568 fix routed
    /// <c>DatabaseMigrationTests</c> and the raw <c>.sql</c> seeds through the lock but left the
    /// <c>BranchVersioning*</c> upgrades unguarded — this helper closes that gap.
    /// </remarks>
    /// <param name="schemaName">Isolated schema to deploy into (also the journal/search-path schema).</param>
    /// <param name="connectionString">Base connection string; its <c>SearchPath</c> is set to the schema.</param>
    /// <param name="migrationsAssembly">Assembly whose embedded <c>.sql</c> scripts are deployed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DbUp upgrade result.</returns>
    public async Task<DbUp.Engine.DatabaseUpgradeResult> RunEmbeddedMigrationsUnderLockAsync(
        string schemaName,
        string connectionString,
        System.Reflection.Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrationsAssembly);

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = $"{schemaName},public",
        };

        DbUp.Engine.DatabaseUpgradeResult result = null!;
        await RunUnderSchemaMutationLockAsync(
            () =>
            {
                var upgrader = DbUp.DeployChanges.To
                    .PostgresqlDatabase(connectionStringBuilder.ToString(), schemaName)
                    .JournalToPostgresqlTable(schemaName, "schema_versions")
                    .WithScriptsEmbeddedInAssembly(migrationsAssembly)
                    .WithTransaction()
                    .Build();
                result = upgrader.PerformUpgrade();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task ExecuteAdvisoryLockCommandAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.Parameters.AddWithValue("key", Seeding.SeedRunner.SeedApplicationLockKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientLockFailure(PostgresException ex)
        => ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

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
