// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Infrastructure.Monitoring;

public static class MonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddPerformanceMonitoring(this IServiceCollection services)
    {
        // Register the enhanced OpenTelemetry-based performance monitoring from Core
        // This includes memory pressure monitoring, P50/P95/P99 latency tracking,
        // enhanced cache monitoring, and geospatial performance counters
        services.AddEnhancedPerformanceMonitoring();

        return services;
    }
}
