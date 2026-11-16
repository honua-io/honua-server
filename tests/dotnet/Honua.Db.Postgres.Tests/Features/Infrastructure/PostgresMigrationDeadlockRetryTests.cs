// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp;
using DbUp.Engine;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public sealed class PostgresMigrationDeadlockRetryTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(2, PostgresErrorCodes.DeadlockDetected, 3)]
    [InlineData(5, PostgresErrorCodes.DeadlockDetected, 3)]
    [InlineData(5, PostgresErrorCodes.CheckViolation, 1)]
    public async Task RunAsync_DatabaseFailure_RetriesOnlyDeadlocksAndRollsBackFailedAttempts(
        int failures, string sqlState, int expectedAttempts)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMigrationDeadlockRetryTests));
        try
        {
            await PrepareAsync(schema);
            var sql = $"""
                INSERT INTO "{schema}".effects VALUES (1);
                SELECT "{schema}".fail_until({failures}, '{sqlState}');
                """;
            UpgradeEngine CreateUpgrader() => BuildUpgrader(schema,
            [
                new SqlScript("01_prepare.sql", "SELECT 1;"),
                new SqlScript("02_retry.sql", sql)
            ]);
            var result = await PostgresMigrationDeadlockRetry.RunAsync(CreateUpgrader, CancellationToken.None);
            var expectedSuccess = failures < expectedAttempts;
            Assert.True(expectedSuccess == result.Successful, result.Error?.ToString());
            await AssertDatabaseStateAsync(new ExpectedState(schema, expectedAttempts, expectedSuccess ? 1 : 0));
            // One transaction covers every script, so a failed attempt journals nothing.
            Assert.Equal(expectedSuccess ? 2 : 0, CreateUpgrader().GetExecutedScripts().Count);
            if (!expectedSuccess)
            {
                Assert.Equal(sqlState, PostgresFailure(result.Error).SqlState);
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task RunAsync_CancelledAfterFailedAttempt_StopsBeforeRetryAndLeavesNoJournal()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMigrationDeadlockRetryTests));
        using var cancellation = new CancellationTokenSource();
        try
        {
            await PrepareAsync(schema);
            UpgradeEngine CreateUpgrader()
            {
                var upgrader = BuildUpgrader(schema,
                [
                    new SqlScript("01_write.sql", "SELECT 1;"),
                    new SqlScript("02_fail.sql", $"""SELECT "{schema}".fail_until(1, '40P01');""")
                ]);
                upgrader.ScriptExecuted += (_, _) => cancellation.Cancel();
                return upgrader;
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PostgresMigrationDeadlockRetry.RunAsync(CreateUpgrader, cancellation.Token));
            await AssertDatabaseStateAsync(new ExpectedState(schema, 1, 0));
            Assert.Empty(CreateUpgrader().GetExecutedScripts());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private UpgradeEngine BuildUpgrader(string schema, SqlScript[] scripts)
        => DeployChanges.To.PostgresqlDatabase(fixture.ConnectionString)
            .JournalToPostgresqlTable(schema, "schema_versions")
            .WithScripts(scripts).WithTransaction().Build();

    private async Task PrepareAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE SEQUENCE "{schema}".attempts;
            CREATE TABLE "{schema}".effects (id INTEGER PRIMARY KEY);
            CREATE FUNCTION "{schema}".fail_until(limit_value integer, sql_state text) RETURNS void
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF nextval('"{schema}".attempts') <= limit_value THEN
                    IF sql_state = '40P01' THEN
                        RAISE EXCEPTION 'Controlled database failure' USING ERRCODE = '40P01';
                    ELSE
                        RAISE EXCEPTION 'Controlled database failure' USING ERRCODE = '23514';
                    END IF;
                END IF;
            END
            $fn$;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertDatabaseStateAsync(ExpectedState expected)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM "{expected.Schema}".attempts),
                   (SELECT count(*) FROM "{expected.Schema}".effects)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.Attempts, reader.GetInt64(0));
        Assert.Equal(expected.Rows, reader.GetInt64(1));
    }

    private static PostgresException PostgresFailure(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException("Expected a PostgreSQL failure.", error);
    }

    private sealed record ExpectedState(string Schema, long Attempts, long Rows);
}
