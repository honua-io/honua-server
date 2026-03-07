// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class DeployControlEndpointsTests : IAsyncLifetime
{
    private readonly StubDatabaseMigrationRunner _migrationRunner = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public DeployControlEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(_migrationRunner);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WhenInstanceIsAligned_ReturnsReady()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded();

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("readyForCoordinatedDeploy").GetBoolean().Should().BeTrue();
        root.GetProperty("deploymentMode").GetString().Should().Be("SingleInstance");
        root.GetProperty("migration").GetProperty("planAvailable").GetBoolean().Should().BeTrue();
        root.GetProperty("migration").GetProperty("upgradeRequired").GetBoolean().Should().BeFalse();
        root.GetProperty("readiness").GetProperty("isReady").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/deploy/preflight")]
    public async Task GetDeployPreflight_WhenPendingMigrationsExist_ReturnsBlocked()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts:
            [
                "0003_add_service_metadata.sql"
            ]);

        var response = await _client.GetAsync("/api/v1/admin/deploy/preflight");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("blocked");
        root.GetProperty("readyForCoordinatedDeploy").GetBoolean().Should().BeFalse();
        root.GetProperty("message").GetString().Should().Be("Pending migrations must be reconciled before coordinated deployment.");
        root.GetProperty("migration").GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        root.GetProperty("migration").GetProperty("pendingScripts").GetArrayLength().Should().Be(1);
    }

    private sealed class StubDatabaseMigrationRunner : IDatabaseMigrationRunner
    {
        public DatabaseMigrationPlan Plan { get; set; } = DatabaseMigrationPlan.Succeeded();

        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Plan);

        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString,
            Assembly migrationsAssembly,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}
