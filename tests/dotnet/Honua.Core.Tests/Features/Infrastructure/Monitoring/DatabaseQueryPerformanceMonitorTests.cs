// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

// The query performance endpoints read DatabaseQueryPerformanceMonitor. Every feature data access
// path reports through IPerformanceMonitor.RecordDatabaseQuery, so that call must reach it.
[Collection("Metrics")]
public class DatabaseQueryPerformanceMonitorTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void RecordDatabaseQuery_ThroughRegisteredMonitor_FeedsQueryStatisticsAndSlowQueries()
    {
        using var services = BuildServices(slowQueryThresholdMs: 500);
        var performanceMonitor = services.GetRequiredService<IPerformanceMonitor>();

        performanceMonitor.RecordDatabaseQuery("select", "7", TimeSpan.FromMilliseconds(20), 3);
        performanceMonitor.RecordDatabaseQuery("select", "7", TimeSpan.FromMilliseconds(800), 40);
        performanceMonitor.RecordDatabaseQuery("count", "7", TimeSpan.FromMilliseconds(10), 1);

        var queryMonitor = services.GetRequiredService<DatabaseQueryPerformanceMonitor>();
        var statistics = queryMonitor.GetStatistics();
        Assert.Equal(3, statistics.TotalQueries);
        Assert.Equal(1, statistics.SlowQueries);
        Assert.Equal(0, statistics.FailedQueries);
        Assert.Equal(800, statistics.MaxExecutionTimeMs, precision: 3);
        Assert.Equal(2, statistics.ByQueryType["select"].Count);
        Assert.Equal(1, statistics.ByQueryType["count"].Count);

        var slow = Assert.Single(queryMonitor.GetRecentSlowQueries());
        Assert.Equal("select", slow.QueryType);
        Assert.Equal(40, slow.RowCount);
        Assert.Null(slow.QueryText);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void RecordDatabaseQuery_WithoutQueryMonitor_StillRecordsMetrics()
    {
        using var services = new ServiceCollection().AddLogging().AddDefaultPerformanceMonitor().BuildServiceProvider();

        var exception = Record.Exception(() => services.GetRequiredService<IPerformanceMonitor>()
            .RecordDatabaseQuery("select", "7", TimeSpan.FromMilliseconds(5), 1));

        Assert.Null(exception);
    }

    private static ServiceProvider BuildServices(double slowQueryThresholdMs)
    {
        var services = new ServiceCollection().AddLogging();
        services.Configure<QueryPerformanceMonitoringOptions>(options => options.SlowQueryThresholdMs = slowQueryThresholdMs);
        services.AddSingleton<DatabaseQueryPerformanceMonitor>();
        services.AddDefaultPerformanceMonitor();
        return services.BuildServiceProvider();
    }
}
