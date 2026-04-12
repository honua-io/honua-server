// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for database performance monitoring endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
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
    public async Task ConnectionTimeouts_DegradeProductionHealthAndPoolMetrics()
    {
        using (var scope = _fixture.Services.CreateScope())
        {
            var metricsCollector = scope.ServiceProvider.GetRequiredService<ProductionMetricsCollector>();
            var connectionPoolMetrics = scope.ServiceProvider.GetRequiredService<ConnectionPoolMetrics>();
            var initialTimeouts = connectionPoolMetrics.GetTotalTimeouts();

            connectionPoolMetrics.UpdatePoolSize(10);
            connectionPoolMetrics.RecordConnectionTimeout();
            for (var i = 0; i < 25; i++)
            {
                metricsCollector.RecordCacheHit("query");
            }

            connectionPoolMetrics.RecordConnectionTimeout();

            var expectedTimeouts = initialTimeouts + 2;

            using var adminClient = _fixture.CreateAdminClient();

            var connectionPoolResponse = await adminClient.GetAsync("/monitoring/metrics/connection-pool");
            connectionPoolResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using (var document = JsonDocument.Parse(await connectionPoolResponse.Content.ReadAsStringAsync()))
            {
                var root = document.RootElement;
                root.GetProperty("totalTimeouts").GetInt64().Should().Be(expectedTimeouts);
                root.GetProperty("isHealthy").GetBoolean().Should().BeFalse();
            }

            var healthResponse = await adminClient.GetAsync("/monitoring/health/production");
            healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var healthDocument = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            var healthRoot = healthDocument.RootElement;
            healthRoot.GetProperty("isHealthy").GetBoolean().Should().BeFalse();
            healthRoot.GetProperty("overallStatus").GetString().Should().Be("Degraded");

            var metrics = healthRoot.GetProperty("metrics");
            metrics.GetProperty("connectionAcquisitionTimeouts").GetInt64().Should().Be(expectedTimeouts);
            metrics.GetProperty("connectionAcquisitionFailures").GetInt64().Should().Be(0);

            var alerts = healthRoot.GetProperty("alertConditions").EnumerateArray().Select(element => element.GetString()).ToArray();
            alerts.Should().Contain($"Database connection acquisition timeouts: {expectedTimeouts}");
        }
    }
}
