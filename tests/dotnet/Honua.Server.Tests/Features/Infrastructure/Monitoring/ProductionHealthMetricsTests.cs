// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Unit tests for production health metric calculations.
/// </summary>
public sealed class ProductionHealthMetricsTests
{
    [Fact]
    public void IsHealthy_IgnoresHistoricalDatabaseTimeoutsAndFailures()
    {
        var metrics = CreateHealthyMetrics();
        metrics.ConnectionAcquisitionTimeouts = 4;
        metrics.ConnectionAcquisitionFailures = 2;

        metrics.IsHealthy().Should().BeTrue();
    }

    [Fact]
    public void GetAlertConditions_IgnoresHistoricalDatabaseTimeoutsAndFailures()
    {
        var metrics = CreateHealthyMetrics();
        metrics.ConnectionAcquisitionTimeouts = 4;
        metrics.ConnectionAcquisitionFailures = 2;

        metrics.GetAlertConditions().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(7, 3, false)]
    [InlineData(8, 2, true)]
    public async Task CheckHealthAsync_CacheSamples_DistinguishesUnobservedFromLowHitRatio(
        long hits, long misses, bool expectedCacheHealthy)
    {
        var tracker = Substitute.For<IActiveDbConnectionTracker>();
        using var pool = new ConnectionPoolMetrics(tracker);
        var cache = Substitute.For<ICacheMetricsSnapshotProvider>();
        cache.GetCacheMetricsSnapshot().Returns(new CacheMetricsSnapshot { TotalHits = hits, TotalMisses = misses });
        var requests = Substitute.For<IHttpRequestMetricsSnapshotProvider>();
        requests.GetHttpRequestMetricsSnapshot().Returns(new HttpRequestMetricsSnapshot());
        using var services = new ServiceCollection().BuildServiceProvider();
        using var collector = new ProductionMetricsCollector(pool, tracker, cache, requests,
            NullLogger<ProductionMetricsCollector>.Instance, services);
        var check = new ProductionMetricsHealthCheck(collector, NullLogger<ProductionMetricsHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        var metrics = collector.GetHealthMetrics();
        metrics.GetAlertConditions().Any(alert => alert.StartsWith("Low cache hit ratio", StringComparison.Ordinal))
            .Should().Be(!expectedCacheHealthy);
        result.Data["hasCacheSamples"].Should().Be(hits + misses > 0);
        if (hits + misses == 0)
        {
            result.Data["cacheSampleStatus"].Should().Be("no samples yet");
            if (result.Status == HealthStatus.Healthy)
            {
                result.Description.Should().Contain("no samples yet");
            }
            result.Data.Should().NotContainKey("cacheHitRatio");
        }
        else
        {
            result.Data["cacheSampleStatus"].Should().Be("available");
            result.Data["cacheHitRatio"].Should().Be((double)hits / (hits + misses));
        }
    }

    [Theory]
    [InlineData(false, 0.0, true)]
    [InlineData(true, 0.0, false)]
    [InlineData(true, 0.7, false)]
    [InlineData(true, 0.8, true)]
    public void IsHealthy_CacheSamples_OnlyAppliesThresholdAfterObservations(
        bool hasSamples, double hitRatio, bool expectedHealthy)
    {
        var metrics = CreateHealthyMetrics();
        metrics.HasCacheSamples = hasSamples;
        metrics.CacheHitRatio = hitRatio;

        metrics.IsHealthy().Should().Be(expectedHealthy);
        metrics.GetAlertConditions().Should().HaveCount(expectedHealthy ? 0 : 1);
    }

    [Fact]
    public void GetAlertConditions_NoCacheSamples_PreservesOtherAlerts()
    {
        var metrics = CreateHealthyMetrics();
        metrics.HasCacheSamples = false;
        metrics.CacheHitRatio = 0;
        metrics.DatabaseConnectionPoolUtilization = 0.9;
        metrics.ErrorRate = 0.1;
        metrics.MemoryPressureLevel = "critical";

        metrics.IsHealthy().Should().BeFalse();
        metrics.GetAlertConditions().Should().HaveCount(3)
            .And.NotContain(alert => alert.StartsWith("Low cache hit ratio", StringComparison.Ordinal));
    }

    private static ProductionHealthMetrics CreateHealthyMetrics()
    {
        return new ProductionHealthMetrics
        {
            MemoryUsageBytes = 512 * 1024 * 1024,
            MemoryPressureLevel = "low",
            DatabaseConnectionPoolUtilization = 0.5,
            HasDatabaseConnectionPoolUtilization = true,
            CacheHitRatio = 0.95,
            HasCacheSamples = true,
            TotalQueries = 100,
            TotalErrors = 2,
            ErrorRate = 0.02,
            ActiveConnections = 2,
            ConnectionAcquisitionTimeouts = 0,
            ConnectionAcquisitionFailures = 0,
            RateLimitViolations = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}
