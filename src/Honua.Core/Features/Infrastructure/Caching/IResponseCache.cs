// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Caching;

/// <summary>
/// Abstraction for response caching to improve performance of frequently requested data
/// </summary>
public interface IResponseCache
{
    /// <summary>
    /// Binds a key to the current cache generation before reading or computing a response.
    /// Use the returned opaque key for both the lookup and its eventual fill so an
    /// intervening invalidation cannot publish old data into a new generation.
    /// </summary>
    /// <param name="key">Logical response cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The key to retain for this read and fill operation.</returns>
    Task<string> BindKeyAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(key);

    /// <summary>
    /// Gets a cached response by key
    /// </summary>
    /// <typeparam name="T">Type of cached object</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached object if found, null otherwise</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sets a cached response with the specified key and expiration
    /// </summary>
    /// <typeparam name="T">Type of object to cache</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="value">Object to cache</param>
    /// <param name="expiration">Cache expiration duration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Gets or creates a cached response using the provided factory function
    /// </summary>
    /// <typeparam name="T">Type of cached object</typeparam>
    /// <param name="key">Cache key</param>
    /// <param name="factory">Function to create the object if not cached</param>
    /// <param name="expiration">Cache expiration duration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached or newly created object</returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Removes a cached response by key
    /// </summary>
    /// <param name="key">Cache key to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cached responses matching the specified pattern
    /// </summary>
    /// <param name="pattern">Key pattern to match (supports wildcards)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);
}
