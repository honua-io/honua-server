// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Monitoring;
using Honua.ServiceDefaults;
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
    public async Task ConnectionTimeouts_KeepDatabaseHealthGreenWithKnownPoolUtilization()
    {
        using (var scope = _fixture.Services.CreateScope())
        {
            var metricsCollector = scope.ServiceProvider.GetRequiredService<ProductionMetricsCollector>();
            var connectionPoolMetrics = scope.ServiceProvider.GetRequiredService<ConnectionPoolMetrics>();

            // CachingDatabaseConnectionProvider's constructor now publishes the concurrency-gate ceiling
            // as the pool size (UpdatePoolSize), so once any scoped provider has been constructed in this
            // process, utilization telemetry is available -- it is no longer permanently "unknown".
            connectionPoolMetrics.TryGetPoolUtilization(out var initialUtilization).Should().BeTrue();
            initialUtilization.Should().BeInRange(0.0, 1.0);

            var initialTimeouts = connectionPoolMetrics.GetTotalTimeouts();

            connectionPoolMetrics.RecordConnectionTimeout();

            connectionPoolMetrics.RecordConnectionTimeout();

            var expectedTimeouts = initialTimeouts + 2;
            var directHealthMetrics = metricsCollector.GetHealthMetrics();

            directHealthMetrics.ConnectionAcquisitionTimeouts.Should().Be(expectedTimeouts);
            directHealthMetrics.ConnectionAcquisitionFailures.Should().Be(0);
            directHealthMetrics.HasDatabaseConnectionPoolUtilization.Should().BeTrue();
            // Connection timeouts are cumulative counters only; they never feed the utilization ratio
            // itself (ConnectionPoolMetrics.TryGetPoolUtilization derives it purely from active/pool size).
            directHealthMetrics.DatabaseConnectionPoolUtilization.Should().BeInRange(0.0, 1.0);

            using var adminClient = _fixture.CreateAdminClient();

            var connectionPoolResponse = await adminClient.GetAsync("/monitoring/metrics/connection-pool");
            connectionPoolResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using (var document = JsonDocument.Parse(await connectionPoolResponse.Content.ReadAsStringAsync()))
            {
                var root = document.RootElement;
                root.GetProperty("totalTimeouts").GetInt64().Should().Be(expectedTimeouts);
                root.GetProperty("hasUtilizationData").GetBoolean().Should().BeTrue();
                root.GetProperty("utilization").GetDouble().Should().BeInRange(0.0, 1.0);
                root.GetProperty("utilizationStatus").GetString().Should().Be("available");
                root.GetProperty("utilizationPercentage").GetString().Should().NotBe("unavailable");

                // ResolveConnectionPoolHealthStatus keys solely off utilization (<=0.8 => Healthy); the
                // effective pool ceiling in this test process is the QueryConcurrencyGate.MaxLimit default
                // (200), so a handful of active connections in a single-fixture integration run stays well
                // under the 0.8 degraded threshold.
                root.GetProperty("healthStatus").GetString().Should().Be("Healthy");
                root.GetProperty("isHealthy").GetBoolean().Should().BeTrue();
            }

            var resilienceResponse = await adminClient.GetAsync("/monitoring/metrics/database-resilience");
            resilienceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var resilienceDocument = JsonDocument.Parse(await resilienceResponse.Content.ReadAsStringAsync());
            var pool = resilienceDocument.RootElement.GetProperty("connectionPool");
            pool.GetProperty("isHealthy").GetBoolean().Should().BeTrue();
            pool.GetProperty("healthStatus").GetString().Should().Be("Healthy");
            pool.GetProperty("hasUtilizationData").GetBoolean().Should().BeTrue();

            // Telemetry is no longer unavailable, and utilization is far below both the 0.8/0.9
            // warning/critical thresholds in GetDatabaseAlerts, so none of the pool-utilization alert
            // variants (unavailable/high/critical) should be present.
            resilienceDocument.RootElement
                .GetProperty("alerts")
                .EnumerateArray()
                .Select(static alert => alert.GetString())
                .Should()
                .NotContain(alert => alert != null && alert.Contains("connection pool utilization", StringComparison.Ordinal));


            var healthResponse = await adminClient.GetAsync("/monitoring/health/comprehensive");
            healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var healthDocument = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            var healthRoot = healthDocument.RootElement;
            var databaseEntry = healthRoot.GetProperty("entries").GetProperty("database");
            databaseEntry.GetProperty("status").GetString().Should().Be("Healthy");

            var data = databaseEntry.GetProperty("data");
            data.GetProperty("connectionTimeouts").GetInt64().Should().Be(expectedTimeouts);
            data.GetProperty("connectionFailures").GetInt64().Should().Be(0);
            // DatabaseHealthCheck now reports the real ratio (ConnectionPoolMetrics.GetPoolUtilization()),
            // not a hardcoded 0 -- assert it is a valid ratio rather than locking in the old "always 0".
            data.GetProperty("poolUtilization").GetDouble().Should().BeInRange(0.0, 1.0);
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

        // ErrorRate (#2809) is no longer TotalErrors/TotalQueries; ProductionMetricsCollector.GetHealthMetrics
        // now sources it from CalculateWindowedErrorRate(), which sums RequestCount/ErrorCount across all
        // protocols in HonuaTelemetry's rolling serving-latency window. RecordHttpRequest (below) only
        // updates the IHttpRequestMetricsSnapshotProvider counters, not that window, so the windowed rate
        // must be driven directly via HonuaTelemetry.RecordServingRequest -- the same call the real request
        // pipeline makes -- and the expectation derived from a before/after snapshot of that window.
        long windowBaselineRequests = 0;
        long windowBaselineErrors = 0;
        foreach (var protocol in HonuaTelemetry.GetServingLatencySnapshot().Protocols)
        {
            windowBaselineRequests += protocol.RequestCount;
            windowBaselineErrors += protocol.ErrorCount;
        }

        performanceMonitor.RecordHttpRequest("GET", "/collections", StatusCodes.Status200OK, TimeSpan.FromMilliseconds(12));
        performanceMonitor.RecordHttpRequest("GET", "/collections/places/items", StatusCodes.Status500InternalServerError, TimeSpan.FromMilliseconds(37));
        performanceMonitor.RecordHttpRequest("GET", "/collections/places/items", StatusCodes.Status404NotFound, TimeSpan.FromMilliseconds(19));

        HonuaTelemetry.RecordServingRequest("ogc-api", "items", StatusCodes.Status200OK, 12);
        HonuaTelemetry.RecordServingRequest("ogc-api", "items", StatusCodes.Status500InternalServerError, 37);
        HonuaTelemetry.RecordServingRequest("ogc-api", "items", StatusCodes.Status404NotFound, 19);

        var expectedTotalRequests = baseline.TotalRequests + 3;
        var expectedServerErrors = baseline.TotalServerErrors + 1;

        long windowAfterRequests = 0;
        long windowAfterErrors = 0;
        foreach (var protocol in HonuaTelemetry.GetServingLatencySnapshot().Protocols)
        {
            windowAfterRequests += protocol.RequestCount;
            windowAfterErrors += protocol.ErrorCount;
        }

        var expectedErrorRate = windowAfterRequests > 0
            ? (double)windowAfterErrors / windowAfterRequests
            : 0.0;

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
