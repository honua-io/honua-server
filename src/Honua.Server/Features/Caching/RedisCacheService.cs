// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Caching;

/// <summary>
/// Redis-based cache service with in-memory fallback for layer metadata.
/// </summary>
/// <remarks>
/// Uses IDistributedCache (provided by Aspire's Redis integration) for primary caching.
/// Falls back to in-memory caching when Redis is unavailable to maintain availability.
/// Tracks cache metrics (hits/misses) using <see cref="IPerformanceMonitor"/>.
/// </remarks>
internal sealed partial class RedisCacheService : ICacheService, ICacheHealthChecker, IDisposable
{
    private const string CacheType = "layer-catalog";
    private readonly IDistributedCache? _distributedCache;
    private readonly CacheOptions _options;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ConcurrentDictionary<string, CacheEntry> _fallbackCache = new();
    private readonly Timer _cleanupTimer;
    private volatile bool _isUsingFallback;
    private volatile bool _disposed;
    private DateTime _lastRedisFailure = DateTime.MinValue;

    public RedisCacheService(
        IDistributedCache? distributedCache,
        IOptions<CacheOptions> options,
        ILogger<RedisCacheService> logger,
        IPerformanceMonitor performanceMonitor)
    {
        _distributedCache = distributedCache;
        _options = options.Value;
        _logger = logger;
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

        // If no distributed cache is provided, start in fallback mode
        if (_distributedCache == null)
        {
            _isUsingFallback = true;
            RedisCacheServiceLog.RedisNotConfigured(_logger);
        }

        // Start cleanup timer for fallback cache (every minute)
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public bool IsUsingFallback => _isUsingFallback;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
            return null;

        string prefixedKey = GetPrefixedKey(key);

        // Try Redis first if available
        if (!_isUsingFallback && _distributedCache != null)
        {
            try
            {
                byte[]? data = await _distributedCache.GetAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                if (data != null)
                {
                    RecordCacheHit();
                    return JsonSerializer.Deserialize<T>(data, CacheJsonContext.Default.Options);
                }

                RecordCacheMiss();
                return null;
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex);
            }
        }

        // Fallback to in-memory
        if (_options.EnableFallback && _fallbackCache.TryGetValue(prefixedKey, out CacheEntry? entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
            {
                RecordCacheHit();
                return JsonSerializer.Deserialize<T>(entry.Data, CacheJsonContext.Default.Options);
            }

            // Remove expired entry
            _fallbackCache.TryRemove(prefixedKey, out _);
        }

        RecordCacheMiss();
        return null;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        return SetAsync(key, value, _options.DefaultTtl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
            return;

        string prefixedKey = GetPrefixedKey(key);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, CacheJsonContext.Default.Options);

        // Try Redis first if available
        if (!_isUsingFallback && _distributedCache != null)
        {
            try
            {
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl
                };

                await _distributedCache.SetAsync(prefixedKey, data, options, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex);
            }
        }

        // Fallback to in-memory
        if (_options.EnableFallback)
        {
            // Enforce max entries limit
            while (_fallbackCache.Count >= _options.FallbackMaxEntries)
            {
                // Remove oldest entry
                var oldestKey = _fallbackCache
                    .OrderBy(x => x.Value.ExpiresAt)
                    .Select(x => x.Key)
                    .FirstOrDefault();

                if (oldestKey != null)
                    _fallbackCache.TryRemove(oldestKey, out _);
                else
                    break;
            }

            _fallbackCache[prefixedKey] = new CacheEntry(data, DateTime.UtcNow.Add(ttl));
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        string prefixedKey = GetPrefixedKey(key);

        // Remove from Redis if available
        if (!_isUsingFallback && _distributedCache != null)
        {
            try
            {
                await _distributedCache.RemoveAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex);
            }
        }

        // Always remove from fallback cache
        _fallbackCache.TryRemove(prefixedKey, out _);

