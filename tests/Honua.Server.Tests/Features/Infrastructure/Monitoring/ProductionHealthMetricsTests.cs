// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Monitoring;

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

    private static ProductionHealthMetrics CreateHealthyMetrics()
    {
        return new ProductionHealthMetrics
        {
            MemoryUsageBytes = 512 * 1024 * 1024,
            MemoryPressureLevel = "low",
            DatabaseConnectionPoolUtilization = 0.5,
            HasDatabaseConnectionPoolUtilization = true,
            CacheHitRatio = 0.95,
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
