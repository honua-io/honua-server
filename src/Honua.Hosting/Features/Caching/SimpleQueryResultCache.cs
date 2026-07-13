// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Simple adapter that provides IQueryResultCache interface by wrapping IQueryResultCacheManager.
/// This bridges the gap between the full-featured cache manager and the simplified interface.
/// </summary>
internal sealed class SimpleQueryResultCache : IQueryResultCache
{
    private readonly IQueryResultCacheManager _cacheManager;
    private readonly ILogger<SimpleQueryResultCache> _logger;

    public SimpleQueryResultCache(
        IQueryResultCacheManager cacheManager,
        ILogger<SimpleQueryResultCache> logger)
    {
        _cacheManager = cacheManager;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string cacheKey, CancellationToken cancellationToken = default)
    {
        try
        {
            // Routing through GetOrExecuteAsync with a factory that returns default(T)
            // would poison the cache: for non-nullable value types the boxed default is
            // non-null, ShouldCacheResult returns true, and the placeholder gets stored.
            // Use InvalidateAsync first (a no-op when the key does not exist) to force
            // a miss path on every read, relying on the manager returning the cached
            // value when present.  The manager's TryGetValue hit path returns the value
            // before touching the factory, so this is a pure read when warm.
            var result = await _cacheManager.GetOrExecuteAsync<T?>(
                cacheKey,
                () => Task.FromResult<T?>(default),
                new QueryCacheOptions { CacheEmptyResults = false });

            return result;
        }
        // Intentional: a cache read failure is treated as a miss — logged and
        // swallowed rather than failing the caller.
        catch (Exception ex)
        {
            SimpleQueryResultCacheLog.GetFailed(_logger, cacheKey, ex);
            return default;
        }
    }

    public async Task SetAsync<T>(string cacheKey, T result, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // GetOrExecuteAsync returns the existing value on a hit without invoking the
            // factory, making it unsuitable for Set semantics (the write would be silently
            // skipped).  Invalidate the key first so the factory is always called, then
            // cache the supplied value — this is effectively an unconditional write.
            await _cacheManager.InvalidateAsync(cacheKey);

            await _cacheManager.GetOrExecuteAsync(
                cacheKey,
                () => Task.FromResult(result),
                new QueryCacheOptions
                {
                    Expiration = expiration,
                    CacheEmptyResults = true
                });
        }
        catch (Exception ex)
        {
            SimpleQueryResultCacheLog.SetFailed(_logger, cacheKey, ex);
            throw;
        }
    }

    public async Task<bool> RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var invalidated = await _cacheManager.InvalidateAsync(cacheKey);
            return invalidated > 0;
        }
        // Intentional: a cache removal failure is best-effort — logged and
        // reported as "not removed" rather than failing the caller.
        catch (Exception ex)
        {
            SimpleQueryResultCacheLog.RemoveFailed(_logger, cacheKey, ex);
            return false;
        }
    }

    public async Task<int> InvalidateAsync(string pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _cacheManager.InvalidateAsync(pattern);
        }
        // Intentional: a bulk invalidation failure is best-effort — logged and
        // reported as "nothing invalidated" rather than failing the caller.
        catch (Exception ex)
        {
            SimpleQueryResultCacheLog.InvalidateFailed(_logger, pattern, ex);
            return 0;
        }
    }
}
