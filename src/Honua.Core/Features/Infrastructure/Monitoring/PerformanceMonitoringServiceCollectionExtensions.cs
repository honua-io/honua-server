// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Service registration helpers for performance monitoring.
/// </summary>
public static class PerformanceMonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default performance monitor implementation.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddDefaultPerformanceMonitor(this IServiceCollection services)
    {
        services.AddSingleton<DefaultPerformanceMonitor>();
        services.AddSingleton<IPerformanceMonitor>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
        services.AddSingleton<ICacheMetricsSnapshotProvider>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
        services.TryAddSingleton<IDatabasePerformanceMetricsProvider, NullDatabasePerformanceMetricsProvider>();
        return services;
    }
}
