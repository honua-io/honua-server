// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// The deploy preflight diagnostics report the database session-settings mode and the
/// role-defaults verification, without letting a mismatch block deploys (honua-server#5870).
/// </summary>
public sealed class DeployPreflightProbeSessionSettingsTests
{
    [UnitTest]
    public async Task ProbeAsync_RoleDefaultsMismatch_ReportsModeAndRemediationWithoutBlocking()
    {
        var state = new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.RoleDefaults);
        state.SetSnapshot(new DatabaseSessionSettingsSnapshot
        {
            Mode = DatabaseSessionSettingsMode.RoleDefaults,
            Status = DatabaseSessionSettingsVerificationStatus.Mismatch,
            Role = "honua",
            Drift =
            [
                new DatabaseSessionSettingDrift(
                    "lock_timeout", "30s", "0 (disabled)", "ALTER ROLE \"honua\" SET lock_timeout = '30s';"),
            ],
        });

        var snapshot = await CreateProbe(state).ProbeAsync();

        snapshot.DatabaseSessionSettings.Should().NotBeNull();
        snapshot.DatabaseSessionSettings!.Mode.Should().Be(DatabaseSessionSettingsMode.RoleDefaults);
        snapshot.DatabaseSessionSettings.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Mismatch);
        snapshot.DatabaseSessionSettings.Drift.Should().ContainSingle()
            .Which.Remediation.Should().Be("ALTER ROLE \"honua\" SET lock_timeout = '30s';");
        snapshot.ReadyForCoordinatedDeploy.Should().BeTrue("the role-defaults check is advisory");
    }

    [UnitTest]
    public async Task ProbeAsync_ConnectionMode_ReportsNotApplicable()
    {
        var snapshot = await CreateProbe(new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.Connection)).ProbeAsync();

        snapshot.DatabaseSessionSettings!.Mode.Should().Be(DatabaseSessionSettingsMode.Connection);
        snapshot.DatabaseSessionSettings.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.NotApplicable);
    }

    [UnitTest]
    public async Task ProbeAsync_NoPostgresServices_OmitsSessionSettings()
    {
        var snapshot = await CreateProbe(sessionSettingsState: null).ProbeAsync();

        snapshot.DatabaseSessionSettings.Should().BeNull();
    }

    private static DeployPreflightProbe CreateProbe(DatabaseSessionSettingsState? sessionSettingsState)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua",
            })
            .Build();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();

        return new DeployPreflightProbe(
            configuration,
            new StubReadinessCheckService(),
            new StubMigrationRunner(),
            migrationState,
            new DatabaseCompatibilityState(),
            NullLogger<DeployPreflightProbe>.Instance,
            sessionSettingsState: sessionSettingsState);
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
