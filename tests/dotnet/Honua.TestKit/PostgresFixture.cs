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
    private static readonly ConcurrentDictionary<string, int> _databaseCounters = new();
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static PostgreSqlContainer? _sharedContainer;
    private static NpgsqlDataSource? _sharedDataSource;
    private static string? _sharedConnectionString;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;
    private const string ExternalConnectionStringEnv = "HONUA_TEST_DB_URL";
    private const string SeedPathEnv = "HONUA_TEST_DB_SEED_PATH";
    private const string SeedProfileEnv = "HONUA_TEST_DB_SEED_PROFILE";

    /// <summary>
    /// Opt-in flag (<c>HONUA_TEST_DB_TEMPLATE=1</c>) selecting the faster per-test
    /// <em>template-database</em> isolation mode. When unset/0 the fixture behaves exactly
    /// as before (per-test schema isolation). When 1 the seed is applied ONCE into a
    /// process-wide template database and every test gets a fresh database cloned from it
    /// via <c>CREATE DATABASE … TEMPLATE …</c> (a fast file copy) — see
    /// <see cref="CreateIsolatedDatabaseAsync"/>.
    /// </summary>
    private const string TemplateDbEnv = "HONUA_TEST_DB_TEMPLATE";
    private const int DropSchemaCommandTimeoutSeconds = 30;
    private const int DropSchemaMaxAttempts = 3;
    private const int InitializationMaxAttempts = 5;

    // Per-test cloned databases use a small pool so a wide parallel run does not exhaust
    // the container's max_connections=200 budget (one pool per live test database).
    private const int PerDatabaseMaxPoolSize = 6;

    private string? _connectionString;

    // Test-side per-database data sources used by GetConnectionAsync/ExecuteAsync in template
    // mode (test setup helpers that insert/mutate feature rows directly). Owned by the
    // fixture; disposed when the database is dropped so pooled connections are released.
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> _testConnectionDataSources =
        new(StringComparer.Ordinal);

    // Names that were created as isolated DATABASES (template mode), not schemas. A fixture
    // that opted out of template mode (e.g. a custom seed) still creates schemas even when
    // the global flag is on, so GetConnectionAsync must route per-DB only for real databases.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _createdDatabases =
        new(StringComparer.Ordinal);

    // Routers attached by the test hosts (shared + per-isolated-host). When a per-test
    // database is dropped, its cached data source is evicted from every router so pooled
    // connections are released.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Infrastructure.TemplateDatabaseDataSourceRouter, byte> _attachedRouters =
        new();

    internal void AttachRouter(Infrastructure.TemplateDatabaseDataSourceRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _attachedRouters[router] = 1;
    }

    private static string? _templateDatabaseName;
    private static bool _templateInitialized;

    /// <summary>
    /// <see langword="true"/> when <c>HONUA_TEST_DB_TEMPLATE</c> selects template-database
    /// isolation. Read once per process so flag-off behaviour is byte-identical to before.
    /// </summary>
    public static bool TemplateDatabaseModeEnabled { get; } = ResolveTemplateMode();

    private static bool ResolveTemplateMode()
    {
        var value = Environment.GetEnvironmentVariable(TemplateDbEnv);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

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

            if (TemplateDatabaseModeEnabled && !_templateInitialized)
            {
                try
                {
                    await BuildTemplateDatabaseAsync().ConfigureAwait(false);
                    _templateInitialized = true;
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
    /// Resolves the YAML seed file used to build the template database. Mirrors
    /// <see cref="Infrastructure.ServerTestData"/>'s resolution of
    /// <c>tests/seed/server.yaml</c> so the template carries the same baseline the
    /// schema-mode shared server seeds per test.
    /// </summary>
    private static string ResolveTemplateSeedPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(SeedPathEnv);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new FileNotFoundException("Unable to locate repository root for template seed data.");
        }

        return Path.Combine(directory.FullName, "tests", "seed", "server.yaml");
    }

    /// <summary>
    /// Builds the process-wide template database ONCE: creates an empty database, installs
    /// the PostGIS/extension set, applies the baseline seed into its <c>public</c>/<c>honua</c>
    /// schemas (with <c>current_schema() = public</c>), then marks it as a PostgreSQL
    /// template. Every test database is later cloned from this template with a fast
    /// file-copy via <see cref="CreateIsolatedDatabaseAsync"/>.
    /// </summary>
    private async Task BuildTemplateDatabaseAsync()
    {
        var seedPath = ResolveTemplateSeedPath();
        var seedProfile = Environment.GetEnvironmentVariable(SeedProfileEnv);

        // Hash the seed identity so a changed seed builds a fresh template instead of
        // silently cloning a stale one.
        var seedTicks = File.GetLastWriteTimeUtc(seedPath).Ticks;
        var hash = unchecked((uint)HashCode.Combine(Path.GetFullPath(seedPath), seedTicks, seedProfile ?? string.Empty));
        var templateName = $"honua_tmpl_{hash:x8}";

        // Drop a stale template (e.g. from a previous aborted run) then build fresh.
        await DropDatabaseInternalAsync(templateName).ConfigureAwait(false);
        await ExecuteMaintenanceAsync($"CREATE DATABASE {QuoteIdentifier(templateName)};").ConfigureAwait(false);

        // Connect to the new template DB to install extensions + seed it.
        var templateConnectionString = BuildConnectionStringForDatabase(templateName);
        await using (var templateDataSource = NpgsqlDataSource.Create(templateConnectionString))
        {
            await ExecuteWithInitializationRetryAsync(async () =>
            {
                await using var conn = await templateDataSource.OpenConnectionAsync().ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            // Apply the seed with NO schema override so feature tables land in `public`
            // and honua.layers.table_schema records `public` (current_schema()). After a
            // template-clone every database is self-consistent in public/honua without any
            // per-request search_path routing.
            await SeedRunner.ApplyAsync(templateDataSource, seedPath, schemaName: null, seedProfile).ConfigureAwait(false);
        }

        // Mark as a template and forbid further connections so clones can copy it. The
        // datistemplate flag also lets CREATE DATABASE ... TEMPLATE work for non-superusers.
        await ExecuteMaintenanceAsync(
            $"UPDATE pg_database SET datistemplate = true, datallowconn = true WHERE datname = {QuoteLiteral(templateName)};")
            .ConfigureAwait(false);

        _templateDatabaseName = templateName;
    }

    /// <summary>
    /// Creates a fresh, fully-seeded database for a single test by cloning the process-wide
    /// template (<c>CREATE DATABASE … TEMPLATE …</c> — a fast file copy reproducing every
    /// table, index, function, trigger, sequence, and the cross-schema <c>honua</c> objects).
    /// Returns the new database name. The template must have no open connections during the
    /// copy, so callers must ensure the template data source is closed (it is — the template
    /// build disposes its data source).
    /// </summary>
    public async Task<string> CreateIsolatedDatabaseAsync(string testClassName)
    {
        if (!TemplateDatabaseModeEnabled || _templateDatabaseName is null)
        {
            throw new InvalidOperationException(
                "Template-database isolation is not enabled. Set HONUA_TEST_DB_TEMPLATE=1.");
        }

        var counter = _databaseCounters.AddOrUpdate(testClassName, 1, (_, c) => c + 1);
        var databaseName = $"test_{SanitizeSchemaName(testClassName)}_{counter}_{Guid.NewGuid():N}".ToLowerInvariant();

        // CREATE DATABASE cannot run inside a transaction, and the source template must have
        // no concurrent connections. Terminate any stragglers on the template, then clone.
        await ExecuteWithInitializationRetryAsync(async () =>
        {
            await TerminateConnectionsAsync(_templateDatabaseName).ConfigureAwait(false);
            await ExecuteMaintenanceAsync(
                $"CREATE DATABASE {QuoteIdentifier(databaseName)} TEMPLATE {QuoteIdentifier(_templateDatabaseName)};")
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

        _createdDatabases[databaseName] = 1;
        return databaseName;
    }

    /// <summary>
    /// Drops a per-test database created by <see cref="CreateIsolatedDatabaseAsync"/>,
    /// terminating any lingering backends first.
    /// </summary>
    public async Task DropDatabaseAsync(string databaseName)
    {
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= DropSchemaMaxAttempts; attempt++)
        {
            try
            {
                await DropDatabaseInternalAsync(databaseName).ConfigureAwait(false);
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

    private async Task DropDatabaseInternalAsync(string databaseName)
    {
        _createdDatabases.TryRemove(databaseName, out _);

        // Evict + dispose the router-cached data source for this database so its pooled
        // connections are released and don't block the drop.
        foreach (var router in _attachedRouters.Keys)
        {
            router.RemoveDatabase(databaseName);
        }

        // Release any test-side pool we opened against this database first so its connections
        // don't block the drop (and so pg_terminate_backend has less to do).
        if (_testConnectionDataSources.TryRemove(databaseName, out var perDbSource))
        {
            await perDbSource.DisposeAsync().ConfigureAwait(false);
        }

        await TerminateConnectionsAsync(databaseName).ConfigureAwait(false);
        // Clear the template marker (if any) so DROP DATABASE is permitted.
        await ExecuteMaintenanceAsync(
            $"UPDATE pg_database SET datistemplate = false WHERE datname = {QuoteLiteral(databaseName)};")
            .ConfigureAwait(false);
        await ExecuteMaintenanceAsync($"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)};").ConfigureAwait(false);
    }

    private async Task TerminateConnectionsAsync(string databaseName)
    {
        await ExecuteMaintenanceAsync(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            $"WHERE datname = {QuoteLiteral(databaseName)} AND pid <> pg_backend_pid();")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a maintenance statement (CREATE/DROP DATABASE, pg_database updates, backend
    /// termination) against the shared cluster. Uses a short-lived dedicated connection on
    /// the shared data source's host but targeting the bootstrap database so it never holds
    /// a connection on the database being created/dropped.
    /// </summary>
    private async Task ExecuteMaintenanceAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(BuildMaintenanceConnectionString());
        await conn.OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = DropSchemaCommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string ClusterConnectionString =>
        _sharedConnectionString ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    private string BuildMaintenanceConnectionString()
    {
        // Connect to the cluster's default `postgres` database so CREATE/DROP DATABASE
        // never runs while connected to the target. Pool small + short-lived.
        var builder = new NpgsqlConnectionStringBuilder(ClusterConnectionString)
        {
            Database = "postgres",
            Pooling = false,
            Multiplexing = false,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="name"/> is a per-test database this fixture
    /// cloned in template mode (as opposed to a classic per-test schema name). Used by the
    /// routing data source to decide between per-database routing and bootstrap-DB + search_path.
    /// </summary>
    internal bool IsIsolatedDatabase(string name) => _createdDatabases.ContainsKey(name);

    internal string BuildConnectionStringForDatabase(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ClusterConnectionString)
        {
            Database = databaseName,
            Multiplexing = false,
            Pooling = true,
            MaxPoolSize = PerDatabaseMaxPoolSize,
            // Per-test databases are torn down promptly; keep the pool lean.
            MinPoolSize = 0,
        };
        return builder.ConnectionString;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException($"Invalid database identifier '{identifier}'.", nameof(identifier));
        }

        return $"\"{identifier}\"";
    }

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
        // Template-database mode: the isolation unit is a database, not a schema, and
        // schemaName carries the per-test database name. Open a connection directly to that
        // database (feature tables live in its public schema) instead of the shared cluster
        // data source. Falls through to the schema path when no name is supplied.
        if (TemplateDatabaseModeEnabled &&
            !string.IsNullOrWhiteSpace(schemaName) &&
            _createdDatabases.ContainsKey(schemaName!))
        {
            var perDbSource = _testConnectionDataSources.GetOrAdd(
                schemaName!,
                name => NpgsqlDataSource.Create(BuildConnectionStringForDatabase(name)));
            return await perDbSource.OpenConnectionAsync().ConfigureAwait(false);
        }

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

        // The template database lives inside the shared container/cluster that was just torn
        // down (or is being reset after a failed init), so its handle is now stale. Clear the
        // template state so the next initialization rebuilds it against the fresh cluster.
        _templateInitialized = false;
        _templateDatabaseName = null;

        foreach (var perDbSource in _testConnectionDataSources.Values)
        {
            await perDbSource.DisposeAsync().ConfigureAwait(false);
        }

        _testConnectionDataSources.Clear();
        _createdDatabases.Clear();
        _attachedRouters.Clear();
    }

    private static bool IsTransientInitializationFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException or SocketException;
    }
}
