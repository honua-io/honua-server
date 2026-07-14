// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Postgres.Features.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// ADR-0060 substrate-neutral MIGRATION-GATE lane (#2166, #2457). Puts the expand/contract discipline
/// in the loop end-to-end: drives the REAL <see cref="PostgresDatabaseMigrationRunner"/> against a real
/// PostgreSQL database with synthetic migration assemblies so the fail-closed rejection, the annotated
/// apply path, and the plan-level "pending contract scripts block coordinated deploy" signal are all
/// proven against a live database rather than a fake.
/// </summary>
/// <remarks>
/// The pure classification logic is unit-tested in <c>MigrationSafetyClassifierTests</c>, and the
/// probe's consumption of the plan signal is unit-tested in <c>DeployPreflightProbeTests</c>; this lane
/// deliberately does not duplicate those and instead exercises the real runner + real database path.
/// Every test <c>[SkippableFact]</c>-skips when Docker is unavailable.
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateMigrationGateTests : IClassFixture<LocalSubstratePostgresFixture>
{
    private static readonly string[] Script002Only = ["002_drop_legacy_annotated.sql"];
    private static readonly string[] Script004Only = ["004_drop_note_annotated.sql"];

    private const string ExpandScript =
        """
        CREATE TABLE honua_ci_demo (
            id INTEGER PRIMARY KEY,
            legacy_name TEXT
        );
        """;

    private const string UnannotatedContractScript =
        """
        ALTER TABLE honua_ci_demo DROP COLUMN legacy_name;
        """;

    private const string AnnotatedContractScript =
        """
        -- honua:compatibility-review reason=legacy_name removed in the contract phase after v2 rollout
        ALTER TABLE honua_ci_demo DROP COLUMN legacy_name;
        """;

    private const string SecondExpandScript =
        """
        ALTER TABLE honua_ci_demo ADD COLUMN note TEXT;
        """;

    private const string DropNoteAnnotatedScript =
        """
        -- honua:compatibility-review reason=note column removed in the contract phase after v3 rollout
        ALTER TABLE honua_ci_demo DROP COLUMN note;
        """;

    private readonly LocalSubstratePostgresFixture _postgres;

    public LocalSubstrateMigrationGateTests(LocalSubstratePostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [SkippableFact]
    public async Task RunMigrations_UnannotatedContractScript_FailsClosedNamingMarkerConvention()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var runner = CreateEnforcingRunner();
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_unannotated_{Guid.NewGuid():N}",
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_unannotated.sql", UnannotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, assembly);

        result.Successful.Should().BeFalse("an unannotated contract-phase migration must be rejected fail-closed");
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("honua:compatibility-review",
            "the rejection names the compatibility-review marker convention authors must add");
        result.ErrorMessage!.Should().Contain("002_drop_legacy_unannotated.sql",
            "the rejection names the offending script");

        // Fail-closed: the gate rejects before any script executes, so even the expand script is not applied.
        (await TableExistsAsync(connectionString, "honua_ci_demo"))
            .Should().BeFalse("the runner must not apply any script when the batch is rejected");
    }

    [SkippableFact]
    public async Task RunMigrations_AnnotatedContractScript_AppliesExpandThenContract()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var runner = CreateEnforcingRunner();
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_annotated_{Guid.NewGuid():N}",
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, assembly);

        result.Successful.Should().BeTrue($"the annotated contract migration is rollout-safe. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().HaveCount(2, "both the expand and the annotated contract scripts apply");

        // The expand created the table and the annotated contract dropped the column.
        (await TableExistsAsync(connectionString, "honua_ci_demo")).Should().BeTrue();
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name"))
            .Should().BeFalse("the annotated contract migration dropped the legacy column");
    }

    [SkippableFact]
    public async Task PlanMigrations_UnannotatedContractScript_ReportsContractBlockingCoordinatedDeploy()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var runner = CreateEnforcingRunner();
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_plan_{Guid.NewGuid():N}",
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_unannotated.sql", UnannotatedContractScript));

        var plan = await runner.PlanMigrationsAsync(connectionString, assembly);

        plan.Successful.Should().BeTrue("planning succeeds even when the batch would be rejected on apply");
        plan.UpgradeRequired.Should().BeTrue();

        // These are exactly the signals DeployPreflightProbe reads to set ReadyForCoordinatedDeploy=false:
        // a pending contract-phase script must run in a dedicated contract step, never on a rolling deploy.
        plan.HasContractScripts.Should().BeTrue("a pending contract-phase script blocks a coordinated deploy");
        plan.HasUnannotatedBreakingScripts.Should().BeTrue();
        plan.ContractScriptNames.Should().Contain(name => name.EndsWith("002_drop_legacy_unannotated.sql", StringComparison.Ordinal));
        plan.UnannotatedBreakingScriptNames.Should().Contain(name => name.EndsWith("002_drop_legacy_unannotated.sql", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RunMigrations_FreshInstallUnderGate_ProvisionsFullyAndSkipsBackup()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        // A fresh install (empty journal) under Gate must provision fully with zero approval — the gate
        // is journal-scoped — and the backup hook must NOT fire (nothing to protect on a brand-new DB).
        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var sentinel = ReserveSentinelPath();
        var runner = CreateRunner(
            new MigrationSafetyOptions
            {
                Enforce = true,
                ContractApplyPolicy = ContractApplyPolicy.Gate,
                BackupCommand = SentinelCommand(sentinel),
            });
        var assembly = SyntheticMigrationsCompiler.Compile(
            $"honua_synthetic_freshgate_{Guid.NewGuid():N}",
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, assembly);

        result.Successful.Should().BeTrue(
            $"a fresh install always provisions fully regardless of the gate. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().HaveCount(2);
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name")).Should().BeFalse();
        File.Exists(sentinel).Should().BeFalse("the backup hook does not run on a fresh install (empty journal)");
    }

    [SkippableFact]
    public async Task RunMigrations_UpgradeUnderGate_BlocksAnnotatedContractWithoutApproval()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_upgradegate_{Guid.NewGuid():N}";

        // First upgrade establishes a non-empty journal (this is now an existing database).
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        var runner = CreateRunner(new MigrationSafetyOptions
        {
            Enforce = true,
            ContractApplyPolicy = ContractApplyPolicy.Gate,
        });
        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, upgrade);

        result.Successful.Should().BeFalse("the gate blocks pending annotated contract scripts on an existing database");
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("002_drop_legacy_annotated.sql", "the block names the offending script");
        result.ErrorMessage!.Should().Contain("/api/v1/admin/deploy/preflight", "the block names the preflight endpoint");
        result.ErrorMessage!.Should().Contain("/api/v1/admin/observability/migrations", "the block names the migrations endpoint");
        result.ErrorMessage!.Should().Contain("HONUA_APPROVE_CONTRACT_MIGRATIONS", "the block names the approval switch");

        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name"))
            .Should().BeTrue("the blocked contract migration must not have applied");
    }

    [SkippableFact]
    public async Task RunMigrations_UpgradeUnderGate_AppliesAnnotatedContractWhenApproved()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_upgradeapprove_{Guid.NewGuid():N}";
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        // The approval nonce is bound to the exact pending contract script name (DbUp journals by name,
        // which SyntheticMigrationsCompiler emits verbatim), so the operator supplies the digest printed
        // in the block message.
        var nonce = MigrationSafetyClassifier.ComputeContractApprovalNonce(Script002Only);
        var runner = CreateRunner(
            new MigrationSafetyOptions
            {
                Enforce = true,
                ContractApplyPolicy = ContractApplyPolicy.Gate,
            },
            approvalToken: nonce);

        var result = await runner.RunMigrationsAsync(connectionString, upgrade);

        result.Successful.Should().BeTrue(
            $"the one-shot approval nonce approves the gated contract migration. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().ContainSingle(name => name.EndsWith("002_drop_legacy_annotated.sql", StringComparison.Ordinal));
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name"))
            .Should().BeFalse("the approved contract migration dropped the legacy column");
    }

    [SkippableFact]
    public async Task RunMigrations_UpgradeUnderGate_StaleNonceDoesNotApproveLaterContract()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        // One-shot proof (#2812): a nonce minted for the FIRST contract migration must not approve a
        // LATER, different contract migration — the "HONUA_APPROVE_CONTRACT_MIGRATIONS is never cleared"
        // failure mode. We reuse the exact token that approved script 002 and show it is inert for 004.
        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_oneshot_{Guid.NewGuid():N}";
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        // First upgrade: drop legacy_name (contract 002) and add a note column (expand 003). The pending
        // annotated-contract set is just {002}, so the approval nonce is bound to that single script.
        var staleNonce = MigrationSafetyClassifier.ComputeContractApprovalNonce(Script002Only);
        var firstUpgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript),
            ("003_add_note_expand.sql", SecondExpandScript));
        var firstResult = await CreateRunner(
                new MigrationSafetyOptions { Enforce = true, ContractApplyPolicy = ContractApplyPolicy.Gate },
                approvalToken: staleNonce)
            .RunMigrationsAsync(connectionString, firstUpgrade);
        firstResult.Successful.Should().BeTrue($"the first contract migration applies under its own nonce. Error: {firstResult.ErrorMessage}");
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "note")).Should().BeTrue("the note column was added by the expand");

        // A NEW, different contract migration (004 drops the note column) is now pending. The operator has
        // left the stale 002 nonce in the environment; it must NOT approve 004.
        var secondUpgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript),
            ("003_add_note_expand.sql", SecondExpandScript),
            ("004_drop_note_annotated.sql", DropNoteAnnotatedScript));
        var blocked = await CreateRunner(
                new MigrationSafetyOptions { Enforce = true, ContractApplyPolicy = ContractApplyPolicy.Gate },
                approvalToken: staleNonce)
            .RunMigrationsAsync(connectionString, secondUpgrade);

        blocked.Successful.Should().BeFalse("a nonce bound to script 002 must not approve the later script 004");
        blocked.ErrorMessage.Should().NotBeNull();
        blocked.ErrorMessage!.Should().Contain("004_drop_note_annotated.sql", "the block names the new offending script");
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "note"))
            .Should().BeTrue("the stale nonce must not have applied the later contract migration");

        // The freshly-minted nonce for 004 approves it — the gate is not permanently stuck.
        var freshNonce = MigrationSafetyClassifier.ComputeContractApprovalNonce(Script004Only);
        var approved = await CreateRunner(
                new MigrationSafetyOptions { Enforce = true, ContractApplyPolicy = ContractApplyPolicy.Gate },
                approvalToken: freshNonce)
            .RunMigrationsAsync(connectionString, secondUpgrade);

        approved.Successful.Should().BeTrue($"the nonce bound to 004 approves it. Error: {approved.ErrorMessage}");
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "note"))
            .Should().BeFalse("the freshly-approved contract migration dropped the note column");
    }

    [SkippableFact]
    public async Task RunMigrations_BackupHookFailure_FailsClosedBeforeApplyingContract()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_backupfail_{Guid.NewGuid():N}";
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        // Auto policy so the gate is not what blocks; a failing backup hook (exit 1) must fail the run
        // closed before the pending contract script is applied.
        var stderr = new string('x', 700);
        var recorder = new CapturingBackupHookRecorder();
        var runner = CreateRunner(new MigrationSafetyOptions
        {
            Enforce = true,
            ContractApplyPolicy = ContractApplyPolicy.Auto,
            BackupCommand = FailingCommand(stderr),
        }, backupHookRecorder: recorder);
        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, upgrade);

        result.Successful.Should().BeFalse("a failing backup hook fails the migration run closed");
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("BackupCommand", "the failure names the backup hook");
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name"))
            .Should().BeTrue("no contract script may apply after the backup hook failed (fail closed, ordering)");

        var outcome = recorder.Outcomes.Should().ContainSingle().Subject;
        outcome.Succeeded.Should().BeFalse();
        outcome.Outcome.Should().Be("failed");
        outcome.DurationMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        outcome.Stderr.Should().StartWith(new string('x', 500));
        outcome.Stderr!.Length.Should().BeLessThan(stderr.Length);
        outcome.PendingContractScripts.Should().ContainSingle(name =>
            name.EndsWith("002_drop_legacy_annotated.sql", StringComparison.Ordinal));
        outcome.MigrationRunId.Should().StartWith("schema-migration-");
    }

    [SkippableFact]
    public async Task RunMigrations_BackupHook_RunsBeforeContractApplyAndSucceeds()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_backupok_{Guid.NewGuid():N}";
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        var sentinel = ReserveSentinelPath();
        var recorder = new CapturingBackupHookRecorder();
        var runner = CreateRunner(new MigrationSafetyOptions
        {
            Enforce = true,
            ContractApplyPolicy = ContractApplyPolicy.Auto,
            BackupCommand = SentinelCommand(sentinel),
        }, backupHookRecorder: recorder);
        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_drop_legacy_annotated.sql", AnnotatedContractScript));

        var result = await runner.RunMigrationsAsync(connectionString, upgrade);

        result.Successful.Should().BeTrue($"the backup hook succeeded so the contract migration applies. Error: {result.ErrorMessage}");
        File.Exists(sentinel).Should().BeTrue("the backup hook ran before the contract script applied");
        (await ColumnExistsAsync(connectionString, "honua_ci_demo", "legacy_name")).Should().BeFalse();

        var outcome = recorder.Outcomes.Should().ContainSingle().Subject;
        outcome.Succeeded.Should().BeTrue();
        outcome.Outcome.Should().Be("succeeded");
        outcome.ExitCode.Should().Be(0);
        outcome.DurationMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        outcome.Stderr.Should().BeNull();
        outcome.PendingContractScripts.Should().ContainSingle(name =>
            name.EndsWith("002_drop_legacy_annotated.sql", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RunMigrations_BackupHook_SkippedWhenNoPendingContractScripts()
    {
        Skip.IfNot(_postgres.Available, "Docker/PostgreSQL is not available for the migration-gate lane.");

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var assemblyName = $"honua_synthetic_backupskip_{Guid.NewGuid():N}";
        await ApplyExpandBaselineAsync(connectionString, assemblyName);

        var sentinel = ReserveSentinelPath();
        var runner = CreateRunner(new MigrationSafetyOptions
        {
            Enforce = true,
            ContractApplyPolicy = ContractApplyPolicy.Auto,
            BackupCommand = SentinelCommand(sentinel),
        });

        // A purely additive (expand) upgrade — the backup hook must not fire.
        var upgrade = SyntheticMigrationsCompiler.Compile(
            assemblyName,
            ("001_expand.sql", ExpandScript),
            ("002_add_note_expand.sql", SecondExpandScript));

        var result = await runner.RunMigrationsAsync(connectionString, upgrade);

        result.Successful.Should().BeTrue($"an additive upgrade applies cleanly. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().ContainSingle(name => name.EndsWith("002_add_note_expand.sql", StringComparison.Ordinal));
        File.Exists(sentinel).Should().BeFalse("the backup hook is skipped when no contract-class script is pending");
    }

    private async Task ApplyExpandBaselineAsync(string connectionString, string assemblyName)
    {
        // Applies only the 001 expand script under the SAME assembly name so its journal entry matches
        // the later upgrade's 001 (DbUp journals by script name); the follow-up run then sees only the
        // 002 script as pending against a non-empty journal.
        var baseline = SyntheticMigrationsCompiler.Compile(assemblyName, ("001_expand.sql", ExpandScript));
        var result = await CreateRunner(new MigrationSafetyOptions { Enforce = true })
            .RunMigrationsAsync(connectionString, baseline);
        result.Successful.Should().BeTrue($"the expand baseline must apply. Error: {result.ErrorMessage}");
    }

    private static PostgresDatabaseMigrationRunner CreateEnforcingRunner()
        => new(Options.Create(new MigrationSafetyOptions { Enforce = true }));

    private static PostgresDatabaseMigrationRunner CreateRunner(
        MigrationSafetyOptions options,
        string? approvalToken = null,
        IDatabaseMigrationBackupHookRecorder? backupHookRecorder = null)
    {
        IConfiguration? configuration = null;
        if (approvalToken != null)
        {
            configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [MigrationSafetyOptions.ApproveContractMigrationsKey] = approvalToken,
                })
                .Build();
        }

        return new PostgresDatabaseMigrationRunner(Options.Create(options), configuration, backupHookRecorder);
    }

    private static string ReserveSentinelPath()
        // The second segment is a generated relative literal, so it can never be rooted and drop
        // the temp-path prefix.
        => Path.Combine(Path.GetTempPath(), $"honua_backup_{Guid.NewGuid():N}.marker");

    private static string SentinelCommand(string path)
        => OperatingSystem.IsWindows() ? $"type nul > \"{path}\"" : $": > '{path}'";

    private static string FailingCommand(string stderr)
        => OperatingSystem.IsWindows()
            ? $"echo {stderr} 1>&2 & exit /b 1"
            : $"printf '%s' '{stderr}' >&2; exit 1";

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@qualified) IS NOT NULL";
        command.Parameters.AddWithValue("qualified", $"public.{table}");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
            )
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed class CapturingBackupHookRecorder : IDatabaseMigrationBackupHookRecorder
    {
        public List<DatabaseMigrationBackupHookResult> Outcomes { get; } = new();

        public Task RecordAsync(
            DatabaseMigrationBackupHookResult result,
            CancellationToken cancellationToken = default)
        {
            Outcomes.Add(result);
            return Task.CompletedTask;
        }
    }
}
