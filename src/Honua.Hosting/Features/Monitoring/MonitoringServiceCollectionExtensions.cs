// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Service collection extensions for registering OpenTelemetry-based monitoring services.
/// Uses standard OpenTelemetry APIs for pluggable observability.
/// </summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Adds enhanced OpenTelemetry-based performance monitoring services.
    /// Uses standard OpenTelemetry Meter and ActivitySource for pluggable observability.
    /// Includes memory pressure monitoring, enhanced latency tracking, and geospatial performance counters.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPerformanceMonitoring(this IServiceCollection services)
    {
        // Register the enhanced OpenTelemetry-based performance monitoring from Core
        // This includes memory pressure monitoring, P50/P95/P99 latency tracking,
        // enhanced cache monitoring, and geospatial performance counters
        services.AddEnhancedPerformanceMonitoring();

        return services;
    }
}
