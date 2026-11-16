// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using System.Security.Cryptography;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Seeding;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Honua.TestKit;

public sealed class PostgresFixture : IAsyncLifetime, IIsolatedSchemaFixture
{
    // Fixtures share process-wide state by default; _sharedLock serializes lifecycle mutations.
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly PostgresSharedState GlobalSharedState = new();
    private readonly PostgresSharedState SharedState;

    internal sealed class PostgresSharedState
    {
        public IAsyncDisposable? SharedContainer { get; set; }
        public NpgsqlDataSource? SharedDataSource { get; set; }
        public string? SharedConnectionString { get; set; }
        public int SharedRefCount { get; set; }
        public bool SharedInitialized { get; set; }
        public bool BoundedResources { get; set; }

        internal async Task ResetAsync()
        {
            var dataSource = SharedDataSource;
            var container = SharedContainer;
            SharedDataSource = null;
            SharedContainer = null;
            SharedConnectionString = null;
            SharedInitialized = false;
            BoundedResources = false;

            try
            {
                if (dataSource is not null)
                    await dataSource.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (container is not null)
                    await container.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    private const string ExternalConnectionStringEnv = "HONUA_TEST_DB_URL";
    private const string SeedPathEnv = "HONUA_TEST_DB_SEED_PATH";
    private const string SeedProfileEnv = "HONUA_TEST_DB_SEED_PROFILE";
    private const int DropSchemaCommandTimeoutSeconds = 30;
    private const int DropSchemaMaxAttempts = 3;
    private const int InitializationMaxAttempts = 5;
    private string? _connectionString;
    private readonly bool _boundedResources;
    private bool _hasSharedReference;

    public PostgresFixture() : this(boundedResources: false)
    {
    }

    internal PostgresFixture(bool boundedResources, PostgresSharedState? sharedState = null)
    {
        _boundedResources = boundedResources;
        SharedState = sharedState ?? GlobalSharedState;
    }

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (_hasSharedReference) return;
            if (_boundedResources && SharedState.SharedContainer is not null && !SharedState.BoundedResources)
            {
                throw new InvalidOperationException("A bounded fixture cannot reuse an unbounded Postgres container.");
            }
            if (!SharedState.SharedInitialized)
            {
                try
                {
                    var externalConnectionString = Environment.GetEnvironmentVariable(ExternalConnectionStringEnv);
                    if (ShouldStartContainer(externalConnectionString))
                    {
                        var containerBuilder = new PostgreSqlBuilder()
                            .WithImage("postgis/postgis:18-3.6")
                            .WithDatabase("honua_test")
                            .WithUsername("test")
                            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
                            .WithEnvironment("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
                            .WithCommand("-c", "max_connections=200");
                        if (_boundedResources)
                        {
                            containerBuilder = containerBuilder.WithCreateParameterModifier(parameters =>
                            {
                                parameters.HostConfig.Memory = 2L * 1024 * 1024 * 1024;
                                parameters.HostConfig.MemorySwap = parameters.HostConfig.Memory;
                                parameters.HostConfig.NanoCPUs = 2_000_000_000;
                                parameters.HostConfig.PidsLimit = 256;
                            });
                        }
                        var container = containerBuilder.Build();
                        SharedState.SharedContainer = container;
                        SharedState.BoundedResources = _boundedResources;
                        await container.StartAsync();
                        var publishedConnectionString = container.GetConnectionString();
                        SharedState.SharedConnectionString = publishedConnectionString;
                        await PostgresEndpointProbe.WaitUntilAcceptingAsync(publishedConnectionString).ConfigureAwait(false);
                    }
                    else
                    {
                        SharedState.SharedConnectionString = externalConnectionString;
                    }

                    var connectionString = SharedState.SharedConnectionString
                        ?? throw new InvalidOperationException("Postgres fixture has no connection string.");
                    SharedState.SharedDataSource = NpgsqlDataSource.Create(connectionString);

                    await ExecuteWithInitializationRetryAsync(async () =>
                    {
                        await using var conn = await SharedState.SharedDataSource.OpenConnectionAsync().ConfigureAwait(false);
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        // Test hosts skip application migrations. Install the same sampler
                        // functions production migration 008 binds, so tile SQL does not inline them.
                        cmd.CommandText = RasterSamplingSql.ReadBoundMigration();
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        // Test hosts skip application migrations. Execute the owning
                        // production DDL once under the shared initialization lock
                        // so registered providers have their required tables.
                        var assembly = typeof(PostgresFixture).Assembly;
                        // codeql[cs/linq/missed-select]: executes each embedded migration before the next script is read
                        foreach (var migration in PostgresFixtureMigrations.Discover())
                        {
                            await using var sql = assembly.GetManifestResourceStream(migration)!;
                            using var reader = new StreamReader(sql);
                            var ddl = (await reader.ReadToEndAsync().ConfigureAwait(false))
                                .Replace("$HonuaSchema$", "honua", StringComparison.Ordinal);
                            cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS honua;\n" + ddl;
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);

                    SharedState.SharedInitialized = true;
                }
                catch
                {
                    await ResetSharedStateAsync().ConfigureAwait(false);
                    throw;
                }
            }

            SharedState.SharedRefCount++;
            _hasSharedReference = true;
            _connectionString = SharedState.SharedConnectionString;
            DataSource = SharedState.SharedDataSource ?? throw new InvalidOperationException("Shared data source not initialized.");
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
            if (!_hasSharedReference) return;
            _hasSharedReference = false;
            SharedState.SharedRefCount--;

            if (SharedState.SharedRefCount == 0 && SharedState.SharedInitialized)
            {
                await ResetSharedStateAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        return await CreateIsolatedSchemaInternalAsync(testClassName, applySeed: true).ConfigureAwait(false);
    }

    internal async Task<string> CreateIsolatedSchemaInternalAsync(string testClassName, bool applySeed)
    {
        var schemaName = CreateIsolatedIdentifier(testClassName);

        // honua-server#1568 residual (#2028 follow-up): CREATE SCHEMA writes pg_namespace and
        // churns the shared pg_catalog (pg_class/pg_depend) exactly like the already-serialized
        // DROP SCHEMA ... CASCADE teardown. #2020/#2028 routed schema *drop* and the post-create
        // migration/seed through the advisory lock but left schema *creation* a non-participant,
        // so a per-test CREATE SCHEMA in one collection raced a locked DROP SCHEMA CASCADE in
        // another and the two acquired catalog locks in interleaved order — the residual 40P01
        // deadlock. Serialize creation on the same lock so all schema-namespace DDL orders
        // behind in-flight schema mutation instead of deadlocking it.
        await CreateSchemaUnderLockAsync(schemaName).ConfigureAwait(false);

        var seedPath = applySeed ? Environment.GetEnvironmentVariable(SeedPathEnv) : null;
        if (!string.IsNullOrWhiteSpace(seedPath))
        {
            var profile = Environment.GetEnvironmentVariable(SeedProfileEnv);
            await SeedRunner.ApplyAsync(DataSource, seedPath, schemaName, profile);
        }

        return schemaName;
    }

    public async Task<string> CreateIsolatedDatabaseAsync(string testClassName)
    {
        var databaseName = CreateIsolatedIdentifier(testClassName);

        // CREATE DATABASE cannot run inside a transaction/pooled session that holds other
        // state, so use a dedicated connection on the bootstrap data source.
        await using (var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{databaseName}\";";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var databaseConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
        }.ToString();

        // Enable the same extensions the shared container provisions, so migrations that
        // depend on PostGIS/pgcrypto behave identically in the isolated database.
        try
        {
            await ExecuteWithInitializationRetryAsync(async () =>
            {
                await using var conn = new NpgsqlConnection(databaseConnectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception initializationError)
        {
            try
            {
                await DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Isolated database initialization and cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }

        return databaseConnectionString;
    }

    public async Task DropDatabaseAsync(string databaseName)
    {
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= DropSchemaMaxAttempts; attempt++)
        {
            try
            {
                // Flush any idle physical connections Npgsql still holds for the
                // target database in process-wide pools. The per-test NpgsqlDataSource
                // is disposed before this runs, but seed/migration paths that opened
                // connections from a bare connection string can leave pooled backends
                // that re-appear in pg_stat_activity between the terminate and the drop
                // (the classic "database is being accessed by other users" race).
                NpgsqlConnection.ClearAllPools();

                await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);

                // Terminate any lingering backends so DROP DATABASE is not blocked by
                // connections still settling in the pool.
                await using (var terminateCmd = conn.CreateCommand())
                {
                    terminateCmd.CommandText = """
                        SELECT pg_terminate_backend(pid)
                        FROM pg_stat_activity
                        WHERE datname = @datname AND pid <> pg_backend_pid();
                        """;
                    _ = terminateCmd.Parameters.AddWithValue("datname", databaseName);
                    terminateCmd.CommandTimeout = DropSchemaCommandTimeoutSeconds;
                    await terminateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // DROP DATABASE ... WITH (FORCE) (PostgreSQL 13+) terminates any
                // backend that reconnects after the explicit terminate above, closing
                // the race window that the plain DROP leaves open.
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);";
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
            $"Failed to drop database '{databaseName}' after {DropSchemaMaxAttempts} attempts.",
            lastTransient);
    }

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

    public Task CreateSchemaUnderLockAsync(string schemaName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        return RunUnderSchemaMutationLockAsync(
            async () =>
            {
                await using var conn = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE SCHEMA {schemaName};";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    Task IIsolatedSchemaFixture.ExecuteDdlUnderLockAsync(string sql, string? schemaName)
        => ExecuteDdlUnderLockAsync(sql, schemaName, CancellationToken.None);

    public Task ExecuteDdlUnderLockAsync(string sql, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return RunUnderSchemaMutationLockAsync(
            async () =>
            {
                await using var conn = await GetConnectionAsync(schemaName).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

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

    public async Task ExecuteAsync(string sql, string? schemaName = null)
    {
        await using var conn = await GetConnectionAsync(schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

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

    // Takes the schema-mutation advisory lock shared with SeedRunner and holds it until the
    // returned handle is disposed. Use it when a test writes process-global rows (for example
    // honua.raster_data) and must also read them back before any parallel fixture seed runs;
    // RunUnderSchemaMutationLockAsync covers a single action instead.
    public async Task<IAsyncDisposable> AcquireSchemaMutationLockAsync(CancellationToken cancellationToken = default)
    {
        var lockConnection = await GetConnectionAsync().ConfigureAwait(false);
        try
        {
            await ExecuteAdvisoryLockCommandAsync(lockConnection, "SELECT pg_advisory_lock(@key);", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await lockConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SchemaMutationLockLease(lockConnection);
    }

    private sealed class SchemaMutationLockLease(NpgsqlConnection lockConnection) : IAsyncDisposable
    {
        private NpgsqlConnection? _lockConnection = lockConnection;

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _lockConnection, null);
            if (connection is null)
            {
                return;
            }

            try
            {
                await ExecuteAdvisoryLockCommandAsync(connection, "SELECT pg_advisory_unlock(@key);", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public Task ApplyGlobalSeedSqlAsync(string sql, string? schemaName = null, CancellationToken cancellationToken = default)
        => ApplyGlobalSeedSqlAsync(sql, configureCommand: null, schemaName, cancellationToken);

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
                    .WithVariable("HonuaSchema", "\"honua\"")
                    .WithTransaction()
                    .Build();
                result = upgrader.PerformUpgrade();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    public Task<string> ProvisionODataDatabaseAsync(string seedPath)
        => ProvisionDatabaseAsync(seedPath);

    public async Task<string> ProvisionDatabaseAsync(string seedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedPath);
        // Match the production migration runner and canonical seed. A CI role named
        // honua otherwise resolves "$user",public to the empty honua.features table.
        var connectionString = new NpgsqlConnectionStringBuilder(
            await CreateIsolatedDatabaseAsync("odata_pilot").ConfigureAwait(false))
        {
            SearchPath = "public",
        }.ToString();
        try
        {
            await RunUnderSchemaMutationLockAsync(() =>
            {
                // Use the production numeric/root ordering and default-schema exclusion:
                // provider migration 001 depends on server migration 001, while 109 is only
                // meaningful for a non-default configured schema.
                var upgrade = Honua.Db.Postgres.Features.Infrastructure.Migrations.PostgresDatabaseMigrationRunner
                    .BuildUpgrader(connectionString, typeof(PostgresFixture).Assembly, "\"honua\"",
                        includeRasterProviderMigrations: true, includeConfiguredSchemaAdoption: false,
                        configuredSchemaAdoptionMigration: "Honua.Server.Migrations.109_AdoptConfiguredGuardedSchema.sql")
                    .PerformUpgrade();
                if (!upgrade.Successful)
                {
                    throw new InvalidOperationException("OData pilot database provisioning failed.", upgrade.Error);
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await SeedRunner.ApplyAsync(dataSource, seedPath, "public", profileName: null).ConfigureAwait(false);
            return connectionString;
        }
        catch
        {
            await DropDatabaseAsync(new NpgsqlConnectionStringBuilder(connectionString).Database!).ConfigureAwait(false);
            throw;
        }
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

    private static string CreateIsolatedIdentifier(string testClassName)
    {
        // PostgreSQL identifiers are limited to 63 bytes. Bound only the readable
        // ASCII prefix so the full random suffix survives without server truncation.
        // "test_" + 25 characters + "_" + 32 GUID characters = 63 bytes.
        var prefix = new string(testClassName
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            .Take(25)
            .ToArray());
        return $"test_{prefix}_{Guid.NewGuid():N}".ToLowerInvariant();
    }

    internal static bool ShouldStartContainer([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? externalConnectionString)
        => string.IsNullOrWhiteSpace(externalConnectionString);

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

    private Task ResetSharedStateAsync() => SharedState.ResetAsync();

    private static bool IsTransientInitializationFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException or SocketException;
    }
}
