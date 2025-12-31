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
/// Default database metrics provider when no implementation is registered.
/// </summary>
internal sealed class NullDatabasePerformanceMetricsProvider : IDatabasePerformanceMetricsProvider
{
    public DatabasePerformanceMetricsSnapshot GetMetrics() => new();
}
