// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Infrastructure.Monitoring;

public static class PerformanceMonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddDefaultPerformanceMonitor(this IServiceCollection services)
    {
        services.AddSingleton<DefaultPerformanceMonitor>();
        services.AddSingleton<IPerformanceMonitor>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
        services.AddSingleton<ICacheMetricsSnapshotProvider>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
        services.AddSingleton<IHttpRequestMetricsSnapshotProvider>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
        services.TryAddSingleton<IDatabasePerformanceMetricsProvider, NullDatabasePerformanceMetricsProvider>();
        services.TryAddSingleton<IActiveDbConnectionTracker, ActiveDbConnectionTracker>();
        return services;
    }

    public static IServiceCollection AddEnhancedPerformanceMonitoring(this IServiceCollection services)
    {
        // Add the default performance monitor
        services.AddDefaultPerformanceMonitor();

        return services;
    }
}
