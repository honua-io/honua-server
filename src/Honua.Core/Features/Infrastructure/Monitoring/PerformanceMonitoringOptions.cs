// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for performance monitoring middleware and services.
/// </summary>
public sealed class PerformanceMonitoringOptions
{
    /// <summary>
    /// Gets or sets whether memory tracking is enabled.
    /// Default is true.
    /// </summary>
    public bool EnableMemoryTracking { get; set; } = true;

    /// <summary>
    /// Gets or sets the threshold for considering a request slow.
    /// Default is 1 second.
    /// </summary>
    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the interval for memory sampling (every N requests).
    /// Default is 100 (sample every 100th request).
    /// </summary>
    public int MemorySamplingInterval { get; set; } = 100;

    /// <summary>
    /// Gets or sets whether to track detailed request metrics.
    /// Default is true.
    /// </summary>
    public bool EnableDetailedRequestTracking { get; set; } = true;
}
