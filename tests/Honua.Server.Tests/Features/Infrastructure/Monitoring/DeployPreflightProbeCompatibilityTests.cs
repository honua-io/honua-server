// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DeployPreflightProbeCompatibilityTests
{
    private static readonly string[] TestExtensions = ["postgis", "postgis_raster", "pgcrypto", "unaccent"];

    [Fact]
    public async Task ProbeAsync_WhenCompatibilityStateAvailable_IncludesDatabaseCompatibility()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua"
            })
            .Build();

        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        var compatibilityState = new DatabaseCompatibilityState();
        compatibilityState.SetResult(DatabaseCompatibilityResult.Compatible(
            "PostgreSQL 16.2",
            "3.4.1",
            "3.4.1",
            TestExtensions));

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new StubMigrationRunner(),
            migrationState,
            compatibilityState);

        var snapshot = await probe.ProbeAsync();

        snapshot.DatabaseCompatibility.Should().NotBeNull();
        snapshot.DatabaseCompatibility!.IsCompatible.Should().BeTrue();
        snapshot.DatabaseCompatibility.EngineVersion.Should().Be("PostgreSQL 16.2");
        snapshot.DatabaseCompatibility.PostGisVersion.Should().Be("3.4.1");
        snapshot.DatabaseCompatibility.PostGisRasterVersion.Should().Be("3.4.1");
        snapshot.DatabaseCompatibility.InstalledExtensions.Should().Contain("postgis");
        snapshot.ReadyForCoordinatedDeploy.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_WhenDatabaseIncompatible_ReportsNotReady()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua"
            })
            .Build();

        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        var compatibilityState = new DatabaseCompatibilityState();
        compatibilityState.SetResult(DatabaseCompatibilityResult.Incompatible(
            "PostGIS extension is not installed. Honua requires PostGIS for spatial operations.",
            engineVersion: "PostgreSQL 16.2",
            installedExtensions: ["pgcrypto", "unaccent"]));

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new StubMigrationRunner(),
            migrationState,
            compatibilityState);

        var snapshot = await probe.ProbeAsync();

        snapshot.ReadyForCoordinatedDeploy.Should().BeFalse();
        snapshot.Status.Should().Be("blocked");
        snapshot.Message.Should().Contain("PostGIS");
        snapshot.DatabaseCompatibility.Should().NotBeNull();
        snapshot.DatabaseCompatibility!.IsCompatible.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_WhenCompatibilityStateUnavailable_OmitsDatabaseCompatibility()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua"
            })
            .Build();

        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        var compatibilityState = new DatabaseCompatibilityState();
        // Do not set any result — simulates preflight not having run

        var probe = new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new StubMigrationRunner(),
            migrationState,
            compatibilityState);

        var snapshot = await probe.ProbeAsync();

        snapshot.DatabaseCompatibility.Should().BeNull();
    }

    private sealed class StubReadinessCheckService : IReadinessCheckService
    {
        public Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadinessResult.Ready());
    }

    private sealed class StubMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}
