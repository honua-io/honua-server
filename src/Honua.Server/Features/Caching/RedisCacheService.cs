// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

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
    private const string HealthCheckKey = "__health_check__";
    private readonly IDistributedCache? _distributedCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly CacheOptions _options;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly IAsyncPolicy _redisPolicy;
    private readonly IAsyncPolicy<byte[]?> _redisGetPolicy;
    private readonly ConcurrentDictionary<string, CacheEntry> _fallbackCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;
    private volatile bool _isUsingFallback;
    private volatile bool _disposed;
    private DateTime _lastRedisFailure = DateTime.MinValue;

    public RedisCacheService(
        IDistributedCache? distributedCache,
        IOptions<CacheOptions> options,
        ILogger<RedisCacheService> logger,
        IPerformanceMonitor performanceMonitor,
        IConnectionMultiplexer? redis = null)
    {
        _distributedCache = distributedCache;
        _redis = redis;
        _options = options.Value;
        _logger = logger;
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        var policyOptions = ResiliencePolicyOptions.Default;
        _redisPolicy = ResiliencePolicyFactory.CreateStandardPolicy(
            Policy.Handle<RedisConnectionException>()
                .Or<RedisTimeoutException>()
                .Or<RedisServerException>(),
            policyOptions);
        _redisGetPolicy = ResiliencePolicyFactory.CreateStandardPolicy<byte[]?>(
            Policy<byte[]?>.Handle<RedisConnectionException>()
                .Or<RedisTimeoutException>()
                .Or<RedisServerException>(),
            policyOptions);

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
        if (_distributedCache != null)
        {
            if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
            {
                await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_isUsingFallback)
            {
                try
                {
                    byte[]? data = await _redisGetPolicy.ExecuteAsync(
                        ct => _distributedCache.GetAsync(prefixedKey, ct),
                        cancellationToken).ConfigureAwait(false);
                    if (data != null)
                    {
                        if (TryDeserialize(data, prefixedKey, out T? value))
                        {
                            RecordCacheHit();
                            return value;
                        }

                        await RemoveCorruptRedisEntryAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                        RecordCacheMiss();
                        return null;
                    }

                    RecordCacheMiss();
                    return null;
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                }
            }
        }

        // Fallback to in-memory
        if (_options.EnableFallback && _fallbackCache.TryGetValue(prefixedKey, out CacheEntry? entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
            {
                if (TryDeserialize(entry.Data, prefixedKey, out T? value))
                {
                    RecordCacheHit();
                    return value;
                }

                _fallbackCache.TryRemove(prefixedKey, out _);
            }
            else
            {
                _fallbackCache.TryRemove(prefixedKey, out _);
            }
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
        if (_distributedCache != null)
        {
            if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
            {
                await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_isUsingFallback)
            {
                try
                {
                    var options = new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = ttl
                    };

                    await _redisPolicy.ExecuteAsync(
                        ct => _distributedCache.SetAsync(prefixedKey, data, options, ct),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                }
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
        if (_distributedCache != null)
        {
            if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
            {
                await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_isUsingFallback)
            {
                try
                {
                    await _redisPolicy.ExecuteAsync(
                        ct => _distributedCache.RemoveAsync(prefixedKey, ct),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                }
            }
        }

        // Always remove from fallback cache
        _fallbackCache.TryRemove(prefixedKey, out _);

        RecordCacheEviction();
    }

    /// <inheritdoc />
    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        string prefixedPattern = GetPrefixedKey(pattern);

        cancellationToken.ThrowIfCancellationRequested();

        if (_distributedCache != null && _isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_isUsingFallback && _redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                var database = db.Database;
                foreach (var endpoint in _redis.GetEndPoints())
                {
                    var server = _redis.GetServer(endpoint);
                    if (!server.IsConnected)
                    {
                        continue;
                    }

                    foreach (var key in server.Keys(database, pattern: prefixedPattern))
                    {
                        await db.KeyDeleteAsync(key).ConfigureAwait(false);
                        RecordCacheEviction();
                    }
                }
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex);
            }
        }

        // For fallback cache, remove matching keys
        var keysToRemove = _fallbackCache.Keys
            .Where(k => MatchesPattern(k, prefixedPattern))
            .ToList();

        foreach (string key in keysToRemove)
        {
            _fallbackCache.TryRemove(key, out _);
            RecordCacheEviction();
        }

        // Note: Redis pattern deletion relies on server key scans when a connection is available.
        // For large keyspaces, consider narrowing patterns or using a dedicated index.

        return;
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
        if (!_options.Enabled)
        {
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        // Try to get from cache first
        T? cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached;

        await using var keyLock = await AcquireKeyLockAsync(GetPrefixedKey(key), cancellationToken).ConfigureAwait(false);

        // Re-check cache after acquiring the lock
        cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
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
                var restored = await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
                return restored || _options.EnableFallback;
            }

            return _options.EnableFallback;
        }

        try
        {
            await _redisGetPolicy.ExecuteAsync(
                ct => _distributedCache.GetAsync(HealthCheckKey, ct),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return _options.EnableFallback;
        }
    }

    private bool ShouldRetryRedis(DateTime now)
    {
        return _isUsingFallback && _distributedCache != null && now - _lastRedisFailure > _options.RetryInterval;
    }

    private async Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return false;
        }

        try
        {
            await _redisGetPolicy.ExecuteAsync(
                ct => _distributedCache.GetAsync(HealthCheckKey, ct),
                cancellationToken).ConfigureAwait(false);
            _isUsingFallback = false;
            RedisCacheServiceLog.RedisConnectionRestored(_logger);
            return true;
        }
        catch
        {
            _lastRedisFailure = DateTime.UtcNow;
            return false;
        }
    }

    private bool TryDeserialize<T>(byte[] data, string cacheKey, out T? value) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(data, CacheJsonContext.Default.Options);
            return value != null;
        }
        catch (JsonException ex)
        {
            value = null;
            RedisCacheServiceLog.CacheEntryDeserializationFailed(_logger, cacheKey, ex);
            return false;
        }
        catch (NotSupportedException ex)
        {
            value = null;
            RedisCacheServiceLog.CacheEntryDeserializationFailed(_logger, cacheKey, ex);
            return false;
        }
    }

    private async Task RemoveCorruptRedisEntryAsync(string prefixedKey, CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return;
        }

        try
        {
            await _redisPolicy.ExecuteAsync(
                ct => _distributedCache.RemoveAsync(prefixedKey, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandleRedisFailure(ex);
        }
    }

    private async ValueTask<KeyLock> AcquireKeyLockAsync(string key, CancellationToken cancellationToken)
    {
        var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new KeyLock(key, semaphore, _keyLocks);
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
        foreach (var semaphore in _keyLocks.Values)
        {
            semaphore.Dispose();
        }
        _keyLocks.Clear();
    }

    private sealed class KeyLock : IAsyncDisposable, IDisposable
    {
        private readonly string _key;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;
        private bool _disposed;

        public KeyLock(string key, SemaphoreSlim semaphore, ConcurrentDictionary<string, SemaphoreSlim> locks)
        {
            _key = key;
            _semaphore = semaphore;
            _locks = locks;
        }

        public void Dispose()
        {
            Release();
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Release();

            if (_semaphore.CurrentCount == 1)
            {
                _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(_key, _semaphore));
            }
        }
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

        [LoggerMessage(1005, LogLevel.Warning, "Failed to deserialize cache entry {CacheKey}")]
        public static partial void CacheEntryDeserializationFailed(ILogger logger, string cacheKey, Exception exception);
    }
}
