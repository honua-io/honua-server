// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

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
            new ThrowingConnectionSecretResolver());

        var snapshot = await probe.ProbeAsync();

        snapshot.ReadyForCoordinatedDeploy.Should().BeFalse();
        snapshot.Migration.PlanAvailable.Should().BeFalse();
        snapshot.Migration.PlanError.Should().Be("resolver failure");
    }

    private sealed class StubReadinessCheckService : IReadinessCheckService
    {
        public Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadinessResult.Ready());
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
        public Task<string> ResolveConnectionStringAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(candidate == secretRef ? resolvedConnectionString : candidate);

        public Task<bool> CanResolveSecretAsync(string candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(candidate == secretRef);

        public string[] GetSupportedProviders() => ["aws"];
    }

    private sealed class ThrowingConnectionSecretResolver : IConnectionSecretResolver
    {
        public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("resolver failure"));

        public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public string[] GetSupportedProviders() => ["aws"];
    }
}
