// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp;
using DbUp.Engine;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public sealed class GovernedLineageMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_ConcurrentChangeThenOutboxWrite_CompletesAndPreservesLegacyRows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(GovernedLineageMigrationTests));
        try
        {
            await RunConcurrentMigrationAsync(schema, ct);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task RunConcurrentMigrationAsync(string schema, CancellationToken ct)
    {
        await using var writer = await fixture.DataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(writer, $"""
            CREATE TABLE "{schema}".feature_changes (id INTEGER PRIMARY KEY);
            CREATE TABLE "{schema}".feature_change_outbox (id INTEGER PRIMARY KEY);
            CREATE TABLE "{schema}".alert_events (id INTEGER PRIMARY KEY);
            INSERT INTO "{schema}".alert_events VALUES (1);
            """, ct);
        await using var writeTransaction = await writer.BeginTransactionAsync(ct);
        await ExecuteAsync(writer, "SET LOCAL deadlock_timeout = '5s'", ct);
        await ExecuteAsync(writer, $"INSERT INTO \"{schema}\".feature_changes VALUES (1)", ct);
        var migrationSql = (await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "110_PreserveGovernedLineage.sql"), ct))
            .Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
        UpgradeEngine CreateUpgrader() => BuildUpgrader(schema, migrationSql);
        var upgrade = Task.Run(() => PostgresMigrationDeadlockRetry.RunAsync(CreateUpgrader, ct), ct);
        var observation = new LockObservation(schema, ct);
        try
        {
            await WaitForChangeLogLockAsync(observation).WaitAsync(ct);
            await ExecuteAsync(writer, $"INSERT INTO \"{schema}\".feature_change_outbox VALUES (1)", ct);
            await writeTransaction.CommitSafelyAsync(ct);
            Assert.True((await upgrade).Successful);
        }
        finally
        {
            // Release the writer even if lock observation or migration fails.
            await writeTransaction.DisposeAsync();
            await upgrade;
        }
        Assert.Empty((await PostgresMigrationDeadlockRetry.RunAsync(CreateUpgrader, ct)).Scripts);
        await using var migration = await fixture.DataSource.OpenConnectionAsync(ct);
        await AssertPreservedRowsAsync(migration, schema, ct);
    }

    private UpgradeEngine BuildUpgrader(string schema, string sql)
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = schema,
            Options = "-c deadlock_timeout=100ms"
        };
        return DeployChanges.To.PostgresqlDatabase(connection.ConnectionString)
            .JournalToPostgresqlTable(schema, "schema_versions")
            .WithScripts(new SqlScript("lineage.sql", sql))
            .WithTransaction().Build();
    }

    private async Task WaitForChangeLogLockAsync(LockObservation observation)
    {
        await using var observer = await fixture.DataSource.OpenConnectionAsync(observation.Token);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_locks l
                JOIN pg_class c ON c.oid = l.relation
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE a.application_name = @schema AND n.nspname = @schema
                  AND c.relname = 'feature_changes'
                  AND l.mode = 'AccessExclusiveLock' AND NOT l.granted)
            """, observer);
        command.Parameters.AddWithValue("schema", observation.Schema);
        while (!(bool)(await command.ExecuteScalarAsync(observation.Token))!)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), observation.Token);
        }
    }

    private static async Task AssertPreservedRowsAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{schema}".feature_changes
                 WHERE id = 1 AND event_id IS NULL AND operation_instance_id IS NULL
                   AND correlation_id IS NULL AND audit_id IS NULL AND proposal_id IS NULL),
                (SELECT count(*) FROM "{schema}".feature_change_outbox
                 WHERE id = 1 AND operation_instance_id IS NULL AND correlation_id IS NULL
                   AND audit_id IS NULL AND proposal_id IS NULL),
                (SELECT count(*) FROM "{schema}".alert_events
                 WHERE id = 1 AND source_event_id IS NULL AND job_id IS NULL
                   AND operation_instance_id IS NULL AND correlation_id IS NULL
                   AND audit_id IS NULL AND proposal_id IS NULL)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record LockObservation(string Schema, CancellationToken Token);
}
