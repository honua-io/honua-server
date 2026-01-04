// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Service collection extensions for registering OpenTelemetry-based monitoring services.
/// Uses standard OpenTelemetry APIs for pluggable observability.
/// </summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>
    /// Adds OpenTelemetry-based performance monitoring services.
    /// Uses standard OpenTelemetry Meter and ActivitySource for pluggable observability.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPerformanceMonitoring(this IServiceCollection services)
    {
        // Register the standard OpenTelemetry-based performance monitor from Core
        services.AddDefaultPerformanceMonitor();

        return services;
    }
}
