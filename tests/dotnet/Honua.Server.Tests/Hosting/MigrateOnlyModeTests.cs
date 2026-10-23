// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Hosting;
using Honua.Server.Hosting.MigrateOnly;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Hosting;

/// <summary>
/// Composition and refusal coverage for migrate-only mode (honua-server#6470). The migrate-only
/// service graph must start nothing (no web server, no hosted or background service) and must
/// refuse an unusable configuration before it touches a database, each with its typed exit code.
/// The real-database path is proven in <c>LocalSubstrateMigrateOnlyTests</c>.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class MigrateOnlyModeTests
{
    [UnitTest]
    public void Composition_RegistersNoHostedServiceServerOrBackgroundWork()
    {
        var services = MigrateOnlyComposition.Register(new ServiceCollection(), Configuration());

        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService),
            "migrate-only mode starts no background service, worker, scheduler or alert dispatcher");
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IServer),
            "migrate-only mode never starts Kestrel");
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostApplicationLifetime));
        services.Should().NotContain(
            descriptor => descriptor.ServiceType.FullName != null &&
                descriptor.ServiceType.FullName.StartsWith("StackExchange.Redis.", StringComparison.Ordinal),
            "migrate-only mode needs no Redis");
    }

    [UnitTest]
    public async Task Composition_BuildsWithValidationAndResolvesOnlyTheMigrationPath()
    {
        await using var provider = MigrateOnlyComposition.Build(Configuration());

        provider.GetServices<IHostedService>().Should().BeEmpty();
        provider.GetService<IServer>().Should().BeNull();
        provider.GetRequiredService<IDatabaseMigrationRunner>().Should().NotBeNull();
        provider.GetRequiredService<IDatabaseSchemaGuard>().Should().NotBeNull();
        provider.GetRequiredService<IDatabaseCompatibilityChecker>().Should().NotBeNull();
        provider.GetRequiredService<IConnectionSecretResolver>().Should().NotBeNull();
        provider.GetRequiredService<IDatabaseMigrationBackupHookRecorder>()
            .Should().BeOfType<MigrateOnlyBackupHookRecorder>("backup outcomes are reported in the summary, not the audit store");
    }

    [UnitTheory]
    [InlineData(null, nameof(MigrateOnlyRequest.NotRequested))]
    [InlineData("", nameof(MigrateOnlyRequest.NotRequested))]
    [InlineData("false", nameof(MigrateOnlyRequest.NotRequested))]
    [InlineData("0", nameof(MigrateOnlyRequest.NotRequested))]
    [InlineData("true", nameof(MigrateOnlyRequest.Requested))]
    [InlineData(" True ", nameof(MigrateOnlyRequest.Requested))]
    [InlineData("1", nameof(MigrateOnlyRequest.Requested))]
    [InlineData("yes", nameof(MigrateOnlyRequest.Invalid))]
    public void Resolve_ReadsTheSwitchOnlyWhenExplicitlySet(string? value, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MigrateOnlyMode.SwitchKey] = value })
            .Build();

        MigrateOnlyMode.Resolve(configuration).Should().Be(Enum.Parse<MigrateOnlyRequest>(expected));
    }

    [UnitTest]
    public async Task RunAsync_InsideAwsLambda_RefusesBeforeTouchingTheDatabase()
    {
        var (exitCode, summary) = await RunAsync(
            Configuration(("ConnectionStrings:DefaultConnection", UnreachableDatabase)),
            environment: name => name == MigrateOnlyMode.AwsLambdaRuntimeApiKey ? "127.0.0.1:9001" : null);

        exitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode);
        summary.GetProperty("code").GetString().Should().Be("invalid-configuration");
        summary.GetProperty("message").GetString().Should().Contain("AWS Lambda");
    }

    [UnitTest]
    public async Task RunAsync_WithExecutionWorkerLaunch_Refuses()
    {
        var (exitCode, summary) = await RunAsync(Configuration(
            ("ConnectionStrings:DefaultConnection", UnreachableDatabase),
            ("HONUA_OPERATION_ID", "op-123")));

        exitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode);
        summary.GetProperty("message").GetString().Should().Contain("HONUA_OPERATION_ID");
    }

    [UnitTest]
    public async Task RunAsync_WithInvalidSwitchValue_Refuses()
    {
        var (exitCode, summary) = await RunAsync(Configuration(
            ("ConnectionStrings:DefaultConnection", UnreachableDatabase),
            (MigrateOnlyMode.SwitchKey, "yes")));

        exitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode);
        summary.GetProperty("message").GetString().Should().Contain(MigrateOnlyMode.SwitchKey);
    }

    [UnitTest]
    public async Task RunAsync_WithNonMigratableProvider_Refuses()
    {
        var (exitCode, summary) = await RunAsync(Configuration(
            ("ConnectionStrings:DefaultConnection", UnreachableDatabase),
            ("DataSource:Provider", "duckdb")));

        exitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode);
        summary.GetProperty("message").GetString().Should().Contain("duckdb");
    }

    [UnitTest]
    public async Task RunAsync_WithoutConnectionString_RefusesWithSummaryOnStandardOutput()
    {
        var (exitCode, summary) = await RunAsync(Configuration());

        exitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode);
        summary.GetProperty("format").GetString().Should().Be(MigrateOnlyCommand.SummaryFormat);
        summary.GetProperty("outcome").GetString().Should().Be("refused");
        summary.GetProperty("exitCode").GetInt32().Should().Be(2);
        summary.GetProperty("provider").GetString().Should().Be("postgres");
        summary.GetProperty("metadataSchema").GetString().Should().Be("honua");
        summary.GetProperty("schemaNumber").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("binarySchemaNumber").GetInt32().Should().BeGreaterThan(0);
        summary.GetProperty("appliedScripts").GetArrayLength().Should().Be(0);
        summary.GetProperty("schemaVerified").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    public void Classify_MapsEachRunnerRefusalToItsOwnOutcome()
    {
        var enforcing = new MigrationSafetyOptions { Enforce = true, BackupCommand = "pg_dump" };
        var unannotatedPlan = DatabaseMigrationPlan.Succeeded(
            ["002_drop.sql"],
            [],
            [new MigrationScriptClassification { ScriptName = "002_drop.sql", Classification = MigrationSafetyClassification.ContractUnannotated }],
            journalIsNonEmpty: true);

        MigrateOnlyCommand.Classify(new DatabaseSchemaCompatibilityException(["999_future.sql"]), null, null)
            .Should().Be(MigrateOnlyOutcome.SchemaNewerThanBinary);
        MigrateOnlyCommand.Classify(new InvalidOperationException(GatedBootShutdown.ContractGateRefusalPrefix + " 038_drop.sql"), null, enforcing)
            .Should().Be(MigrateOnlyOutcome.ContractApprovalRequired);
        MigrateOnlyCommand.Classify(
                new InvalidOperationException("Refusing to apply ... marker '" + MigrationSafetyClassifier.CompatibilityReviewMarkerConvention + "'"),
                unannotatedPlan,
                enforcing)
            .Should().Be(MigrateOnlyOutcome.UnannotatedContractMigration);
        MigrateOnlyCommand.Classify(
                new InvalidOperationException("The pre-migration backup command (Database:MigrationSafety:BackupCommand) exited with code 3."),
                null,
                enforcing)
            .Should().Be(MigrateOnlyOutcome.BackupFailed);
        MigrateOnlyCommand.Classify(new TimeoutException("Timed out waiting 5 minute(s) for database migration lock."), null, null)
            .Should().Be(MigrateOnlyOutcome.MigrationLockTimeout);
        MigrateOnlyCommand.Classify(new InvalidOperationException("relation already exists"), null, enforcing)
            .Should().Be(MigrateOnlyOutcome.MigrationFailed);
        MigrateOnlyCommand.Classify(new OperationCanceledException(), null, null)
            .Should().Be(MigrateOnlyOutcome.Cancelled);
    }

    [UnitTest]
    public void Outcomes_HaveDistinctExitCodesAndNeverUseTheRuntimeCrashCode()
    {
        MigrateOnlyOutcome.All.Select(outcome => outcome.ExitCode).Should().OnlyHaveUniqueItems();
        MigrateOnlyOutcome.All.Select(outcome => outcome.Code).Should().OnlyHaveUniqueItems();
        MigrateOnlyOutcome.All.Should().NotContain(outcome => outcome.ExitCode == 1,
            "exit code 1 is what an unhandled exception produces");
        MigrateOnlyOutcome.Completed.ExitCode.Should().Be(0);
    }

    [UnitTest]
    public void ResolveSchemaNumber_IsTheHighestNumberedServerMigration()
    {
        var expected = typeof(MigrateOnlyCommand).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Honua.Server.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => int.Parse(name["Honua.Server.Migrations.".Length..].Split('_')[0], System.Globalization.CultureInfo.InvariantCulture))
            .Max();

        MigrateOnlyCommand.ResolveSchemaNumber(typeof(MigrateOnlyCommand).Assembly).Should().Be(expected);
    }

    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private static async Task<(int ExitCode, JsonElement Summary)> RunAsync(
        IConfiguration configuration,
        Func<string, string?>? environment = null)
    {
        using var output = new StringWriter();
        var exitCode = await MigrateOnlyCommand.RunAsync(configuration, output, environment ?? (_ => null));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle("standard output carries exactly one summary line");
        using var document = JsonDocument.Parse(lines[0]);
        return (exitCode, document.RootElement.Clone());
    }
}
