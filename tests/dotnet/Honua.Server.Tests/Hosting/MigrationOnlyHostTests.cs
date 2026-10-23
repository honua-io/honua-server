// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Hosting.MigrateOnly;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class MigrationOnlyHostTests
{
    private const string Connection = "Host=database.example;Database=gis;Username=migrator;Password=never-emit-this";

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReceipt_RequiresPreflightMigrationAndSchemaVerificationInOrder(bool skipMigrations)
    {
        var configuration = Configuration();
        configuration["HONUA_SKIP_MIGRATIONS"] = skipMigrations.ToString();
        var calls = new List<string>();
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Connection, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("preflight");
            return DatabaseCompatibilityResult.Compatible("PostgreSQL", "3.6", null, ["postgis"]);
        });
        var runner = Substitute.For<IDatabaseMigrationRunner>();
        runner.RunMigrationsAsync(Connection, typeof(Program).Assembly, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("migrate");
            return DatabaseMigrationResult.Succeeded(["Honua.Server.Migrations.001_CreateHonuaSchema.sql"]);
        });
        var guard = Substitute.For<IDatabaseSchemaGuard>();
        guard.VerifyAsync(Connection, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("verify");
            return Task.CompletedTask;
        });
        await using var services = Services(checker, runner, guard);

        var receipt = await RunAsync(configuration, services, CancellationToken.None);

        receipt.GetProperty("outcome").GetString().Should().Be("completed");
        receipt.GetProperty("schemaVerified").GetBoolean().Should().BeTrue();
        receipt.GetProperty("appliedScripts").GetArrayLength().Should().Be(1);
        receipt.GetProperty("packagedMigrationManifestSha256").GetString().Should().MatchRegex("^[a-f0-9]{64}$");
        receipt.GetProperty("targetLocatorSha256").GetString().Should().Be(MigrateOnlyEvidence.ComputeTargetLocatorHash(Connection, "honua"));
        calls.Should().Equal("preflight", "migrate", "verify");
        receipt.GetRawText()
            .Should().NotContain("never-emit-this").And.NotContain("database.example").And.NotContain("migrator");
    }

    [UnitTheory]
    [InlineData("HONUA_OPERATION_ID", "job-123")]
    [InlineData("HONUA_JOB_KIND", "Geoprocessing")]
    [InlineData("DataSource:Provider", "duckdb")]
    [InlineData("HONUA_MIGRATE_ONLY", "maybe")]
    [InlineData("HONUA_MIGRATION_RUN_ID", "not-a-guid")]
    public async Task ConflictingLaunch_RefusesBeforeResolvingOrMutating(string key, string value)
    {
        var configuration = Configuration();
        configuration[key] = value;
        await using var services = new ServiceCollection().BuildServiceProvider();
        var receipt = await RunAsync(configuration, services, CancellationToken.None);
        receipt.GetProperty("code").GetString().Should().Be("invalid-configuration");
        receipt.GetProperty("schemaVerified").GetBoolean().Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("preflight", "database-incompatible")]
    [InlineData("apply", "migration-failed")]
    [InlineData("verify", "migration-failed")]
    public async Task FailedStage_NeverBecomesSuccessInDevelopmentOrDegradedMode(string failure, string code)
    {
        var configuration = Configuration();
        configuration["ASPNETCORE_ENVIRONMENT"] = "Development";
        configuration["HONUA_DB_DEGRADED_START"] = "true";
        var checker = Substitute.For<IDatabaseCompatibilityChecker>();
        checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            failure == "preflight" ? DatabaseCompatibilityResult.Incompatible("never-emit-this")
                : DatabaseCompatibilityResult.Compatible("PostgreSQL", "3.6", null, ["postgis"]));
        var runner = Substitute.For<IDatabaseMigrationRunner>();
        runner.RunMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>()).Returns(
            failure == "apply" ? DatabaseMigrationResult.Failed(new InvalidOperationException("never-emit-this"))
                : DatabaseMigrationResult.Succeeded());
        var guard = Substitute.For<IDatabaseSchemaGuard>();
        guard.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            failure == "verify" ? Task.FromException(new InvalidOperationException("never-emit-this")) : Task.CompletedTask);
        await using var services = Services(checker, runner, guard);

        var receipt = await RunAsync(configuration, services, CancellationToken.None);

        receipt.GetProperty("outcome").GetString().Should().NotBe("completed");
        receipt.GetProperty("code").GetString().Should().Be(code);
        receipt.GetProperty("schemaVerified").GetBoolean().Should().BeFalse();
        receipt.GetProperty("appliedScripts").GetArrayLength().Should().Be(0);
        receipt.GetRawText().Should().NotContain("never-emit-this");
        if (failure == "preflight")
        {
            await runner.DidNotReceive().RunMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>());
        }
    }

    [UnitTest]
    public async Task CancelledLaunch_DoesNotProduceCompletedReceipt()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var receipt = await RunAsync(Configuration(), services, new CancellationToken(canceled: true));
        receipt.GetProperty("code").GetString().Should().Be("cancelled");
        receipt.GetProperty("schemaVerified").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    public async Task MinimalComposition_ContainsCanonicalDependenciesAndNoHostedServices()
    {
        var configuration = Configuration();
        var descriptors = MigrateOnlyComposition.Register(new ServiceCollection(), configuration);
        descriptors.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
        await using var services = descriptors.BuildServiceProvider();
        services.GetRequiredService<IDatabaseMigrationRunner>().Should().NotBeNull();
        services.GetRequiredService<IDatabaseSchemaGuard>().Should().NotBeNull();
        services.GetRequiredService<IDatabaseCompatibilityChecker>().Should().NotBeNull();
        services.GetRequiredService<IConnectionSecretResolver>().Should().NotBeNull();
        configuration["ConnectionStrings:DefaultConnection"] = null;
        var receipt = await RunAsync(configuration, services, CancellationToken.None);
        receipt.GetProperty("code").GetString().Should().Be("invalid-configuration");
    }

    [UnitTest]
    public void LocatorHash_ChangesWithTargetButNotCredentialRotation()
    {
        var hash = MigrateOnlyEvidence.ComputeTargetLocatorHash(Connection, "honua");
        MigrateOnlyEvidence.ComputeTargetLocatorHash(Connection.Replace("never-emit-this", "rotated", StringComparison.Ordinal), "honua")
            .Should().Be(hash);
        MigrateOnlyEvidence.ComputeTargetLocatorHash(Connection.Replace("Database=gis", "Database=other", StringComparison.Ordinal), "honua")
            .Should().NotBe(hash);
        MigrateOnlyEvidence.ComputeTargetLocatorHash(Connection, "other_schema").Should().NotBe(hash);
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            [MigrateOnlyMode.SwitchKey] = "true",
            ["ConnectionStrings:DefaultConnection"] = Connection,
        }).Build();

    private static ServiceProvider Services(IDatabaseCompatibilityChecker checker, IDatabaseMigrationRunner runner, IDatabaseSchemaGuard guard)
    {
        runner.PlanMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseMigrationPlan.Succeeded([], [], [], journalIsNonEmpty: false));
        return new ServiceCollection().AddLogging().AddSingleton(checker).AddSingleton(runner).AddSingleton(guard)
            .AddSingleton<IOptions<MigrationSafetyOptions>>(Options.Create(new MigrationSafetyOptions()))
            .AddSingleton(new MigrateOnlyBackupHookRecorder())
            .AddSingleton(Substitute.For<IConnectionSecretResolver>()).BuildServiceProvider();
    }

    private static async Task<JsonElement> RunAsync(IConfiguration configuration, IServiceProvider services, CancellationToken cancellationToken)
    {
        // Refusal and cancellation must finish before any database dependency is resolved.
        var registrations = new ServiceCollection().AddLogging().AddSingleton(new MigrateOnlyBackupHookRecorder());
        await using var fallback = registrations.BuildServiceProvider();
        using var output = new StringWriter();
        var code = await MigrateOnlyCommand.RunWithServicesAsync(configuration, output, new FallbackServices(services, fallback),
            environment: _ => null, cancellationToken: cancellationToken);
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("exitCode").GetInt32().Should().Be(code);
        return document.RootElement.Clone();
    }

    private sealed class FallbackServices(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType) => primary.GetService(serviceType) ?? fallback.GetService(serviceType);
    }
}
