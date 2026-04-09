// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ObservabilityEndpointsTests : IAsyncLifetime
{
    private readonly StubDatabaseMigrationRunner _migrationRunner = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ObservabilityEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_ReturnsInstanceScopedLifecycleAndPlan()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Succeeded(
            pendingScripts:
            [
                "0001_create_honua_schema.sql",
                "0002_create_service_tables.sql"
            ]);

        _fixture.Services.GetRequiredService<MigrationState>()
            .MarkRunning("Applying 2 pending migration script(s).");

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("running");
        root.GetProperty("isReady").GetBoolean().Should().BeFalse();
        root.GetProperty("isFailed").GetBoolean().Should().BeFalse();
        root.GetProperty("planAvailable").GetBoolean().Should().BeTrue();
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        root.GetProperty("pendingScripts").GetArrayLength().Should().Be(2);
        root.GetProperty("message").GetString().Should().Be("Applying 2 pending migration script(s).");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/migrations")]
    public async Task GetMigrationStatus_WhenPlanFails_ReturnsPlanErrorWithoutThrowing()
    {
        _migrationRunner.Plan = DatabaseMigrationPlan.Failed(new InvalidOperationException("Unable to inspect migrations."));

        _fixture.Services.GetRequiredService<MigrationState>()
            .MarkSucceeded("No pending migration scripts.");

        var response = await _client.GetAsync("/api/v1/admin/observability/migrations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("succeeded");
        root.GetProperty("planAvailable").GetBoolean().Should().BeFalse();
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeFalse();
        root.GetProperty("planError").GetString().Should().Be("Migration planning is temporarily unavailable.");
        root.GetProperty("pendingScripts").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    public async Task GetRecentErrors_WhenEmpty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/errors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("capacity").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_ReturnsTracingEnabledAndEndpointInfo()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/telemetry");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.TryGetProperty("tracingEnabled", out _).Should().BeTrue();
        root.TryGetProperty("otlpConfigured", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/telemetry")]
    public async Task GetTelemetryStatus_WhenTracingDisabled_ReturnsDisabledStatus()
    {
        var disabledFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Tracing:Enabled", "false");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new StubDatabaseMigrationRunner());
            });

        try
        {
            await disabledFixture.InitializeAsync();
            using var client = disabledFixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/observability/telemetry");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("tracingEnabled").GetBoolean().Should().BeFalse();
            root.GetProperty("otlpConfigured").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await disabledFixture.DisposeAsync();
        }
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
