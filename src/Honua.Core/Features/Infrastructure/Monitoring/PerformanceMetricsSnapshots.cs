// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides access to database performance metrics snapshots.
/// </summary>
public interface IDatabasePerformanceMetricsProvider
{
    /// <summary>
    /// Gets a snapshot of database performance metrics.
    /// </summary>
    DatabasePerformanceMetricsSnapshot GetMetrics();
}

/// <summary>
/// Provides access to cache performance metrics snapshots.
/// </summary>
public interface ICacheMetricsSnapshotProvider
{
    /// <summary>
    /// Gets a snapshot of cache performance metrics.
    /// </summary>
    CacheMetricsSnapshot GetCacheMetricsSnapshot();
}

/// <summary>
/// Provides access to HTTP request performance metrics snapshots.
/// </summary>
public interface IHttpRequestMetricsSnapshotProvider
{
    /// <summary>
    /// Gets a snapshot of HTTP request performance metrics.
    /// </summary>
    HttpRequestMetricsSnapshot GetHttpRequestMetricsSnapshot();
}

/// <summary>
/// Snapshot of database performance metrics.
/// </summary>
public sealed record DatabasePerformanceMetricsSnapshot
{
    /// <summary>
    /// Cache hit rate as a decimal (0.0 to 1.0).
    /// </summary>
    public double CacheHitRate { get; init; }

    /// <summary>
    /// Total number of cache hits.
    /// </summary>
    public long CacheHits { get; init; }

    /// <summary>
    /// Total number of cache misses.
    /// </summary>
    public long CacheMisses { get; init; }

    /// <summary>
    /// Performance metrics by operation type.
    /// </summary>
    public Dictionary<string, DatabaseOperationMetricsSnapshot> Operations { get; init; } = new();
}

/// <summary>
/// Snapshot of performance metrics for a specific database operation.
/// </summary>
public sealed record DatabaseOperationMetricsSnapshot
{
    /// <summary>
    /// Total number of operations executed.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Total execution time in milliseconds.
    /// </summary>
    public long TotalTimeMs { get; init; }

    /// <summary>
    /// Maximum execution time in milliseconds.
    /// </summary>
    public long MaxTimeMs { get; init; }

    /// <summary>
    /// Average execution time in milliseconds.
    /// </summary>
    public double AvgTimeMs { get; init; }
}

/// <summary>
/// Snapshot of cache performance metrics.
/// </summary>
public sealed record CacheMetricsSnapshot
{
    /// <summary>
    /// Total number of cache hits.
    /// </summary>
    public long TotalHits { get; init; }

    /// <summary>
    /// Total number of cache misses.
    /// </summary>
    public long TotalMisses { get; init; }

    /// <summary>
    /// Total number of cache evictions.
    /// </summary>
    public long TotalEvictions { get; init; }

    /// <summary>
    /// Metrics by cache type.
    /// </summary>
    public Dictionary<string, CacheTypeMetricsSnapshot> Types { get; init; } = new();
}

/// <summary>
/// Snapshot of cache metrics for a specific cache type.
/// </summary>
public sealed record CacheTypeMetricsSnapshot
{
    /// <summary>
    /// Number of cache hits for this type.
    /// </summary>
    public long Hits { get; init; }

    /// <summary>
    /// Number of cache misses for this type.
    /// </summary>
    public long Misses { get; init; }

    /// <summary>
    /// Number of evictions for this type.
    /// </summary>
    public long Evictions { get; init; }
}

/// <summary>
/// Snapshot of HTTP request performance metrics.
/// </summary>
public sealed record HttpRequestMetricsSnapshot
{
    /// <summary>
    /// Total number of HTTP requests processed.
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// Total number of server error responses (5xx).
    /// </summary>
    public long TotalServerErrors { get; init; }

    /// <summary>
    /// Total number of client error responses (4xx).
    /// </summary>
    public long TotalClientErrors { get; init; }

    /// <summary>
    /// Current number of active HTTP requests.
    /// </summary>
    public int ActiveRequests { get; init; }

    /// <summary>
    /// Average request duration in milliseconds.
    /// </summary>
    public double AvgDurationMs { get; init; }

    /// <summary>
    /// Maximum request duration in milliseconds.
    /// </summary>
    public double MaxDurationMs { get; init; }

    /// <summary>
    /// Estimated 95th percentile request duration in milliseconds.
    /// </summary>
    public double P95DurationMs { get; init; }

    /// <summary>
    /// Total number of slow requests.
    /// </summary>
    public long SlowRequestCount { get; init; }

    /// <summary>
    /// Threshold used for slow request classification in milliseconds.
    /// </summary>
    public double SlowRequestThresholdMs { get; init; }
}

/// <summary>
/// Default database metrics provider when no implementation is registered.
/// </summary>
internal sealed class NullDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    public DatabasePerformanceMetricsSnapshot GetMetrics() => new();
}
