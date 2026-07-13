// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for database performance monitoring endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public class DatabasePerformanceEndpointsTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public DatabasePerformanceEndpointsTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /api/v1/admin/performance/database/query-cache/statistics")]
    public async Task GetQueryCacheStatistics_ReturnsOkOrUnauthorized()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/performance/database/query-cache/statistics");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected OK or Unauthorized, got {response.StatusCode}.");
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/production")]
    [Endpoint("GET /monitoring/health/comprehensive")]
    [Endpoint("GET /monitoring/metrics/connection-pool")]
    [Endpoint("GET /monitoring/metrics/database-resilience")]
    public async Task PoolTelemetry_IsSourcedFromLiveAdmissionGate()
    {
        // Regression guard for honua-server#2805: pool utilization + acquisition-timeout telemetry
        // must come from the live admission gate (IDatabaseAdmissionPressureSource), not the former
        // decorative counters that no production code ever wrote. On the Postgres-backed fixture the
        // gate is registered, so utilization is *available* (not "unavailable"/"Unknown") and the
        // windowed timeout count is a real, honest signal.
        using (var scope = _fixture.Services.CreateScope())
        {
            var pressureSource = scope.ServiceProvider.GetService<IDatabaseAdmissionPressureSource>();
            pressureSource.Should().NotBeNull("the Postgres provider must expose the admission gate as the pressure source");

            var metricsCollector = scope.ServiceProvider.GetRequiredService<ProductionMetricsCollector>();
            var connectionPoolMetrics = scope.ServiceProvider.GetRequiredService<ConnectionPoolMetrics>();

            // Utilization is now available (gate-backed) and honestly bounded to [0,1].
            connectionPoolMetrics.TryGetPoolUtilization(out var utilization).Should().BeTrue();
            utilization.Should().BeInRange(0d, 1d);
            connectionPoolMetrics.GetTotalTimeouts().Should().BeGreaterThanOrEqualTo(0);

            var directHealthMetrics = metricsCollector.GetHealthMetrics();
            directHealthMetrics.HasDatabaseConnectionPoolUtilization.Should().BeTrue();
            directHealthMetrics.DatabaseConnectionPoolUtilization.Should().BeInRange(0d, 1d);

            using var adminClient = _fixture.CreateAdminClient();

            var connectionPoolResponse = await adminClient.GetAsync("/monitoring/metrics/connection-pool");
            connectionPoolResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using (var document = JsonDocument.Parse(await connectionPoolResponse.Content.ReadAsStringAsync()))
            {
                var root = document.RootElement;
                root.GetProperty("totalTimeouts").GetInt64().Should().BeGreaterThanOrEqualTo(0);
                root.GetProperty("hasUtilizationData").GetBoolean().Should().BeTrue();
                root.GetProperty("utilizationStatus").GetString().Should().Be("available");
                root.GetProperty("utilization").GetDouble().Should().BeInRange(0d, 1d);
                root.GetProperty("healthStatus").GetString().Should().NotBe("Unknown");
                // The query-admission block mirrors the same gate the pressure source reads.
                root.GetProperty("queryAdmission").ValueKind.Should().Be(JsonValueKind.Object);
            }

            var resilienceResponse = await adminClient.GetAsync("/monitoring/metrics/database-resilience");
            resilienceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var resilienceDocument = JsonDocument.Parse(await resilienceResponse.Content.ReadAsStringAsync());
            var pool = resilienceDocument.RootElement.GetProperty("connectionPool");
            pool.GetProperty("hasUtilizationData").GetBoolean().Should().BeTrue();
            pool.GetProperty("healthStatus").GetString().Should().NotBe("Unknown");
            resilienceDocument.RootElement
                .GetProperty("alerts")
                .EnumerateArray()
                .Select(static alert => alert.GetString())
                .Should()
                .NotContain("Warning: Database connection pool utilization telemetry is unavailable.");

            var healthResponse = await adminClient.GetAsync("/monitoring/health/comprehensive");
            healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var healthDocument = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            var healthRoot = healthDocument.RootElement;
            var databaseEntry = healthRoot.GetProperty("entries").GetProperty("database");
            databaseEntry.GetProperty("status").GetString().Should().Be("Healthy");

            var data = databaseEntry.GetProperty("data");
            data.GetProperty("connectionTimeouts").GetInt64().Should().BeGreaterThanOrEqualTo(0);
            data.GetProperty("connectionFailures").GetInt64().Should().Be(0);
            data.GetProperty("poolUtilization").GetDouble().Should().BeInRange(0d, 1d);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Cache)]
    [Endpoint("GET /monitoring/metrics/cache")]
    public async Task CacheMetrics_ReflectLiveCacheTelemetry()
    {
        using var scope = _fixture.Services.CreateScope();
        var metricsCollector = scope.ServiceProvider.GetRequiredService<ProductionMetricsCollector>();
        var performanceMonitor = scope.ServiceProvider.GetRequiredService<IPerformanceMonitor>();
        var cacheSnapshotProvider = scope.ServiceProvider.GetRequiredService<ICacheMetricsSnapshotProvider>();
        var baseline = cacheSnapshotProvider.GetCacheMetricsSnapshot();

        performanceMonitor.RecordCacheMetrics("response-cache", "hit");
        performanceMonitor.RecordCacheMetrics("response-cache", "hit");
        performanceMonitor.RecordCacheMetrics("response-cache", "miss");

        var expectedHits = baseline.TotalHits + 2;
        var expectedMisses = baseline.TotalMisses + 1;
        var expectedHitRatio = expectedHits + expectedMisses > 0
            ? (double)expectedHits / (expectedHits + expectedMisses)
            : 0.0;

        metricsCollector.GetHealthMetrics().CacheHitRatio.Should().BeApproximately(expectedHitRatio, 0.000001);

        using var adminClient = _fixture.CreateAdminClient();
        var cacheResponse = await adminClient.GetAsync("/monitoring/metrics/cache");
        cacheResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await cacheResponse.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("hitRatio").GetDouble().Should().BeApproximately(expectedHitRatio, 0.000001);
        root.GetProperty("isHealthy").GetBoolean().Should().Be(expectedHitRatio >= 0.8);
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/production")]
    public async Task ProductionHealthMetrics_ReflectLiveHttpRequestTelemetry()
    {
        using var scope = _fixture.Services.CreateScope();
        var metricsCollector = scope.ServiceProvider.GetRequiredService<ProductionMetricsCollector>();
        var performanceMonitor = scope.ServiceProvider.GetRequiredService<IPerformanceMonitor>();
        var httpRequestSnapshotProvider = scope.ServiceProvider.GetRequiredService<IHttpRequestMetricsSnapshotProvider>();
        var baseline = httpRequestSnapshotProvider.GetHttpRequestMetricsSnapshot();

        performanceMonitor.RecordHttpRequest("GET", "/collections", StatusCodes.Status200OK, TimeSpan.FromMilliseconds(12));
        performanceMonitor.RecordHttpRequest("GET", "/collections/places/items", StatusCodes.Status500InternalServerError, TimeSpan.FromMilliseconds(37));
        performanceMonitor.RecordHttpRequest("GET", "/collections/places/items", StatusCodes.Status404NotFound, TimeSpan.FromMilliseconds(19));

        var expectedTotalRequests = baseline.TotalRequests + 3;
        var expectedServerErrors = baseline.TotalServerErrors + 1;
        var expectedErrorRate = (double)expectedServerErrors / expectedTotalRequests;

        var directHealthMetrics = metricsCollector.GetHealthMetrics();
        directHealthMetrics.TotalQueries.Should().Be(expectedTotalRequests);
        directHealthMetrics.TotalErrors.Should().Be(expectedServerErrors);
        directHealthMetrics.ErrorRate.Should().BeApproximately(expectedErrorRate, 0.000001);

        using var adminClient = _fixture.CreateAdminClient();
        var response = await adminClient.GetAsync("/monitoring/health/production");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var metrics = document.RootElement.GetProperty("metrics");
        metrics.GetProperty("totalQueries").GetInt64().Should().Be(expectedTotalRequests);
        metrics.GetProperty("totalErrors").GetInt64().Should().Be(expectedServerErrors);
        metrics.GetProperty("errorRate").GetDouble().Should().BeApproximately(expectedErrorRate, 0.000001);
    }

    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/metrics/resources")]
    public async Task ResourceMetrics_ReturnsProcessSnapshot()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var response = await adminClient.GetAsync("/monitoring/metrics/resources");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("memoryUsageBytes").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("memoryUsageMB").GetInt64().Should().BeGreaterOrEqualTo(0);
        root.GetProperty("memoryPressureLevel").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("gcInfo").GetProperty("totalMemory").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("isHealthy").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /monitoring/alerts")]
    public async Task AlertsEndpoint_ReturnsConsistentAlertSnapshot()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var response = await adminClient.GetAsync("/monitoring/alerts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var hasActiveAlerts = root.GetProperty("hasActiveAlerts").GetBoolean();
        var alertCount = root.GetProperty("alertCount").GetInt32();
        var alerts = root.GetProperty("alerts").EnumerateArray().ToArray();

        alertCount.Should().Be(alerts.Length);
        hasActiveAlerts.Should().Be(alertCount > 0);
        root.GetProperty("timestamp").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
    }
}
