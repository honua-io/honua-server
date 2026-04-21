// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Specialized interface for query result caching.
/// This interface provides a simplified view of the IQueryResultCacheManager
/// for scenarios that only need basic get/set operations.
/// </summary>
public interface IQueryResultCache
{
    /// <summary>
    /// Gets a cached query result.
    /// </summary>
    /// <typeparam name="T">Type of the cached result</typeparam>
    /// <param name="cacheKey">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached result or null if not found</returns>
    Task<T?> GetAsync<T>(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a query result in the cache.
    /// </summary>
    /// <typeparam name="T">Type of the result to cache</typeparam>
    /// <param name="cacheKey">Cache key</param>
    /// <param name="result">Result to cache</param>
    /// <param name="expiration">Cache expiration time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync<T>(string cacheKey, T result, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cached query result.
    /// </summary>
    /// <param name="cacheKey">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the key existed and was removed</returns>
    Task<bool> RemoveAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cache entries by pattern.
    /// </summary>
    /// <param name="pattern">Pattern to match cache keys</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of entries invalidated</returns>
    Task<int> InvalidateAsync(string pattern, CancellationToken cancellationToken = default);
}