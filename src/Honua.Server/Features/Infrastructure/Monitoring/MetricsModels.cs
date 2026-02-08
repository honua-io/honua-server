// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Response model for basic health metrics.
/// </summary>
public sealed class HealthMetrics
{
    /// <summary>
    /// Overall health status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Current memory usage in megabytes.
    /// </summary>
    public required double MemoryUsageMB { get; init; }

    /// <summary>
    /// Memory pressure as a percentage.
    /// </summary>
    public required double MemoryPressurePercent { get; init; }

    /// <summary>
    /// Total garbage collection count across all generations.
    /// </summary>
    public required int GCCollections { get; init; }
}

/// <summary>
/// Response model for comprehensive performance metrics.
/// </summary>
public sealed class PerformanceMetricsResponse
{
    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Memory usage statistics.
    /// </summary>
    public required MemoryUsage Memory { get; init; }

    /// <summary>
    /// System information.
    /// </summary>
    public required SystemInfo SystemInfo { get; init; }

    /// <summary>
    /// HTTP request performance metrics.
    /// </summary>
    public required HttpRequestMetrics Http { get; init; }
}

/// <summary>
/// System information for performance context.
/// </summary>
public sealed class SystemInfo
{
    /// <summary>
    /// Number of logical processors.
    /// </summary>
    public required int ProcessorCount { get; init; }

    /// <summary>
    /// Machine name where the application is running.
    /// </summary>
    public required string MachineName { get; init; }

    /// <summary>
    /// Working set size in bytes.
    /// </summary>
    public required long WorkingSet { get; init; }

    /// <summary>
    /// .NET framework version.
    /// </summary>
    public required string FrameworkVersion { get; init; }
}

/// <summary>
/// Database performance metrics.
/// </summary>
public sealed class DatabaseMetrics
{
    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Cache hit rate as a decimal (0.0 to 1.0).
    /// </summary>
    public required double CacheHitRate { get; init; }

    /// <summary>
    /// Total number of cache hits.
    /// </summary>
    public required long CacheHits { get; init; }

    /// <summary>
    /// Total number of cache misses.
    /// </summary>
    public required long CacheMisses { get; init; }

    /// <summary>
    /// Performance metrics for different database operations.
    /// </summary>
    public required Dictionary<string, DatabaseOperationMetrics> Operations { get; init; }
}

/// <summary>
/// HTTP request performance metrics.
/// </summary>
public sealed class HttpRequestMetrics
{
    /// <summary>
    /// Total number of HTTP requests processed.
    /// </summary>
    public required long TotalRequests { get; init; }

    /// <summary>
    /// Total number of server error responses (5xx).
    /// </summary>
    public required long TotalServerErrors { get; init; }

    /// <summary>
    /// Total number of client error responses (4xx).
    /// </summary>
    public required long TotalClientErrors { get; init; }

    /// <summary>
    /// Current number of active HTTP requests.
    /// </summary>
    public required int ActiveRequests { get; init; }

    /// <summary>
    /// Average request duration in milliseconds.
    /// </summary>
    public required double AvgDurationMs { get; init; }

    /// <summary>
    /// Maximum request duration in milliseconds.
    /// </summary>
    public required double MaxDurationMs { get; init; }

    /// <summary>
    /// Estimated 95th percentile request duration in milliseconds.
    /// </summary>
    public required double P95DurationMs { get; init; }

    /// <summary>
    /// Total number of slow requests.
    /// </summary>
    public required long SlowRequests { get; init; }

    /// <summary>
    /// Threshold used for slow request classification in milliseconds.
    /// </summary>
    public required double SlowRequestThresholdMs { get; init; }

    /// <summary>
    /// Server error rate as a decimal (0.0 to 1.0).
    /// </summary>
    public required double ServerErrorRate { get; init; }
}

/// <summary>
/// Performance metrics for a specific database operation.
/// </summary>
public sealed class DatabaseOperationMetrics
{
    /// <summary>
    /// Total number of operations executed.
    /// </summary>
    public required long Count { get; init; }

    /// <summary>
    /// Total execution time in milliseconds.
    /// </summary>
    public required long TotalTimeMs { get; init; }

    /// <summary>
    /// Maximum execution time in milliseconds.
    /// </summary>
    public required long MaxTimeMs { get; init; }

    /// <summary>
    /// Average execution time in milliseconds.
    /// </summary>
    public required double AvgTimeMs { get; init; }
}

/// <summary>
/// Cache performance metrics.
/// </summary>
public sealed class CacheMetrics
{
    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Total number of cache requests.
    /// </summary>
    public required long TotalRequests { get; init; }

    /// <summary>
    /// Overall cache hit ratio as a decimal (0.0 to 1.0).
    /// </summary>
    public required double HitRatio { get; init; }

    /// <summary>
    /// Performance metrics by cache type.
    /// </summary>
    public required Dictionary<string, CacheTypeMetrics> Types { get; init; }
}

/// <summary>
/// Performance metrics for a specific cache type.
/// </summary>
public sealed class CacheTypeMetrics
{
    /// <summary>
    /// Number of cache hits for this type.
    /// </summary>
    public required long Hits { get; init; }

    /// <summary>
    /// Number of cache misses for this type.
    /// </summary>
    public required long Misses { get; init; }

    /// <summary>
    /// Hit ratio for this cache type.
    /// </summary>
    public double HitRatio => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0.0;

    /// <summary>
    /// Number of evictions for this cache type.
    /// </summary>
    public required long Evictions { get; init; }

}
