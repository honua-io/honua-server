// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Caching;

/// <summary>
/// Provides access to prepared statement cache statistics without exposing implementation details.
/// </summary>
public interface IPreparedStatementCacheStatisticsProvider
{
    /// <summary>
    /// Gets current prepared statement cache statistics.
    /// </summary>
    PreparedStatementCacheStatistics GetStatistics();
}

/// <summary>
/// Snapshot of prepared statement cache performance metrics.
/// </summary>
public sealed record PreparedStatementCacheStatistics
{
    /// <summary>
    /// Total number of unique statements tracked.
    /// </summary>
    public int TotalStatements { get; init; }

    /// <summary>
    /// Total cache hit count.
    /// </summary>
    public int CacheHits { get; init; }

    /// <summary>
    /// Total cache miss count.
    /// </summary>
    public int CacheMisses { get; init; }

    /// <summary>
    /// Number of statements currently prepared and cached.
    /// </summary>
    public int PreparedStatements { get; init; }

    /// <summary>
    /// Cache hit ratio (0.0 to 1.0).
    /// </summary>
    public double HitRatio => CacheHits + CacheMisses > 0
        ? (double)CacheHits / (CacheHits + CacheMisses)
        : 0;
}