        RecordCacheEviction();
    }

    /// <inheritdoc />
    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        string prefixedPattern = GetPrefixedKey(pattern);

        // For fallback cache, remove matching keys
        var keysToRemove = _fallbackCache.Keys
            .Where(k => MatchesPattern(k, prefixedPattern))
            .ToList();

        foreach (string key in keysToRemove)
        {
            _fallbackCache.TryRemove(key, out _);
            RecordCacheEviction();
        }

        // Note: For Redis, pattern-based deletion requires SCAN+DEL which is complex.
        // In a production system, you'd use Redis KEYS or Lua scripting.
        // For metadata caching with known key patterns, explicit removal is preferred.

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken = default) where T : class
    {
        return await GetOrSetAsync(key, factory, _options.DefaultTtl, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) where T : class
    {
        // Try to get from cache first
        T? cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached;

        // Not in cache, call factory
        T? value = await factory(cancellationToken).ConfigureAwait(false);

        // Cache the result if not null
        if (value != null)
            await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);

        return value;
    }

    /// <inheritdoc />
    public async Task<bool> IsCacheHealthyAsync(CancellationToken cancellationToken = default)
    {
        if (_distributedCache == null)
        {
            // No Redis configured - report healthy if fallback is enabled
            return _options.EnableFallback;
        }

        if (_isUsingFallback)
        {
            // Check if we should retry Redis
            if (DateTime.UtcNow - _lastRedisFailure > _options.RetryInterval)
            {
                try
                {
                    // Try a simple operation to test connectivity
                    await _distributedCache.GetAsync("__health_check__", cancellationToken).ConfigureAwait(false);
                    _isUsingFallback = false;
                    RedisCacheServiceLog.RedisConnectionRestored(_logger);
                    return true;
                }
                catch
                {
                    _lastRedisFailure = DateTime.UtcNow;
                    return _options.EnableFallback;
                }
            }

            return _options.EnableFallback;
        }

        try
        {
            await _distributedCache.GetAsync("__health_check__", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return _options.EnableFallback;
        }
    }

    private string GetPrefixedKey(string key)
    {
        return $"{_options.KeyPrefix}{key}";
    }

    private void HandleRedisFailure(Exception ex)
    {
        if (!_isUsingFallback)
        {
            _isUsingFallback = true;
            _lastRedisFailure = DateTime.UtcNow;
            RedisCacheServiceLog.RedisConnectionFailed(_logger, ex);
        }
    }

    private static bool MatchesPattern(string key, string pattern)
    {
        // Simple wildcard matching for patterns like "prefix*"
        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];
            return key.StartsWith(prefix, StringComparison.Ordinal);
        }

        return key.Equals(pattern, StringComparison.Ordinal);
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        var expiredKeys = _fallbackCache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string key in expiredKeys)
        {
            _fallbackCache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            RedisCacheServiceLog.CleanupExpiredCacheEntries(_logger, expiredKeys.Count);
        }
    }

    private void RecordCacheHit()
    {
        _performanceMonitor.RecordCacheMetrics(CacheType, "hit");
    }

    private void RecordCacheMiss()
    {
        _performanceMonitor.RecordCacheMetrics(CacheType, "miss");
    }

    private void RecordCacheEviction()
    {
        _performanceMonitor.RecordCacheMetrics(CacheType, "eviction");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer.Dispose();
        _fallbackCache.Clear();
    }

    private sealed record CacheEntry(byte[] Data, DateTime ExpiresAt);

    private static partial class RedisCacheServiceLog
    {
        [LoggerMessage(1001, LogLevel.Information, "Redis not configured, using in-memory fallback cache")]
        public static partial void RedisNotConfigured(ILogger logger);

        [LoggerMessage(1002, LogLevel.Information, "Redis connection restored, switching from fallback mode")]
        public static partial void RedisConnectionRestored(ILogger logger);

        [LoggerMessage(1003, LogLevel.Warning, "Redis connection failed, switching to in-memory fallback cache")]
        public static partial void RedisConnectionFailed(ILogger logger, Exception exception);

        [LoggerMessage(1004, LogLevel.Debug, "Cleaned up {Count} expired cache entries from fallback cache")]
        public static partial void CleanupExpiredCacheEntries(ILogger logger, int count);
    }
}
