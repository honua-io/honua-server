// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Hosting;
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

    [UnitTest]
    public async Task CompletedReceipt_RequiresPreflightMigrationAndSchemaVerificationInOrder()
    {
        var configuration = Configuration();
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

        var receipt = await MigrationOnlyHost.RunAsync(configuration, services, CancellationToken.None);

        receipt.Outcome.Should().Be("completed");
        receipt.SchemaVerified.Should().BeTrue();
        receipt.AppliedScriptCount.Should().Be(1);
        receipt.PackagedMigrationManifestSha256.Should().MatchRegex("^[a-f0-9]{64}$");
        receipt.TargetLocatorSha256.Should().Be(MigrationOnlyHost.ComputeTargetLocatorHash(Connection, "honua"));
        calls.Should().Equal("preflight", "migrate", "verify");
        JsonSerializer.Serialize(receipt, MigrationRunJsonContext.Default.MigrationRunReceipt)
            .Should().NotContain("never-emit-this").And.NotContain("database.example").And.NotContain("migrator");
    }

    [UnitTheory]
    [InlineData("HONUA_SKIP_MIGRATIONS", "true")]
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
        var receipt = await MigrationOnlyHost.RunAsync(configuration, services, CancellationToken.None);
        receipt.ErrorCode.Should().Be("migration-configuration-invalid");
        receipt.SchemaVerified.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("preflight", "migration-preflight-failed")]
    [InlineData("apply", "migration-apply-failed")]
    [InlineData("verify", "migration-schema-verification-failed")]
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

        var receipt = await MigrationOnlyHost.RunAsync(configuration, services, CancellationToken.None);

        receipt.Outcome.Should().Be("failed");
        receipt.ErrorCode.Should().Be(code);
        receipt.SchemaVerified.Should().BeFalse();
        receipt.AppliedScriptNames.Should().BeNull();
        JsonSerializer.Serialize(receipt, MigrationRunJsonContext.Default.MigrationRunReceipt).Should().NotContain("never-emit-this");
        if (failure == "preflight")
        {
            await runner.DidNotReceive().RunMigrationsAsync(Arg.Any<string>(), Arg.Any<Assembly>(), Arg.Any<CancellationToken>());
        }
    }

    [UnitTest]
    public async Task CancelledLaunch_DoesNotProduceCompletedReceipt()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var receipt = await MigrationOnlyHost.RunAsync(Configuration(), services, new CancellationToken(canceled: true));
        receipt.Outcome.Should().Be("cancelled");
        receipt.SchemaVerified.Should().BeFalse();
    }

    [UnitTest]
    public async Task MinimalComposition_ContainsCanonicalDependenciesAndNoHostedServices()
    {
        var configuration = Configuration();
        var descriptors = MigrationOnlyHost.CreateServices(configuration);
        descriptors.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
        await using var services = descriptors.BuildServiceProvider();
        services.GetRequiredService<IDatabaseMigrationRunner>().Should().NotBeNull();
        services.GetRequiredService<IDatabaseSchemaGuard>().Should().NotBeNull();
        services.GetRequiredService<IDatabaseCompatibilityChecker>().Should().NotBeNull();
        services.GetRequiredService<IConnectionSecretResolver>().Should().NotBeNull();
        configuration["ConnectionStrings:DefaultConnection"] = null;
        var receipt = await MigrationOnlyHost.RunAsync(configuration, services, CancellationToken.None);
        receipt.ErrorCode.Should().Be("migration-connection-unavailable");
    }

    [UnitTest]
    public void LocatorHash_ChangesWithTargetButNotCredentialRotation()
    {
        var hash = MigrationOnlyHost.ComputeTargetLocatorHash(Connection, "honua");
        MigrationOnlyHost.ComputeTargetLocatorHash(Connection.Replace("never-emit-this", "rotated", StringComparison.Ordinal), "honua")
            .Should().Be(hash);
        MigrationOnlyHost.ComputeTargetLocatorHash(Connection.Replace("Database=gis", "Database=other", StringComparison.Ordinal), "honua")
            .Should().NotBe(hash);
        MigrationOnlyHost.ComputeTargetLocatorHash(Connection, "other_schema").Should().NotBe(hash);
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            [MigrationOnlyHost.ModeKey] = "true",
            ["ConnectionStrings:DefaultConnection"] = Connection,
        }).Build();

    private static ServiceProvider Services(IDatabaseCompatibilityChecker checker, IDatabaseMigrationRunner runner, IDatabaseSchemaGuard guard)
        => new ServiceCollection().AddSingleton(checker).AddSingleton(runner).AddSingleton(guard)
            .AddSingleton(Substitute.For<IConnectionSecretResolver>()).BuildServiceProvider();
}
