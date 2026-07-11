// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    private static readonly DateTimeOffset TestNow = DateTimeOffset.UtcNow;

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
            })
            // The shared test configuration defaults the ops-health rollup sampler OFF for every
            // integration host (it would otherwise add background Postgres load across the whole
            // parallel test fleet). This suite owns the rollup surface, so it opts back in —
            // PostConfigure wins over the config binding — keeping the enabled/hosted sampler path
            // booted by at least one host. The sampler's initial jitter + flush interval keep it from
            // flushing mid-test; the history assertions seed the store directly instead.
            .ConfigureServices(services =>
            {
                services.PostConfigure<Honua.Infrastructure.Monitoring.OpsHealthRollupOptions>(
                    options => options.Enabled = true);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(TestNow));
                services.RemoveAll<IAlertDispatchHealth>();
                services.AddSingleton<IAlertDispatchHealth>(new FixedAlertDispatchHealth(
                    new AlertDispatchBacklog
                    {
                        PendingCount = 4,
                        RetryingCount = 2,
                        DeadLetteredCount = 2,
                        OldestItemAt = TestNow.AddMinutes(-10),
                        Channels =
                        [
                            new AlertDispatchChannelBacklog
                            {
                                ChannelType = AlertChannelType.Webhook,
                                IsPaused = false,
                                PendingCount = 2,
                                RetryingCount = 1,
                                DeadLetteredCount = 0,
                                OldestItemAt = TestNow.AddMinutes(-2),
                            },
                            new AlertDispatchChannelBacklog
                            {
                                ChannelType = AlertChannelType.Email,
                                IsPaused = true,
                                PendingCount = 0,
                                RetryingCount = 1,
                                DeadLetteredCount = 2,
                                OldestItemAt = TestNow.AddMinutes(-10),
                            },
                        ],
                    }));
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
    [Trait("Tier", "Fast")]
    [Endpoint("GET /api/v1/admin/observability/ops-health")]
    public async Task GetOpsHealth_WithMixedChannelHealth_SerializesExactPrivacySafeProjection()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/ops-health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var dispatch = json.RootElement.GetProperty("alertDispatch");
        dispatch.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            [
                "dispatcherRunning",
                "dispatcherEnabled",
                "storagePollFailing",
                "lastPollAt",
                "pendingCount",
                "deadLetteredCount",
                "retryingCount",
                "oldestItemAgeSeconds",
                "channels",
            ]);
        dispatch.GetProperty("pendingCount").GetInt64().Should().Be(4);
        dispatch.GetProperty("retryingCount").GetInt64().Should().Be(2);
        dispatch.GetProperty("deadLetteredCount").GetInt64().Should().Be(2);
        dispatch.GetProperty("oldestItemAgeSeconds").GetInt64().Should().Be(600);

        var channels = dispatch.GetProperty("channels");
        channels.GetArrayLength().Should().Be(2);
        AssertChannel(channels[0], "webhook", paused: false, pending: 2, retrying: 1, deadLettered: 0, oldestAge: 120);
        AssertChannel(channels[1], "email", paused: true, pending: 0, retrying: 1, deadLettered: 2, oldestAge: 600);

        foreach (var forbiddenProperty in new[] { "destination", "payload", "error", "credential", "secret" })
        {
            content.Contains($"\"{forbiddenProperty}\"", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
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

    private static void AssertChannel(
        JsonElement channel,
        string name,
        bool paused,
        long pending,
        long retrying,
        long deadLettered,
        long oldestAge)
    {
        channel.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            ["channel", "paused", "pendingCount", "retryingCount", "deadLetteredCount", "oldestItemAgeSeconds"]);
        channel.GetProperty("channel").GetString().Should().Be(name);
        channel.GetProperty("paused").GetBoolean().Should().Be(paused);
        channel.GetProperty("pendingCount").GetInt64().Should().Be(pending);
        channel.GetProperty("retryingCount").GetInt64().Should().Be(retrying);
        channel.GetProperty("deadLetteredCount").GetInt64().Should().Be(deadLettered);
        channel.GetProperty("oldestItemAgeSeconds").GetInt64().Should().Be(oldestAge);
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

    private sealed class FixedAlertDispatchHealth(AlertDispatchBacklog backlog) : IAlertDispatchHealth
    {
        public bool IsDispatcherRunning => true;

        public bool IsDispatcherEnabled => true;

        public DateTimeOffset? LastPollAt => TestNow;

        public AlertDispatchBacklog? LastBacklog => backlog;

        public bool IsStoragePollFailing => false;

        public Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(backlog);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
