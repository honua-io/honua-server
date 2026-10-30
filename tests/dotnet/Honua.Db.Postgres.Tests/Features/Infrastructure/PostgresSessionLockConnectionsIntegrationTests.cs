// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

/// <summary>
/// Behind RDS Proxy a session advisory lock pins the proxy session, and a frozen Lambda never
/// prunes its pool. On Lambda the import and publish paths hold those locks on unpooled
/// connections, so releasing the lock ends the backend instead of returning a pinned connection
/// to the pool (honua-server#6765).
/// </summary>
[Collection("Database")]
public sealed class PostgresSessionLockConnectionsIntegrationTests(PostgresFixture fixture)
{
    private const string StopMessage = "stop the attempt after observing the lock";
    private static readonly PostgresSessionLockConnections Release = new(releaseSessionOnClose: true);

    [IntegrationTest]
    public async Task OpenAsync_DataSourceReleaseSessionOnClose_LeavesNoBackendAfterLockRelease()
    {
        var applicationName = NewApplicationName();
        await using var dataSource = NpgsqlDataSource.Create(PooledConnectionString(applicationName));

        int pid;
        await using (var connection = await Release.OpenAsync(dataSource, CancellationToken.None))
        {
            pid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()");
            await LockAndUnlockAsync(connection, applicationName);
        }

        Assert.False(await WaitForBackendAsync(pid, expectedAlive: false));
    }

    [IntegrationTest]
    public async Task OpenAsync_DataSourcePooled_KeepsBackendInPool()
    {
        // Long-lived hosts keep warm pools: the same backend serves the next open.
        var applicationName = NewApplicationName();
        await using var dataSource = NpgsqlDataSource.Create(PooledConnectionString(applicationName));

        int pid;
        await using (var connection = await PostgresSessionLockConnections.Pooled.OpenAsync(
            dataSource, CancellationToken.None))
        {
            pid = await ScalarAsync<int>(connection, "SELECT pg_backend_pid()");
            await LockAndUnlockAsync(connection, applicationName);
        }

        Assert.True(await WaitForBackendAsync(pid, expectedAlive: true));
    }

    [IntegrationTest]
    public async Task OpenAsync_PooledWorkConnectionReleaseSessionOnClose_ReplacesItAndEndsSessionOnDispose()
    {
        var applicationName = NewApplicationName();
        await using var dataSource = NpgsqlDataSource.Create(PooledConnectionString(applicationName));
        await using var work = await dataSource.OpenConnectionAsync();
        var workPid = await ScalarAsync<int>(work, "SELECT pg_backend_pid()");

        int sessionPid;
        await using (var session = await Release.OpenAsync(work, CancellationToken.None))
        {
            Assert.True(session.IsUnpooledReplacement);
            sessionPid = await ScalarAsync<int>(session.Connection, "SELECT pg_backend_pid()");
            Assert.NotEqual(workPid, sessionPid);
            await LockAndUnlockAsync(session.Connection, applicationName);
        }

        Assert.False(await WaitForBackendAsync(sessionPid, expectedAlive: false));
        Assert.Equal(workPid, await ScalarAsync<int>(work, "SELECT pg_backend_pid()"));
    }

    [IntegrationTest]
    public async Task GetOrImportAsync_ReleaseSessionOnClose_LockBackendEndsWithTheAttempt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresSessionLockConnectionsIntegrationTests));
        try
        {
            await fixture.ExecuteAsync(
                $"""
                CREATE TABLE "{schema}".job_artifact_import_receipt (
                    scope_key text PRIMARY KEY, tenant_id text NOT NULL, actor_id text NOT NULL,
                    intent_id text NOT NULL, fingerprint text NOT NULL, phase text NOT NULL,
                    schema_name text NOT NULL, table_name text NOT NULL, physical_table text,
                    feature_count integer, source_srid integer, target_srid integer,
                    content_sha256 text, crs_state text,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now())
                """);
            var applicationName = NewApplicationName();
            await using var dataSource = NpgsqlDataSource.Create(PooledConnectionString(applicationName));
            var ledger = new PostgresServerJobArtifactImportLedger(
                dataSource,
                new PostgresSchemaConfiguration(schema, schema, [schema]),
                Release);
            var scope = new ServerJobArtifactImportScope(
                "tenant", "actor", "intent-6765", new string('a', 64), schema, "target");

            var lockHolders = new List<int>();
            var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.GetOrImportAsync(
                scope,
                async (_, _) =>
                {
                    lockHolders.AddRange(await AdvisoryLockHoldersAsync(applicationName));
                    throw new InvalidOperationException(StopMessage);
                },
                CancellationToken.None));
            Assert.Equal(StopMessage, stopped.Message);

            var holder = Assert.Single(lockHolders);
            Assert.False(await WaitForBackendAsync(holder, expectedAlive: false));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static string NewApplicationName() => "honua-6765-" + Guid.NewGuid().ToString("N")[..12];

    private string PooledConnectionString(string applicationName)
        => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = true,
            PersistSecurityInfo = true,
        }.ConnectionString;

    private static async Task LockAndUnlockAsync(NpgsqlConnection connection, string key)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@key, 0)); SELECT pg_advisory_unlock(hashtextextended(@key, 0));",
            connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<int>> AdvisoryLockHoldersAsync(string applicationName)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT l.pid FROM pg_locks l
            JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND a.application_name = @name
            """,
            admin);
        command.Parameters.AddWithValue("name", applicationName);
        var pids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            pids.Add(reader.GetInt32(0));
        }

        return pids;
    }

    // Returns whether the backend is alive once it reaches the expected state, or after the wait.
    private async Task<bool> WaitForBackendAsync(int pid, bool expectedAlive)
    {
        var alive = !expectedAlive;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid)", admin);
            command.Parameters.AddWithValue("pid", pid);
            alive = (bool)(await command.ExecuteScalarAsync())!;
            if (alive == expectedAlive)
            {
                return alive;
            }

            await Task.Delay(100);
        }

        return alive;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
