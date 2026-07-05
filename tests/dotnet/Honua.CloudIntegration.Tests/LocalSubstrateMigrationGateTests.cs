// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Postgres.Features.Infrastructure.Migrations;
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

    private static PostgresDatabaseMigrationRunner CreateEnforcingRunner()
        => new(Options.Create(new MigrationSafetyOptions { Enforce = true }));

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
}
