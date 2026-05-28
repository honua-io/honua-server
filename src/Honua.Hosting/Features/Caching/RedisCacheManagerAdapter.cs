// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Adapter that implements ICacheManager by wrapping IDistributedCache.
/// </summary>
internal sealed class RedisCacheManagerAdapter : ICacheManager
{
    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<RedisCacheManagerAdapter> _logger;

    public RedisCacheManagerAdapter(
        IDistributedCache distributedCache,
        ILogger<RedisCacheManagerAdapter> logger)
    {
        _distributedCache = distributedCache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _distributedCache.GetAsync(key, cancellationToken);
            if (bytes == null) return default;

            var value = JsonSerializer.Deserialize(bytes, ResolveTypeInfo<T>());
            return value is T typed ? typed : default;
        }
        catch (Exception ex)
        {
            RedisCacheManagerAdapterLog.GetFailed(_logger, key, ex);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes((object?)value, ResolveTypeInfo<T>());
            var options = new DistributedCacheEntryOptions();

            if (expiration.HasValue)
                options.SetAbsoluteExpiration(expiration.Value);
            else
                options.SetAbsoluteExpiration(TimeSpan.FromHours(1));

            await _distributedCache.SetAsync(key, bytes, options, cancellationToken);
        }
        catch (Exception ex)
        {
            RedisCacheManagerAdapterLog.SetFailed(_logger, key, ex);
            throw;
        }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _distributedCache.RemoveAsync(key, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            RedisCacheManagerAdapterLog.RemoveFailed(_logger, key, ex);
            return false;
        }
    }

    public async Task<int> RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        // Pattern-based removal is not supported by IDistributedCache
        RedisCacheManagerAdapterLog.PatternRemovalUnsupported(_logger, pattern);
        return 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _distributedCache.GetAsync(key, cancellationToken);
            return bytes != null;
        }
        catch (Exception ex)
        {
            RedisCacheManagerAdapterLog.ExistsCheckFailed(_logger, key, ex);
            return false;
        }
    }

    public CacheHealthInfo GetHealthInfo()
    {
        // Basic health check - in practice this would ping the cache service
        return new CacheHealthInfo
        {
            IsHealthy = true,
            TotalKeys = -1,
            MemoryUsageBytes = -1,
            HitRatePercent = 0,
            HealthMessage = "IDistributedCache adapter - limited health info available"
        };
    }

    private static JsonTypeInfo ResolveTypeInfo<T>()
    {
        return CacheJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Type '{typeof(T).FullName}' is not registered in {nameof(CacheJsonContext)}.");
    }
}

/// <summary>
/// Null object implementation for when no cache is available.
/// </summary>
internal sealed class NullCacheManager : ICacheManager
{
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<T?>(default);

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<int> RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public CacheHealthInfo GetHealthInfo() => new()
    {
        IsHealthy = true,
        TotalKeys = 0,
        MemoryUsageBytes = 0,
        HitRatePercent = 0,
        HealthMessage = "Null cache (no caching)"
    };
}
