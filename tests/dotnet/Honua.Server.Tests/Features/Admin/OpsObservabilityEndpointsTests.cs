// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the admin ops-health snapshot and ops-findings endpoints
/// (ADR-0060 WS4 / epic #2457). Compile locally; the full assertions run in CI (Testcontainers).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.HealthCheck)]
public sealed class OpsObservabilityEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "ops-observability-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OpsObservabilityEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/ops-health")]
    public async Task GetOpsHealth_WithAdminAuth_ReturnsComposedSnapshot()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/ops-health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.TryGetProperty("generatedAt", out _).Should().BeTrue();
        root.GetProperty("overallStatus").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("health").TryGetProperty("entries", out var entries).Should().BeTrue();
        entries.ValueKind.Should().Be(JsonValueKind.Array);
        root.TryGetProperty("servingLatency", out var latency).Should().BeTrue();
        latency.TryGetProperty("windowSeconds", out _).Should().BeTrue();
        root.GetProperty("geoprocessing").TryGetProperty("totalActive", out _).Should().BeTrue();
        root.TryGetProperty("alertDispatch", out _).Should().BeTrue();
        root.GetProperty("deploy").TryGetProperty("readyForCoordinatedDeploy", out _).Should().BeTrue();
        root.GetProperty("database").TryGetProperty("cacheHitRatio", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/ops-health")]
    public async Task GetOpsHealth_WithoutAdminAuth_IsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/admin/observability/ops-health");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/ops-health/history")]
    public async Task GetOpsHealthHistory_WithAdminAuth_ReturnsClusterAggregatedSeries()
    {
        // Seed one persisted flush through the app's rollup store (when the Postgres store is registered)
        // so the read path returns a non-empty series including the GP queue breakdown.
        await SeedRollupSampleAsync();

        var response = await _client.GetAsync("/api/v1/admin/observability/ops-health/history?window=1h&resolution=1m");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("resolution").GetString().Should().Be("1m");
        root.GetProperty("perReplica").GetBoolean().Should().BeFalse();
        root.GetProperty("windowSeconds").GetDouble().Should().BeGreaterThan(0);
        root.GetProperty("latency").ValueKind.Should().Be(JsonValueKind.Array);
        var vitals = root.GetProperty("vitals");
        vitals.ValueKind.Should().Be(JsonValueKind.Array);
        vitals.GetArrayLength().Should().BeGreaterThan(0);
        var point = vitals[0];
        point.GetProperty("gpQueueTotal").GetInt32().Should().Be(4);
        var breakdown = point.GetProperty("gpQueueBreakdown");
        breakdown.ValueKind.Should().Be(JsonValueKind.Object);
        breakdown.GetProperty("Queued|local").GetInt32().Should().Be(3);
        breakdown.GetProperty("Running|local").GetInt32().Should().Be(1);
    }

    private async Task SeedRollupSampleAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOpsHealthRollupStore>();
        await store.WriteSampleAsync(new OpsHealthRollupSample
        {
            ReplicaId = "test-replica",
            CapturedAt = DateTimeOffset.UtcNow,
            Latency = [],
            Vitals = new OpsHealthVitalsPoint
            {
                OverallStatus = "Healthy",
                GpQueueTotal = 4,
                GpQueueBreakdown = new Dictionary<string, int>
                {
                    ["Queued|local"] = 3,
                    ["Running|local"] = 1,
                },
                DbActiveConnections = 2,
                CacheHitRatio = 0.9,
                ErrorRate = 0.0,
            },
        });
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/ops-health/history")]
    public async Task GetOpsHealthHistory_WithInvalidResolution_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/ops-health/history?resolution=13m");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/ops-health/history")]
    public async Task GetOpsHealthHistory_WithoutAdminAuth_IsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/admin/observability/ops-health/history");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/findings")]
    public async Task GetFindings_WithAdminAuth_ReturnsFindingsEnvelope()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/findings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("generatedAt", out _).Should().BeTrue();
        json.RootElement.GetProperty("findings").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/findings")]
    public async Task GetFindings_WithoutAdminAuth_IsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/admin/observability/findings");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/observability/findings/{findingId}/propose")]
    public async Task ProposeFinding_UnknownId_Returns404()
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/observability/findings/does-not-exist/propose",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/observability/findings/{findingId}/propose")]
    public async Task ProposeFinding_WithoutAdminAuth_IsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.PostAsync(
            "/api/v1/admin/observability/findings/does-not-exist/propose",
            content: null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
