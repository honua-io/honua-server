// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

// Replicas start together, fail part-way and are stopped while waiting; the migration runner must
// apply each script once, commit all or nothing, and report only what it committed.
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateMigrationConcurrencyTests : IClassFixture<LocalSubstratePostgresFixture>
{
    private const string SkipReason = "Docker/PostgreSQL is not available for the migration-gate lane.";
    private const string CreateRunsTable = "CREATE TABLE honua_ci_runs (id SERIAL PRIMARY KEY);";
    private const string SlowInsertRun = "SELECT pg_sleep(2); INSERT INTO honua_ci_runs DEFAULT VALUES;";
    private const string CreateSecondTable = "CREATE TABLE honua_ci_second (id INTEGER PRIMARY KEY);";
    private const string FailingScript = "INSERT INTO honua_ci_missing_table VALUES (1);";
    private const string FixedThirdScript = "CREATE TABLE honua_ci_third (id INTEGER PRIMARY KEY);";

    private readonly LocalSubstratePostgresFixture _postgres;

    public LocalSubstrateMigrationConcurrencyTests(LocalSubstratePostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [SkippableFact]
    public async Task RunMigrations_TwoRunnersStartTogether_ApplyEachScriptExactlyOnce()
    {
        Skip.IfNot(_postgres.Available, SkipReason);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_concurrent_{Guid.NewGuid():N}",
            ("001_create_runs.sql", CreateRunsTable),
            ("002_slow_insert_run.sql", SlowInsertRun));

        var results = await Task.WhenAll(
            CreateRunner().RunMigrationsAsync(connectionString, assembly),
            CreateRunner().RunMigrationsAsync(connectionString, assembly));

        results.Should().OnlyContain(result => result.Successful,
            "the advisory lock makes the second runner wait and then find nothing pending");
        results.Should().ContainSingle(result => result.AppliedScripts.Count == 2,
            "exactly one runner applies the batch");
        results.Should().ContainSingle(result => result.AppliedScripts.Count == 0,
            "the runner that waited reports nothing applied");
        (await CountAsync(connectionString, "honua_ci_runs")).Should().Be(1, "the slow script ran once");
        (await JournalCountAsync(connectionString)).Should().Be(2, "each script is journaled once");
    }

    [SkippableFact]
    public async Task RunMigrations_ScriptFailsMidBatch_RollsBackWholeBatchReportsNothingAppliedAndResumes()
    {
        Skip.IfNot(_postgres.Available, SkipReason);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var failing = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_midbatch_{Guid.NewGuid():N}",
            ("001_create_runs.sql", CreateRunsTable),
            ("002_create_second.sql", CreateSecondTable),
            ("003_fails.sql", FailingScript));

        var failed = await CreateRunner().RunMigrationsAsync(connectionString, failing);

        failed.Successful.Should().BeFalse();
        failed.AppliedScripts.Should().BeEmpty("one transaction covers the batch, so a failed run commits no script");
        failed.ErrorMessage.Should().NotContain("honua_ci_missing_table", "the operator message stays generic");
        (await TableExistsAsync(connectionString, "honua_ci_runs")).Should().BeFalse("script 001 rolled back");
        (await TableExistsAsync(connectionString, "honua_ci_second")).Should().BeFalse("script 002 rolled back");
        (await JournalCountAsync(connectionString)).Should().Be(0, "a failed run journals nothing");

        var corrected = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_midbatch_fixed_{Guid.NewGuid():N}",
            ("001_create_runs.sql", CreateRunsTable),
            ("002_create_second.sql", CreateSecondTable),
            ("003_fails.sql", FixedThirdScript));

        var resumed = await CreateRunner().RunMigrationsAsync(connectionString, corrected);

        resumed.Successful.Should().BeTrue($"the corrected batch applies from the start. Error: {resumed.ErrorMessage}");
        resumed.AppliedScripts.Should().HaveCount(3);
        (await JournalCountAsync(connectionString)).Should().Be(3);
    }

    [SkippableFact]
    public async Task RunMigrations_CancelledWhileWaitingForLock_StopsPromptlyAndLeavesNoState()
    {
        Skip.IfNot(_postgres.Available, SkipReason);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_cancel_{Guid.NewGuid():N}",
            ("001_create_runs.sql", CreateRunsTable));

        await using (var holder = new NpgsqlConnection(connectionString))
        {
            await holder.OpenAsync();
            await ExecuteLockFunctionAsync(holder, "pg_advisory_lock");

            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var stopwatch = Stopwatch.StartNew();
            var waiting = () => CreateRunner().RunMigrationsAsync(connectionString, assembly, stopping.Token);

            await waiting.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
                "a stopping replica must not poll out the five-minute lock timeout");
            (await TableExistsAsync(connectionString, "honua_ci_runs")).Should().BeFalse();
            (await JournalCountAsync(connectionString)).Should().Be(0);

            await ExecuteLockFunctionAsync(holder, "pg_advisory_unlock");
        }

        var later = await CreateRunner().RunMigrationsAsync(connectionString, assembly);

        later.Successful.Should().BeTrue($"the lock is free again. Error: {later.ErrorMessage}");
        later.AppliedScripts.Should().ContainSingle();
    }

    private static PostgresDatabaseMigrationRunner CreateRunner()
        => new(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest,
            Options.Create(new MigrationSafetyOptions { Enforce = true }));

    private static async Task ExecuteLockFunctionAsync(NpgsqlConnection connection, string function)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}(@lock_key);", connection);
        _ = command.Parameters.AddWithValue("lock_key", PostgresDatabaseMigrationRunner.MigrationLockKey);
        _ = await command.ExecuteScalarAsync();
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
        => await ScalarAsync(connectionString, $"SELECT to_regclass('public.{table}') IS NOT NULL") is true;

    private static async Task<long> CountAsync(string connectionString, string table)
        => (long)(await ScalarAsync(connectionString, $"SELECT count(*) FROM public.{table}"))!;

    private static async Task<long> JournalCountAsync(string connectionString)
        => await TableExistsAsync(connectionString, "schema_versions")
            ? await CountAsync(connectionString, "schema_versions")
            : 0;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
