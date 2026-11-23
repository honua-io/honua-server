// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Hosting.MigrateOnly;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateMigrateOnlyTests : IClassFixture<LocalSubstratePostgresFixture>
{
    private const string ExpandScript =
        """
        CREATE TABLE honua_migrate_only_demo (
            id INTEGER PRIMARY KEY,
            legacy_name TEXT
        );
        """;

    private const string AnnotatedContractScript =
        """
        -- honua:compatibility-review reason=legacy_name removed in the contract phase after v2 rollout
        ALTER TABLE honua_migrate_only_demo DROP COLUMN legacy_name;
        """;

    private const string UnannotatedContractScript =
        """
        ALTER TABLE honua_migrate_only_demo DROP COLUMN legacy_name;
        """;

    private readonly LocalSubstratePostgresFixture _postgres;

    public LocalSubstrateMigrateOnlyTests(LocalSubstratePostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [SkippableFact]
    public async Task ServerProcess_MigrateOnly_AppliesRealMigrationsThenExitsWithoutListening()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);

        var first = await RunServerProcessAsync(connectionString);

        first.ExitCode.Should().Be(0, first.Describe());
        first.Summary.GetProperty("outcome").GetString().Should().Be("completed");
        first.Summary.GetProperty("journalWasEmpty").GetBoolean().Should().BeTrue();
        first.Summary.GetProperty("schemaVerified").GetBoolean().Should().BeTrue();
        first.Summary.GetProperty("schemaNumber").GetInt32()
            .Should().Be(first.Summary.GetProperty("binarySchemaNumber").GetInt32());
        var applied = first.Summary.GetProperty("appliedScripts").EnumerateArray().Select(script => script.GetString()!).ToArray();
        applied.Should().NotBeEmpty();
        (await ReadJournalAsync(connectionString)).Should().BeEquivalentTo(applied,
            "the summary names exactly the scripts the journal recorded");
        first.StandardError.Should().NotContain("Now listening on:", "migrate-only mode never starts Kestrel");

        var second = await RunServerProcessAsync(connectionString);

        second.ExitCode.Should().Be(0, second.Describe());
        second.Summary.GetProperty("journalWasEmpty").GetBoolean().Should().BeFalse();
        second.Summary.GetProperty("appliedScripts").GetArrayLength().Should().Be(0, "a current journal applies nothing");
        second.Summary.GetProperty("schemaNumber").GetInt32()
            .Should().Be(first.Summary.GetProperty("schemaNumber").GetInt32());
    }

    [SkippableFact]
    public async Task ServerProcess_StoppedDuringMigration_RestartsWithAnEmptyJournal()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var childConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "honua_migrate_only_interrupted"
        }.ConnectionString;

        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await InstallMigrationBarrierAsync(observer);
        await StopBlockedMigrationAsync(observer, childConnectionString);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(720618, 1)", observer);
        await unlock.ExecuteNonQueryAsync(timeout.Token);
        await WaitForMigrationSessionAsync(observer, activityExists: false, timeout.Token);
        var servicesTableExists = await LocalSubstrateMigrationDatabase.ScalarAsync(
            connectionString, "SELECT to_regclass('honua.services') IS NOT NULL");
        servicesTableExists.Should().Be(false, "the interrupted CREATE TABLE rolled back");

        await using var removeBarrier = new NpgsqlCommand("DROP EVENT TRIGGER stop_migration_after_ddl", observer);
        await removeBarrier.ExecuteNonQueryAsync(timeout.Token);

        var restarted = await RunServerProcessAsync(connectionString);
        restarted.ExitCode.Should().Be(0, restarted.Describe());
        restarted.Summary.GetProperty("journalWasEmpty").GetBoolean().Should().BeTrue();
        restarted.Summary.GetProperty("schemaVerified").GetBoolean().Should().BeTrue();
        var applied = restarted.Summary.GetProperty("appliedScripts").EnumerateArray()
            .Select(script => script.GetString()!).ToArray();
        applied.Should().NotBeEmpty();
        (await ReadJournalAsync(connectionString)).Should().BeEquivalentTo(applied);
    }

    private static async Task InstallMigrationBarrierAsync(NpgsqlConnection observer)
    {
        const string sql =
            """
            SELECT pg_advisory_lock(720618, 1);
            CREATE FUNCTION hold_migration_after_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_event_trigger_ddl_commands()
                    WHERE object_identity = 'honua.services'
                ) THEN
                    PERFORM pg_advisory_xact_lock(720618, 1);
                END IF;
            END;
            $$;
            CREATE EVENT TRIGGER stop_migration_after_ddl ON ddl_command_end
                WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION hold_migration_after_ddl();
            """;
        await using var command = new NpgsqlCommand(sql, observer);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task StopBlockedMigrationAsync(NpgsqlConnection observer, string connectionString)
    {
        using var process = StartServerProcess(connectionString);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await WaitForMigrationSessionAsync(observer, activityExists: true, timeout.Token);
            process.HasExited.Should().BeFalse("the migration is blocked inside a CREATE TABLE");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cleanupTimeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(cleanupTimeout.Token);
        }
    }

    private static async Task WaitForMigrationSessionAsync(
        NpgsqlConnection observer, bool activityExists, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity AS activity
                WHERE activity.datname = current_database()
                  AND activity.application_name = 'honua_migrate_only_interrupted'
                  AND (NOT @blocked_only OR EXISTS (
                      SELECT 1 FROM pg_locks AS held_lock
                      WHERE held_lock.pid = activity.pid AND held_lock.locktype = 'advisory'
                        AND held_lock.classid = 720618 AND held_lock.objid = 1
                        AND held_lock.objsubid = 2 AND NOT held_lock.granted
                  ))
            )
            """;
        await using var command = new NpgsqlCommand(sql, observer);
        command.Parameters.AddWithValue("blocked_only", activityExists);
        while ((bool)(await command.ExecuteScalarAsync(cancellationToken))! != activityExists)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    [SkippableFact]
    public async Task MigrateOnlyAtNPlusK_ThenSkipMigrationsReaderAtN_StartsOnVerifiedExpandAndRefusesOthers()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);

        // The newer image (N+k) runs out of process in migrate-only mode, exactly as a pre-upgrade
        // Job or one-shot task would, and records a declaration for every script it applied.
        var migrate = await RunServerProcessAsync(connectionString);
        migrate.ExitCode.Should().Be(0, migrate.Describe());

        // The rollback image (N) ships only the scripts below the newest k, and starts with
        // HONUA_SKIP_MIGRATIONS=true, so only its startup schema guard runs.
        var full = ServerCoreSchemaMigrations.Manifest;
        // A default Database:Schema install never discovers the configured-schema adoption
        // scripts (109, 227, 231), so migrate-only neither applies nor declares them here.
        string[] adoption =
        [
            full.ConfiguredSchemaAdoptionMigration,
            full.ConfiguredRegistryAdoptionMigration!,
            full.ConfiguredFeatureStoreAdoptionMigration!,
        ];
        var newest = full.KnownMigrationNames!
            .Where(name => name.StartsWith("Honua.Server.Migrations.", StringComparison.Ordinal))
            .Except(adoption, StringComparer.Ordinal)
            .OrderByDescending(name => MigrationReaderCompatibility.ReadSchemaNumber(name))
            .Take(3)
            .ToHashSet(StringComparer.Ordinal);
        var windowFloor = newest.Min(name => MigrationReaderCompatibility.ReadSchemaNumber(name));
        var shipped = full.KnownMigrationNames!
            .Where(name => !name.StartsWith("Honua.Server.Migrations.", StringComparison.Ordinal) ||
                MigrationReaderCompatibility.ReadSchemaNumber(name) < windowFloor)
            .ToHashSet(StringComparer.Ordinal);
        var olderReader = new PostgresCoreSchemaGuard(new PostgresCoreSchemaMigrationManifest(
            full.ApplicationMigrationAssemblyName,
            full.MetadataV2SnapshotMigration,
            full.MetadataV2ReleasePackagesMigration,
            full.RasterExternalStorageMigration,
            full.SensorThingsMigration,
            full.RasterOverviewsMigration,
            full.RasterFootprintsMigration,
            full.ConfiguredSchemaAdoptionMigration,
            full.GovernedLineageMigration,
            full.InitialSchemaMigration,
            full.SensorThingsIdSequencesMigration,
            full.SensorThingsSensingMigration)
        {
            Attachments = full.Attachments,
            KnownMigrationNames = shipped,
        });
        newest.Should().HaveCount(3);
        foreach (var name in newest)
        {
            MigrationReaderCompatibility.ReadSchemaNumber(name).Should().BeGreaterThan(
                MigrationReaderCompatibility.ReaderSchemaOf(shipped), name);
        }

        // A shipped script may opt out with honua:reader-incompatible (142 does). The older reader must
        // refuse exactly those and verify every expand script in the window, so the refusal list is
        // also the proof that the expand declarations were accepted.
        var declared = await ReadDeclaredClassificationsAsync(connectionString, newest);
        declared.Keys.Should().BeEquivalentTo(newest, "migrate-only records a declaration for every script it applied");
        var optedOutShipped = newest
            .Where(name => declared[name] != MigrationReaderCompatibilityClassification.Expand)
            .ToArray();
        newest.Except(optedOutShipped).Should().NotBeEmpty("the window must still prove an expand script verifies");

        var journalBefore = await ReadJournalAsync(connectionString);
        if (optedOutShipped.Length == 0)
        {
            await olderReader.Invoking(guard => guard.VerifyAsync(connectionString)).Should().NotThrowAsync(
                "the newest shipped scripts are expand-only and their recorded declarations verify for the older reader");
        }
        else
        {
            var shippedRefusal = (await olderReader.Invoking(guard => guard.VerifyAsync(connectionString))
                .Should().ThrowAsync<Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaCompatibilityException>()).Which;
            shippedRefusal.UnknownMigrations.Should().BeEquivalentTo(optedOutShipped,
                "only scripts declared reader-incompatible are refused; the expand scripts verify");
        }
        (await ReadJournalAsync(connectionString)).Should().BeEquivalentTo(journalBefore, "the skip path applies nothing");

        // Sibling: the newer image also applied a script its author opted out of forward compatibility.
        const string optedOut = "Honua.Server.Migrations.999_OptedOutProbe.sql";
        const string sql = "-- honua:reader-incompatible reason=older readers write rows the new trigger rejects\n" +
            "CREATE TABLE IF NOT EXISTS honua.opted_out_probe (id integer PRIMARY KEY);";
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                sql + "\nINSERT INTO public.schema_versions (scriptname, applied) VALUES (@name, now());", connection);
            command.Parameters.AddWithValue("name", optedOut);
            await command.ExecuteNonQueryAsync();
            await PostgresDatabaseMigrationRunner.RecordReaderCompatibilityDeclarationsAsync(
                connection, [MigrationReaderCompatibility.Declare(optedOut, sql)], CancellationToken.None);
        }

        var refusal = (await olderReader.Invoking(guard => guard.VerifyAsync(connectionString))
            .Should().ThrowAsync<Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaCompatibilityException>()).Which;
        refusal.UnknownMigrations.Should().BeEquivalentTo(optedOutShipped.Append(optedOut));
        refusal.DeclarationRefusals.Should().ContainKey(optedOut)
            .WhoseValue.Should().Be("declared reader-incompatible, not expand");
    }

    [SkippableFact]
    public async Task MigrateOnly_GatedContractMigration_RefusesWithNonceThenAppliesWhenApproved()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var assemblyName = $"honua_migrate_only_gate_{Guid.NewGuid():N}";

        var baseline = await RunInProcessAsync(connectionString, SyntheticMigrationsCompiler.Compile(
            assemblyName, ("001_expand.sql", ExpandScript)));
        baseline.ExitCode.Should().Be(0, baseline.Summary.ToString());

        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var refused = await RunInProcessAsync(connectionString, upgrade);

        refused.ExitCode.Should().Be(MigrateOnlyOutcome.ContractApprovalRequired.ExitCode, refused.Summary.ToString());
        refused.Summary.GetProperty("code").GetString().Should().Be("contract-approval-required");
        var approval = refused.Summary.GetProperty("contractApproval");
        approval.GetProperty("setting").GetString().Should().Be(MigrationSafetyOptions.ApproveContractMigrationsKey);
        var nonce = approval.GetProperty("nonce").GetString();
        nonce.Should().NotBeNullOrWhiteSpace();
        refused.Summary.GetProperty("pendingScripts").EnumerateArray()
            .Should().ContainSingle(script => script.GetProperty("classification").GetString() == "contract-annotated");
        (await ColumnExistsAsync(connectionString, "legacy_name")).Should().BeTrue("a refused run applies nothing");

        var approved = await RunInProcessAsync(
            connectionString,
            upgrade,
            (MigrationSafetyOptions.ApproveContractMigrationsKey, nonce));

        approved.ExitCode.Should().Be(0, approved.Summary.ToString());
        approved.Summary.GetProperty("appliedScripts").EnumerateArray()
            .Should().ContainSingle(script => script.GetString()!.EndsWith("002_drop_legacy_annotated.sql", StringComparison.Ordinal));
        (await ColumnExistsAsync(connectionString, "legacy_name")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task MigrateOnly_UnannotatedContractMigration_RefusesAndAppliesNothing()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);

        var run = await RunInProcessAsync(connectionString, SyntheticMigrationsCompiler.Compile(
            $"honua_migrate_only_unannotated_{Guid.NewGuid():N}",
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_unannotated.sql", UnannotatedContractScript)));

        run.ExitCode.Should().Be(MigrateOnlyOutcome.UnannotatedContractMigration.ExitCode, run.Summary.ToString());
        run.Summary.GetProperty("code").GetString().Should().Be("unannotated-contract-migration");
        run.Summary.GetProperty("appliedScripts").GetArrayLength().Should().Be(0);
        (await TableExistsAsync(connectionString)).Should().BeFalse("the gate rejects the batch before any script runs");
    }

    [SkippableFact]
    public async Task MigrateOnly_FailedBackupCommand_RefusesBeforeTheContractScript()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migrate-only lane.");
        var connectionString = await _postgres.CreateFreshDatabaseAsync(enablePostGis: true);
        var assemblyName = $"honua_migrate_only_backup_{Guid.NewGuid():N}";
        (await RunInProcessAsync(connectionString, SyntheticMigrationsCompiler.Compile(
            assemblyName, ("001_expand.sql", ExpandScript)))).ExitCode.Should().Be(0);
        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));
        var nonce = (await RunInProcessAsync(connectionString, upgrade))
            .Summary.GetProperty("contractApproval").GetProperty("nonce").GetString();

        var run = await RunInProcessAsync(
            connectionString,
            upgrade,
            (MigrationSafetyOptions.ApproveContractMigrationsKey, nonce),
            ("Database:MigrationSafety:BackupCommand", "exit 3"));

        run.ExitCode.Should().Be(MigrateOnlyOutcome.BackupFailed.ExitCode, run.Summary.ToString());
        run.Summary.GetProperty("code").GetString().Should().Be("backup-failed");
        var hook = run.Summary.GetProperty("backupHooks").EnumerateArray().Should().ContainSingle().Subject;
        hook.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        hook.GetProperty("exitCode").GetInt32().Should().Be(3);
        (await ColumnExistsAsync(connectionString, "legacy_name")).Should().BeTrue("a failed backup applies no contract script");
    }

    private static async Task<(int ExitCode, JsonElement Summary)> RunInProcessAsync(
        string connectionString,
        System.Reflection.Assembly migrations,
        params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var output = new StringWriter();
        var exitCode = await MigrateOnlyCommand.RunAsync(configuration, output, _ => null, migrations);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle();
        using var document = JsonDocument.Parse(lines[0]);
        return (exitCode, document.RootElement.Clone());
    }

    private static Process StartServerProcess(string connectionString)
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the test output carries the server runtime ({0})", serverDirectory);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(serverAssembly);
        foreach (var key in start.Environment.Keys.Where(IsInheritedHonuaSetting).ToArray())
        {
            start.Environment.Remove(key);
        }

        // Production, with no admin password, encryption key, licence or Redis: a serving start would
        // refuse without them, migrate-only mode needs none.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment[MigrateOnlyMode.SwitchKey] = "true";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the migrate-only process.");
    }

    private static async Task<ServerRun> RunServerProcessAsync(string connectionString)
    {
        using var process = StartServerProcess(connectionString);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The migrate-only process did not exit: " + await stderrTask);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle("standard output carries only the summary. stdout:{0}{1}{0}stderr:{0}{2}",
            Environment.NewLine, stdout, stderr.Length <= 4000 ? stderr : stderr[^4000..]);
        using var document = JsonDocument.Parse(lines[0]);
        return new ServerRun(process.ExitCode, document.RootElement.Clone(), stderr);
    }

    private static bool IsInheritedHonuaSetting(string key)
        => key.StartsWith("HONUA_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Database__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DataSource__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("AWS_LAMBDA_", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, string>> ReadDeclaredClassificationsAsync(
        string connectionString, IReadOnlyCollection<string> scriptNames)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT script_name, classification FROM public.schema_version_reader_compatibility WHERE script_name = ANY(@names)",
            connection);
        command.Parameters.AddWithValue("names", scriptNames.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        var classifications = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            classifications[reader.GetString(0)] = reader.GetString(1);
        }

        return classifications;
    }

    private static async Task<string[]> ReadJournalAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT scriptname FROM public.schema_versions", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var scripts = new List<string>();
        while (await reader.ReadAsync())
        {
            scripts.Add(reader.GetString(0));
        }

        return [.. scripts];
    }

    private static async Task<bool> TableExistsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.honua_migrate_only_demo') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'honua_migrate_only_demo' AND column_name = @column)",
            connection);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ServerRun(int ExitCode, JsonElement Summary, string StandardError)
    {
        public string Describe()
            => $"summary: {Summary}{Environment.NewLine}stderr tail: {(StandardError.Length <= 4000 ? StandardError : StandardError[^4000..])}";
    }
}
