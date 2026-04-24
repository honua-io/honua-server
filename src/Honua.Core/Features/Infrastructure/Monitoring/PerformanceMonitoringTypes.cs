// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Context for query execution with performance tracking.
/// </summary>
public sealed class QueryExecutionContext
{
    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Type of query being executed.
    /// </summary>
    public string QueryType { get; set; } = string.Empty;

    /// <summary>
    /// Layer ID if applicable.
    /// </summary>
    public int? LayerId { get; set; }

    /// <summary>
    /// User ID if applicable.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Additional metadata for the query.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// When the query execution started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Statistics about query performance across all operations.
/// </summary>
public sealed record QueryCacheStatistics
{
    /// <summary>
    /// Total number of cache requests.
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// Number of cache hits.
    /// </summary>
    public long CacheHits { get; init; }

    /// <summary>
    /// Number of cache misses.
    /// </summary>
    public long CacheMisses { get; init; }

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0).
    /// </summary>
    public double HitRatio { get; init; }

    /// <summary>
    /// Total time saved by cache hits in milliseconds.
    /// </summary>
    public long TotalTimeSavedMs { get; init; }

    /// <summary>
    /// Average query execution time for cache misses in milliseconds.
    /// </summary>
    public double AvgMissExecutionTimeMs { get; init; }

    /// <summary>
    /// Current number of cached items.
    /// </summary>
    public int CurrentCacheSize { get; init; }

    /// <summary>
    /// Total memory used by cache in bytes.
    /// </summary>
    public long TotalCacheMemoryBytes { get; init; }

    /// <summary>
    /// Statistics by query type.
    /// </summary>
    public IReadOnlyDictionary<string, QueryTypeCacheStatistics> ByQueryType { get; init; } =
        new Dictionary<string, QueryTypeCacheStatistics>();

    /// <summary>
    /// When statistics were collected.
    /// </summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Cache statistics for a specific query type.
/// </summary>
public sealed record QueryTypeCacheStatistics
{
    /// <summary>
    /// Number of requests for this query type.
    /// </summary>
    public long Requests { get; init; }

    /// <summary>
    /// Number of cache hits for this query type.
    /// </summary>
    public long Hits { get; init; }

    /// <summary>
    /// Average execution time for this query type in milliseconds.
    /// </summary>
    public double AvgExecutionTimeMs { get; init; }
}

/// <summary>
/// Metrics about cache effectiveness for optimization.
/// </summary>
public sealed record CacheEffectivenessMetrics
{
    /// <summary>
    /// Top frequently accessed cache keys.
    /// </summary>
    public IReadOnlyList<CacheKeyMetrics> TopAccessedKeys { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    /// <summary>
    /// Cache keys with low hit ratios (candidates for removal).
    /// </summary>
    public IReadOnlyList<CacheKeyMetrics> LowEfficiencyKeys { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    /// <summary>
    /// Largest cached items by memory usage.
    /// </summary>
    public IReadOnlyList<CacheKeyMetrics> LargestItems { get; init; } =
        Array.Empty<CacheKeyMetrics>();

    /// <summary>
    /// Cache eviction statistics.
    /// </summary>
    public CacheEvictionMetrics EvictionMetrics { get; init; } = new();
}

/// <summary>
/// Metrics for a specific cache key.
/// </summary>
public sealed record CacheKeyMetrics
{
    /// <summary>
    /// Cache key.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Number of times accessed.
    /// </summary>
    public long AccessCount { get; init; }

    /// <summary>
    /// Cache hit ratio for this key.
    /// </summary>
    public double HitRatio { get; init; }

    /// <summary>
    /// Memory usage in bytes.
    /// </summary>
    public long MemoryBytes { get; init; }

    /// <summary>
    /// When last accessed.
    /// </summary>
    public DateTime LastAccessed { get; init; }

    /// <summary>
    /// Average execution time when not cached.
    /// </summary>
    public double AvgExecutionTimeMs { get; init; }
}

/// <summary>
/// Cache eviction statistics.
/// </summary>
public sealed record CacheEvictionMetrics
{
    /// <summary>
    /// Total number of evictions.
    /// </summary>
    public long TotalEvictions { get; init; }

    /// <summary>
    /// Evictions due to memory pressure.
    /// </summary>
    public long MemoryPressureEvictions { get; init; }

    /// <summary>
    /// Evictions due to expiration.
    /// </summary>
    public long ExpirationEvictions { get; init; }

    /// <summary>
    /// Evictions due to manual invalidation.
    /// </summary>
    public long ManualEvictions { get; init; }
}
