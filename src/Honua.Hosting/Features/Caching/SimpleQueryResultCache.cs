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
            // Use the cache manager's GetOrExecuteAsync with a task that returns default
            var result = await _cacheManager.GetOrExecuteAsync<T?>(
                cacheKey,
                () => Task.FromResult<T?>(default),
                new QueryCacheOptions { CacheEmptyResults = false });

            return result;
        }
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
        catch (Exception ex)
        {
            SimpleQueryResultCacheLog.InvalidateFailed(_logger, pattern, ex);
            return 0;
        }
    }
}
