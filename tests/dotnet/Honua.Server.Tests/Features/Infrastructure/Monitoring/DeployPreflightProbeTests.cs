// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.HealthCheck;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DeployPreflightProbeTests
{
    [Fact]
    public async Task ProbeAsync_WhenConnectionStringIsSecretReference_ResolvesBeforePlanning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "aws:secretsmanager:test-db"
            })
            .Build();
        var migrationRunner = new CapturingMigrationRunner();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            migrationRunner,
            migrationState,
            new DatabaseCompatibilityState(),
            NullLogger<DeployPreflightProbe>.Instance,
            new StubConnectionSecretResolver("aws:secretsmanager:test-db", "Host=resolved;Database=honua;Username=test;Password=secret"));

        var snapshot = await probe.ProbeAsync();

        snapshot.ReadyForCoordinatedDeploy.Should().BeTrue();
        snapshot.Migration.PlanAvailable.Should().BeTrue();
        migrationRunner.LastConnectionString.Should().Be("Host=resolved;Database=honua;Username=test;Password=secret");
    }

    [Fact]
    public async Task ProbeAsync_WhenSecretResolutionFails_ReturnsPlanErrorWithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "aws:secretsmanager:test-db"
            })
            .Build();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new CapturingMigrationRunner(),
            migrationState,
            new DatabaseCompatibilityState(),
            NullLogger<DeployPreflightProbe>.Instance,
            new ThrowingConnectionSecretResolver());

        var snapshot = await probe.ProbeAsync();

        snapshot.ReadyForCoordinatedDeploy.Should().BeFalse();
        snapshot.Migration.PlanAvailable.Should().BeFalse();
        snapshot.Migration.PlanError.Should().Be("Migration planning is temporarily unavailable.");
    }

    [Fact]
    public async Task ProbeAsync_WhenPendingContractMigration_BlocksCoordinatedDeploy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=db;Database=honua;Username=test;Password=secret"
            })
            .Build();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        // A pending, annotated (reviewed) contract migration: it is rollout-safe to apply in a
        // dedicated contract step, but it must NOT ride along a rolling deploy.
        var contract = MigrationSafetyClassifier.Classify(
            "099_drop_legacy.sql",
            """
            -- honua:compatibility-review reviewer=jane.doe ticket=honua-server#2812 reason=legacy column removed after v2 contract phase
            ALTER TABLE honua.layers DROP COLUMN legacy_name;
            """);
        contract.Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);

        var plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts: new[] { contract.ScriptName },
            executedButNotDiscoveredScripts: null,
            pendingScriptClassifications: new[] { contract });

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new FixedPlanMigrationRunner(plan),
            migrationState,
            new DatabaseCompatibilityState(),
            NullLogger<DeployPreflightProbe>.Instance);

        var snapshot = await probe.ProbeAsync();

        snapshot.ReadyForCoordinatedDeploy.Should().BeFalse();
        snapshot.Migration.HasPendingContractScripts.Should().BeTrue();
        snapshot.Migration.PendingContractScripts.Should().Contain("099_drop_legacy.sql");
        snapshot.Message.Should().Contain("contract-phase");
    }

    private sealed class StubReadinessCheckService : IReadinessCheckService
    {
        public Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadinessResult.Ready());
    }

    private sealed class FixedPlanMigrationRunner(DatabaseMigrationPlan plan) : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(plan);

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }

    private sealed class CapturingMigrationRunner : IDatabaseMigrationRunner
    {
        public string? LastConnectionString { get; private set; }

        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
        {
            LastConnectionString = connectionString;
            return Task.FromResult(DatabaseMigrationPlan.Succeeded());
        }

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }

    private sealed class StubConnectionSecretResolver(string secretRef, string resolvedConnectionString) : IConnectionSecretResolver
    {
        public string ProviderName => "aws";

        public Task<string?> ResolveSecretAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(candidate == secretRef ? resolvedConnectionString : null);

        public bool CanResolve(string candidate)
            => candidate == secretRef;

        public Task<string> ResolveConnectionStringAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(candidate == secretRef ? resolvedConnectionString : candidate);
    }

    private sealed class ThrowingConnectionSecretResolver : IConnectionSecretResolver
    {
        public string ProviderName => "aws";

        public Task<string?> ResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromException<string?>(new InvalidOperationException("resolver failure"));

        public bool CanResolve(string secretKey)
            => true;

        public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("resolver failure"));
    }
}
