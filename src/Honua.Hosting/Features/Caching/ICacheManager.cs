// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// High-level cache manager interface providing unified caching operations
/// across different cache types and storage mechanisms.
/// </summary>
public interface ICacheManager
{
    /// <summary>
    /// Gets a value from the cache by key.
    /// </summary>
    /// <typeparam name="T">Type of the cached value</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached value or null if not found</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in the cache with optional expiration.
    /// </summary>
    /// <typeparam name="T">Type of the value to cache</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="value">Value to cache</param>
    /// <param name="expiration">Optional expiration time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the key existed and was removed</returns>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes multiple values from the cache by pattern.
    /// </summary>
    /// <param name="pattern">Pattern to match keys (supports wildcards)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of keys removed</returns>
    Task<int> RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in the cache.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the key exists</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cache statistics and health information.
    /// </summary>
    CacheHealthInfo GetHealthInfo();
}

/// <summary>
/// Cache health and statistics information.
/// </summary>
public sealed record CacheHealthInfo
{
    /// <summary>
    /// Whether the cache is healthy and operational.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Total number of keys in the cache.
    /// </summary>
    public long TotalKeys { get; init; }

    /// <summary>
    /// Memory usage in bytes.
    /// </summary>
    public long MemoryUsageBytes { get; init; }

    /// <summary>
    /// Cache hit rate as a percentage.
    /// </summary>
    public double HitRatePercent { get; init; }

    /// <summary>
    /// Any health-related messages or warnings.
    /// </summary>
    public string? HealthMessage { get; init; }
}
