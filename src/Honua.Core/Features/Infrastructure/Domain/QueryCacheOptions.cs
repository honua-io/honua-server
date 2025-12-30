// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Configuration options for database query plan caching and prepared statements
/// </summary>
/// <remarks>
/// Controls behavior of prepared statement caching to optimize database performance
/// for high-frequency operations while managing memory usage and resource pooling.
/// </remarks>
public sealed class QueryCacheOptions
{
    /// <summary>
    /// Maximum number of prepared statements to cache per connection
    /// </summary>
    /// <remarks>
    /// PostgreSQL has a default limit of 1000 prepared statements per session.
    /// This value should be set well below that limit to allow for other operations.
    /// </remarks>
    public int MaxCachedStatements { get; init; } = 100;

    /// <summary>
    /// Maximum lifetime of a cached prepared statement in minutes
    /// </summary>
    /// <remarks>
    /// Statements older than this will be evicted from the cache to prevent
    /// memory leaks and ensure query plans remain optimal.
    /// </remarks>
    public int StatementLifetimeMinutes { get; init; } = 30;

    /// <summary>
    /// Minimum number of executions before a statement is considered for caching
    /// </summary>
    /// <remarks>
    /// Only statements executed this many times will be prepared and cached
    /// to avoid preparing statements that are rarely used.
    /// </remarks>
    public int MinExecutionsForCaching { get; init; } = 3;

    /// <summary>
    /// Whether to enable automatic prepared statement caching
    /// </summary>
    /// <remarks>
    /// When disabled, only manually specified statements will be prepared.
    /// Useful for debugging or environments where prepared statements cause issues.
    /// </remarks>
    public bool EnableAutomaticCaching { get; set; } = true;

    /// <summary>
    /// Whether to log cache hit/miss statistics
    /// </summary>
    /// <remarks>
    /// Enables detailed logging of cache performance for monitoring and optimization.
    /// Should be disabled in production for performance unless debugging issues.
    /// </remarks>
    public bool EnablePerformanceLogging { get; init; }

    /// <summary>
    /// Interval in minutes for cleaning up expired statements
    /// </summary>
    /// <remarks>
    /// Background cleanup runs at this interval to remove expired statements
    /// and free up memory. Should balance cleanup frequency with overhead.
    /// </remarks>
    public int CleanupIntervalMinutes { get; init; } = 10;
}
