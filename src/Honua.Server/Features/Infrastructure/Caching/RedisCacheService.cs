// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Server.Features.Infrastructure.Middleware;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Redis-based cache service with in-memory fallback for layer metadata.
/// </summary>
/// <remarks>
/// Uses IDistributedCache (provided by Aspire's Redis integration) for primary caching.
/// Falls back to in-memory caching when Redis is unavailable to maintain availability.
/// Tracks cache metrics (hits/misses) using <see cref="IPerformanceMonitor"/>.
///
/// PERFORMANCE NOTE: DistributedLock class uses IAsyncDisposable pattern to avoid
/// blocking operations in disposal. Always use 'await using' syntax to ensure
/// proper async cleanup and prevent thread pool starvation.
/// </remarks>
internal sealed partial class RedisCacheService : ICacheService, ICacheHealthChecker, IDisposable
{
    private const string CacheType = "layer-catalog";
    private const string HealthCheckKey = "__health_check__";
    private const string CacheKeyIndexKey = "__cache_key_index__";
    private const int MaxKeyLocks = 1000;
    private static readonly TimeSpan CacheKeyIndexTtl = TimeSpan.FromDays(30);
    private readonly IDistributedCache? _distributedCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly CacheOptions _options;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly IAsyncPolicy _redisPolicy;
    private readonly IAsyncPolicy<byte[]?> _redisGetPolicy;
    private readonly ConcurrentDictionary<string, CacheEntry> _fallbackCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheWriteInfo> _writeMetadata = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _distributedIndexLock = new(1, 1);
    private readonly Timer _cleanupTimer;
    private readonly string _distributedCacheKeyPrefix;
    private volatile bool _isUsingFallback;
    private volatile bool _disposed;
    private long _lastRedisFailureTicks = DateTime.MinValue.Ticks;
    private int _isPruningKeyLocks;

    public RedisCacheService(
        IDistributedCache? distributedCache,
        IOptions<CacheOptions> options,
        ILogger<RedisCacheService> logger,
        IPerformanceMonitor performanceMonitor,
        IConnectionMultiplexer? redis = null,
        string? distributedCacheKeyPrefix = null)
    {
        _distributedCache = distributedCache;
        _redis = redis;
        _options = options.Value;
        _distributedCacheKeyPrefix = distributedCacheKeyPrefix ?? string.Empty;
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
        using var operationScope = _performanceMonitor.StartOperation("cache_get")
            .WithTag("cache_type", CacheType)
            .WithTag("key_family", GetCacheKeyFamily(key));

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
                    byte[]? data;
                    if (_redis != null)
                    {
                        data = await _redisGetPolicy.ExecuteAsync(
                            _ => GetRedisEntryAsync(prefixedKey),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        data = await _redisGetPolicy.ExecuteAsync(
                            ct => _distributedCache.GetAsync(prefixedKey, ct),
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (data != null)
                    {
                        if (TryDeserialize(data, prefixedKey, out T? value))
                        {
                            RecordCacheHit();
                            operationScope.WithTag("result", "hit").WithTag("source", "redis");
                            return value;
                        }

                        await RemoveCorruptRedisEntryAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                        RecordCacheMiss();
                        operationScope.WithTag("result", "miss").WithTag("source", "redis").WithTag("reason", "corrupt");
                        return null;
                    }

                    RecordCacheMiss();
                    operationScope.WithTag("result", "miss").WithTag("source", "redis");
                    return null;
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                    operationScope.WithTag("result", "error").WithTag("source", "redis");
                    _performanceMonitor.RecordErrorWithContext("cache_error", "redis_get",
                        new Dictionary<string, object>
                        {
                            ["cache_type"] = CacheType,
                            ["key_family"] = GetCacheKeyFamily(key),
                            ["source"] = "redis"
                        },
                        ex);
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
                    operationScope.WithTag("result", "hit").WithTag("source", "fallback");
                    return value;
                }

                _fallbackCache.TryRemove(prefixedKey, out _);
                _writeMetadata.TryRemove(prefixedKey, out _);
            }
            else
            {
                _fallbackCache.TryRemove(prefixedKey, out _);
                _writeMetadata.TryRemove(prefixedKey, out _);
            }
        }

        RecordCacheMiss();
        operationScope.WithTag("result", "miss").WithTag("source", "fallback");
        return null;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        return SetAsync(key, value, _options.GetDefaultTtlWithJitter(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
            return;

        string prefixedKey = GetPrefixedKey(key);
        if (!TrySerialize(value, prefixedKey, out var data))
        {
            return;
        }

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
                    if (_redis != null)
                    {
                        await _redisPolicy.ExecuteAsync(
                            _ => SetRedisEntryWithIndexAsync(prefixedKey, data, ttl, cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var options = new DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = ttl
                        };

                        await _redisPolicy.ExecuteAsync(
                            ct => _distributedCache.SetAsync(prefixedKey, data, options, ct),
                            cancellationToken).ConfigureAwait(false);
                        await TrackIndexedKeyAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                    }

                    _writeMetadata[prefixedKey] = new CacheWriteInfo(DateTime.UtcNow.Ticks, ttl.Ticks);
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
            // Efficient cache eviction to prevent memory leaks under pressure
            EvictEntriesIfNeeded();
            _fallbackCache[prefixedKey] = new CacheEntry(data, DateTime.UtcNow.Add(ttl));
            _writeMetadata[prefixedKey] = new CacheWriteInfo(DateTime.UtcNow.Ticks, ttl.Ticks);
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
                    if (_redis != null)
                    {
                        await _redisPolicy.ExecuteAsync(
                            _ => RemoveRedisEntryWithIndexAsync(prefixedKey, cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _redisPolicy.ExecuteAsync(
                            ct => _distributedCache.RemoveAsync(prefixedKey, ct),
                            cancellationToken).ConfigureAwait(false);
                        await RemoveIndexedKeyAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                }
            }
        }

        // Always remove from fallback cache and write metadata
        _fallbackCache.TryRemove(prefixedKey, out _);
        _writeMetadata.TryRemove(prefixedKey, out _);

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
                var indexedKeys = await db.SetMembersAsync(GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey))).ConfigureAwait(false);
                var matchingKeys = indexedKeys
                    .Select(static value => value.ToString())
                    .Where(static key => !string.IsNullOrWhiteSpace(key))
                    .Where(key => MatchesPattern(key, GetRedisStorageKey(prefixedPattern)))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (matchingKeys.Length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var indexKey = GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey));
                    var redisKeys = matchingKeys.Select(static key => (RedisKey)key).ToArray();
                    var redisValues = matchingKeys.Select(static key => (RedisValue)key).ToArray();
                    var transaction = db.CreateTransaction();
                    var deleteTask = transaction.KeyDeleteAsync(redisKeys);
                    var removeIndexTask = transaction.SetRemoveAsync(indexKey, redisValues);
                    var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
                    if (!committed)
                    {
                        throw new InvalidOperationException("Failed to remove cache entries and index members from Redis.");
                    }

                    await Task.WhenAll(deleteTask, removeIndexTask).ConfigureAwait(false);
                    for (var i = 0; i < matchingKeys.Length; i++)
                    {
                        RecordCacheEviction();
                    }
                }
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex);
            }
        }
        else if (!_isUsingFallback && _distributedCache != null)
        {
            try
            {
                var indexedKeys = await LoadDistributedIndexedKeysAsync(cancellationToken).ConfigureAwait(false);
                var matchingKeys = indexedKeys
                    .Where(key => MatchesPattern(key, prefixedPattern))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                foreach (var key in matchingKeys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                    RecordCacheEviction();
                }

                if (matchingKeys.Length > 0)
                {
                    await UpdateDistributedKeyIndexAsync(
                        keys => keys.RemoveAll(key => matchingKeys.Contains(key, StringComparer.Ordinal)),
                        cancellationToken).ConfigureAwait(false);
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
            _writeMetadata.TryRemove(key, out _);
            RecordCacheEviction();
        }

        // Prune write metadata for keys not in the fallback cache (entries written
        // while Redis was healthy exist only in _writeMetadata, not _fallbackCache).
        var metadataKeysToRemove = _writeMetadata.Keys
            .Where(k => MatchesPattern(k, prefixedPattern))
            .ToList();

        foreach (string key in metadataKeysToRemove)
        {
            _writeMetadata.TryRemove(key, out _);
        }

        return;
    }

    /// <inheritdoc />
    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken = default) where T : class
    {
        return await GetOrSetAsync(key, factory, _options.GetDefaultTtlWithJitter(), cancellationToken).ConfigureAwait(false);
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

        // Use distributed lock for cache stampede protection
        await using var distributedLock = await AcquireDistributedLockAsync(GetPrefixedKey(key), cancellationToken).ConfigureAwait(false);

        if (distributedLock.IsAcquired)
        {
            // Re-check cache after acquiring the distributed lock
            cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (cached != null)
                return cached;

            // Not in cache, call factory under distributed lock
            T? value = await factory(cancellationToken).ConfigureAwait(false);

            // Cache the result if not null
            if (value != null)
                await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);

            return value;
        }
        else
        {
            // Failed to acquire distributed lock, fallback to local lock
            await using var keyLock = await AcquireKeyLockAsync(GetPrefixedKey(key), cancellationToken).ConfigureAwait(false);

            // Re-check cache after acquiring local lock
            cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (cached != null)
                return cached;

            // Call factory with local lock only
            T? value = await factory(cancellationToken).ConfigureAwait(false);

            // Cache the result if not null
            if (value != null)
                await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);

            return value;
        }
    }

    /// <inheritdoc />
    public async Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
            return CacheEntryMetadata<T>.Miss();

        string prefixedKey = GetPrefixedKey(key);
        using var operationScope = _performanceMonitor.StartOperation("cache_get_metadata")
            .WithTag("cache_type", CacheType)
            .WithTag("key_family", GetCacheKeyFamily(key));

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
                    byte[]? data;
                    if (_redis != null)
                    {
                        data = await _redisGetPolicy.ExecuteAsync(
                            _ => GetRedisEntryAsync(prefixedKey),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        data = await _redisGetPolicy.ExecuteAsync(
                            ct => _distributedCache.GetAsync(prefixedKey, ct),
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (data != null && TryDeserialize(data, prefixedKey, out T? value))
                    {
                        // Resolve remaining TTL from in-memory write metadata (no extra Redis command).
                        // Falls back to a multiplexer TTL query only for entries written by another
                        // node in multi-node deployments (where the multiplexer is always available).
                        var resolvedTtl = await ResolveRemainingTtlAsync(prefixedKey).ConfigureAwait(false);

                        RecordCacheHit();
                        operationScope.WithTag("result", "hit").WithTag("source", "redis");
                        return new CacheEntryMetadata<T>(value, resolvedTtl);
                    }

                    if (data != null)
                    {
                        await RemoveCorruptRedisEntryAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
                        RecordCacheMiss();
                        operationScope.WithTag("result", "miss").WithTag("source", "redis").WithTag("reason", "corrupt");
                        return CacheEntryMetadata<T>.Miss();
                    }

                    RecordCacheMiss();
                    operationScope.WithTag("result", "miss").WithTag("source", "redis");
                    return CacheEntryMetadata<T>.Miss();
                }
                catch (Exception ex)
                {
                    HandleRedisFailure(ex);
                    operationScope.WithTag("result", "error").WithTag("source", "redis");
                    _performanceMonitor.RecordErrorWithContext("cache_error", "redis_get_metadata",
                        new Dictionary<string, object>
                        {
                            ["cache_type"] = CacheType,
                            ["key_family"] = GetCacheKeyFamily(key),
                            ["source"] = "redis"
                        },
                        ex);
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
                    operationScope.WithTag("result", "hit").WithTag("source", "fallback");
                    var remaining = entry.ExpiresAt - DateTime.UtcNow;
                    return new CacheEntryMetadata<T>(value, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }

                _fallbackCache.TryRemove(prefixedKey, out _);
                _writeMetadata.TryRemove(prefixedKey, out _);
            }
            else
            {
                _fallbackCache.TryRemove(prefixedKey, out _);
                _writeMetadata.TryRemove(prefixedKey, out _);
            }
        }

        RecordCacheMiss();
        operationScope.WithTag("result", "miss").WithTag("source", "fallback");
        return CacheEntryMetadata<T>.Miss();
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
            var lastFailureDt = new DateTime(Volatile.Read(ref _lastRedisFailureTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - lastFailureDt > _options.RetryInterval)
            {
                var restored = await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
                return restored || _options.EnableFallback;
            }

            return _options.EnableFallback;
        }

        try
        {
            if (_redis != null)
            {
                var db = _redis.GetDatabase();
                _ = await db.PingAsync().ConfigureAwait(false);
            }
            else
            {
                await _redisGetPolicy.ExecuteAsync(
                    ct => _distributedCache.GetAsync(HealthCheckKey, ct),
                    cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            return _options.EnableFallback;
        }
    }

    /// <summary>
    /// Efficiently evicts cache entries when at or near capacity to prevent memory leaks.
    /// Uses a two-pass approach: first remove expired entries, then remove oldest entries
    /// without expensive sorting operations.
    /// </summary>
    private void EvictEntriesIfNeeded()
    {
        // Fast path: if well under capacity, no eviction needed
        if (_fallbackCache.Count < _options.FallbackMaxEntries * 0.9)
            return;

        var now = DateTime.UtcNow;
        var targetSize = Math.Max(1, (int)(_options.FallbackMaxEntries * 0.75)); // Target 75% capacity

        // First pass: Remove expired entries (most efficient)
        var expiredKeys = new List<string>();
        foreach (var kvp in _fallbackCache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _fallbackCache.TryRemove(key, out _);
            _writeMetadata.TryRemove(key, out _);
        }

        // If still over capacity after removing expired entries, remove oldest
        if (_fallbackCache.Count > targetSize)
        {
            // Collect entries to evict in a single pass (O(n) instead of O(n log n))
            var entriesToEvict = new List<(string Key, DateTime ExpiresAt)>();
            var evictCount = _fallbackCache.Count - targetSize;

            foreach (var kvp in _fallbackCache)
            {
                entriesToEvict.Add((kvp.Key, kvp.Value.ExpiresAt));

                // Only collect enough entries to reduce to target size
                if (entriesToEvict.Count >= evictCount * 2) // Collect 2x to account for concurrent removals
                    break;
            }

            // Sort only the collected entries (much smaller set), then evict the oldest
            entriesToEvict.Sort((a, b) => a.ExpiresAt.CompareTo(b.ExpiresAt));

            var evicted = 0;
            foreach (var (key, _) in entriesToEvict)
            {
                if (_fallbackCache.TryRemove(key, out _))
                {
                    _writeMetadata.TryRemove(key, out _);
                    evicted++;
                    if (evicted >= evictCount)
                        break;
                }
            }
        }
    }

    private bool ShouldRetryRedis(DateTime now)
    {
        var lastFailure = new DateTime(Volatile.Read(ref _lastRedisFailureTicks), DateTimeKind.Utc);
        return _isUsingFallback && _distributedCache != null && now - lastFailure > _options.RetryInterval;
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
            if (_isUsingFallback)
            {
                _fallbackCache.Clear();
                _writeMetadata.Clear();
            }

            _isUsingFallback = false;
            RedisCacheServiceLog.RedisConnectionRestored(_logger);
            return true;
        }
        catch
        {
            Volatile.Write(ref _lastRedisFailureTicks, DateTime.UtcNow.Ticks);
            return false;
        }
    }

    private bool TryDeserialize<T>(byte[] data, string cacheKey, out T? value) where T : class
    {
        try
        {
            var typeInfo = ResolveTypeInfo<T>();
            value = JsonSerializer.Deserialize(data, typeInfo) as T;
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

    private bool TrySerialize<T>(T value, string cacheKey, out byte[] data) where T : class
    {
        try
        {
            var typeInfo = ResolveTypeInfo<T>();
            data = JsonSerializer.SerializeToUtf8Bytes((object?)value, typeInfo);
            return true;
        }
        catch (NotSupportedException ex)
        {
            data = Array.Empty<byte>();
            RedisCacheServiceLog.CacheEntrySerializationFailed(_logger, cacheKey, ex);
            return false;
        }
    }

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo ResolveTypeInfo<T>() where T : class
    {
        return CacheJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Type '{typeof(T).FullName}' is not registered in {nameof(CacheJsonContext)}.");
    }

    private async Task RemoveCorruptRedisEntryAsync(string prefixedKey, CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return;
        }

        try
        {
            if (_redis != null)
            {
                await _redisPolicy.ExecuteAsync(
                    _ => RemoveRedisEntryWithIndexAsync(prefixedKey, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _redisPolicy.ExecuteAsync(
                    ct => _distributedCache.RemoveAsync(prefixedKey, ct),
                    cancellationToken).ConfigureAwait(false);
                await RemoveIndexedKeyAsync(prefixedKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            HandleRedisFailure(ex);
        }
    }

    private async ValueTask<DistributedLock> AcquireDistributedLockAsync(string key, CancellationToken cancellationToken)
    {
        // Try to acquire distributed lock using Redis first
        if (!_isUsingFallback && _redis != null)
        {
            try
            {
                var lockKey = $"lock:{key}";
                var lockValue = Environment.MachineName + ":" + Environment.ProcessId + ":" + Guid.NewGuid().ToString("N")[..8];
                var lockExpiry = TimeSpan.FromSeconds(30); // Lock timeout

                var db = _redis.GetDatabase();
                bool acquired = await db.StringSetAsync(lockKey, lockValue, lockExpiry, When.NotExists).ConfigureAwait(false);

                if (acquired)
                {
                    return new DistributedLock(lockKey, lockValue, db, isAcquired: true);
                }

                // Lock not acquired, but Redis is available
                return new DistributedLock(lockKey, lockValue, db, isAcquired: false);
            }
            catch (Exception ex)
            {
                // Redis failed, handle as failure but don't switch to fallback for locks
                RedisCacheServiceLog.DistributedLockFailed(_logger, key, ex);
            }
        }

        // Fallback: no distributed lock acquired
        return new DistributedLock("", "", null, isAcquired: false);
    }

    private async ValueTask<KeyLock> AcquireKeyLockAsync(string key, CancellationToken cancellationToken)
    {
        if (_keyLocks.Count > MaxKeyLocks)
        {
            TryPruneKeyLocks();
        }

        var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new KeyLock(key, semaphore, _keyLocks);
    }

    private void TryPruneKeyLocks()
    {
        if (Interlocked.CompareExchange(ref _isPruningKeyLocks, 1, 0) != 0)
        {
            return;
        }

        try
        {
            PruneKeyLocks();
        }
        finally
        {
            Volatile.Write(ref _isPruningKeyLocks, 0);
        }
    }

    private void PruneKeyLocks()
    {
        foreach (var kvp in _keyLocks)
        {
            // Only remove if idle (CurrentCount == 1) and TryRemove atomically matches
            // the exact key-value pair. If another thread obtained this semaphore via
            // GetOrAdd between our check and removal, TryRemove will not remove it
            // because GetOrAdd returns the existing instance (same reference).
            // However, after removal a new thread could create a different semaphore.
            // This is acceptable: pruning is best-effort cleanup, not a correctness gate.
            if (kvp.Value.CurrentCount == 1)
            {
                _keyLocks.TryRemove(kvp);
            }
        }
    }

    private string GetPrefixedKey(string key)
    {
        var scopedKey = CacheScopeKeys.EnsureScoped(key, SchemaContext.AmbientCurrentSchema);
        return $"{_options.KeyPrefix}{scopedKey}";
    }

    private void HandleRedisFailure(Exception ex)
    {
        if (!_isUsingFallback)
        {
            _isUsingFallback = true;
            Volatile.Write(ref _lastRedisFailureTicks, DateTime.UtcNow.Ticks);
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

    private async Task TrackIndexedKeyAsync(string prefixedKey, CancellationToken cancellationToken)
    {
        if (string.Equals(prefixedKey, GetPrefixedKey(CacheKeyIndexKey), StringComparison.Ordinal))
        {
            return;
        }

        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.SetAddAsync(
                    GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey)),
                    GetRedisStorageKey(prefixedKey)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                RedisCacheIndexLog.RedisIndexTrackFailed(_logger, prefixedKey, ex);
                return;
            }
        }

        if (_distributedCache != null)
        {
            await UpdateDistributedKeyIndexAsync(
                keys => keys.Add(prefixedKey),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveIndexedKeyAsync(string prefixedKey, CancellationToken cancellationToken)
    {
        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.SetRemoveAsync(
                    GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey)),
                    GetRedisStorageKey(prefixedKey)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                RedisCacheIndexLog.RedisIndexRemoveFailed(_logger, prefixedKey, ex);
                return;
            }
        }

        if (_distributedCache != null)
        {
            await UpdateDistributedKeyIndexAsync(
                keys => keys.RemoveAll(key => string.Equals(key, prefixedKey, StringComparison.Ordinal)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> LoadDistributedIndexedKeysAsync(CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return [];
        }

        var data = await _distributedCache.GetAsync(GetPrefixedKey(CacheKeyIndexKey), cancellationToken).ConfigureAwait(false);
        if (data == null)
        {
            return [];
        }

        var index = JsonSerializer.Deserialize(data, CacheJsonContext.Default.CachedCacheKeyIndex);
        return index?.Keys ?? [];
    }

    private async Task UpdateDistributedKeyIndexAsync(Action<List<string>> update, CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return;
        }

        await _distributedIndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadDistributedIndexedKeysAsync(cancellationToken).ConfigureAwait(false);
            var keys = existing.ToList();
            update(keys);
            keys = keys
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (keys.Count == 0)
            {
                await _distributedCache.RemoveAsync(GetPrefixedKey(CacheKeyIndexKey), cancellationToken).ConfigureAwait(false);
                return;
            }

            var data = JsonSerializer.SerializeToUtf8Bytes(
                new CachedCacheKeyIndex([.. keys]),
                CacheJsonContext.Default.CachedCacheKeyIndex);
            await _distributedCache.SetAsync(
                GetPrefixedKey(CacheKeyIndexKey),
                data,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheKeyIndexTtl },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _distributedIndexLock.Release();
        }
    }

    private string GetRedisStorageKey(string prefixedKey) => $"{_distributedCacheKeyPrefix}{prefixedKey}";

    private async Task<byte[]?> GetRedisEntryAsync(string prefixedKey)
    {
        var db = _redis!.GetDatabase();
        var actualRedisKey = GetRedisStorageKey(prefixedKey);
        var value = await db.StringGetAsync(actualRedisKey).ConfigureAwait(false);
        return value.HasValue
            ? (byte[]?)value
            : null;
    }

    private async Task SetRedisEntryWithIndexAsync(string prefixedKey, byte[] data, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _redis!.GetDatabase();
        var actualRedisKey = GetRedisStorageKey(prefixedKey);
        var indexKey = GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey));
        var transaction = db.CreateTransaction();
        var writeTask = transaction.StringSetAsync(actualRedisKey, data, ttl);
        var addIndexTask = transaction.SetAddAsync(indexKey, actualRedisKey);
        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw new InvalidOperationException("Failed to write cache entry and index member to Redis.");
        }

        await Task.WhenAll(writeTask, addIndexTask).ConfigureAwait(false);
    }

    private async Task RemoveRedisEntryWithIndexAsync(string prefixedKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _redis!.GetDatabase();
        var actualRedisKey = GetRedisStorageKey(prefixedKey);
        var indexKey = GetRedisStorageKey(GetPrefixedKey(CacheKeyIndexKey));
        var transaction = db.CreateTransaction();
        var removeValueTask = transaction.KeyDeleteAsync(actualRedisKey);
        var removeIndexTask = transaction.SetRemoveAsync(indexKey, actualRedisKey);
        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw new InvalidOperationException("Failed to remove cache entry and index member from Redis.");
        }

        await Task.WhenAll(removeValueTask, removeIndexTask).ConfigureAwait(false);
    }

    internal static string GetCacheKeyFamily(string key) => CacheKeyUtilities.GetCacheKeyFamily(key);

    /// <summary>
    /// Resolves the remaining TTL for a cached entry, preferring the lightweight in-memory
    /// write metadata over a Redis multiplexer TTL command. This avoids an extra Redis round
    /// trip on the hot path and works even when <see cref="_redis"/> is null (e.g., startup
    /// connection failure where <see cref="IDistributedCache"/> recovered independently).
    /// Falls back to <c>KeyTimeToLiveAsync</c> only for entries written by another node in
    /// multi-node deployments; the resolved TTL is then cached locally so the next read
    /// for the same key uses the fast path without another multiplexer round trip.
    /// When neither write metadata nor a multiplexer is available, returns
    /// <see cref="TimeSpan.Zero"/> so the entry is treated as near-expiry and a single
    /// background refresh populates write metadata for subsequent reads.
    /// </summary>
    private async Task<TimeSpan> ResolveRemainingTtlAsync(string prefixedKey)
    {
        // Fast path: entry was written (or TTL previously resolved) by this node.
        if (_writeMetadata.TryGetValue(prefixedKey, out var writeInfo))
        {
            long elapsedTicks = DateTime.UtcNow.Ticks - writeInfo.WrittenAtTicks;
            long remainingTicks = writeInfo.TtlTicks - elapsedTicks;
            return remainingTicks > 0 ? TimeSpan.FromTicks(remainingTicks) : TimeSpan.Zero;
        }

        // Slow path: entry written by another node — query Redis TTL via multiplexer.
        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                var actualRedisKey = $"{_distributedCacheKeyPrefix}{prefixedKey}";
                TimeSpan? ttl = await db.KeyTimeToLiveAsync(actualRedisKey).ConfigureAwait(false);
                var resolved = ttl ?? TimeSpan.Zero;

                // Cache the resolved TTL locally so subsequent reads for the same key
                // skip the multiplexer round trip (self-corrects after one lookup).
                if (resolved > TimeSpan.Zero)
                {
                    _writeMetadata[prefixedKey] = new CacheWriteInfo(DateTime.UtcNow.Ticks, resolved.Ticks);
                }

                return resolved;
            }
            catch
            {
                // TTL lookup failed — disable near-expiry detection for this entry.
                return TimeSpan.MaxValue;
            }
        }

        // No write metadata and no multiplexer — treat as near-expiry so the background
        // refresh coordinator enqueues one refresh, which populates write metadata for
        // subsequent reads. Worst case is a single unnecessary proactive refresh per key.
        return TimeSpan.Zero;
    }

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        var nowTicks = now.Ticks;

        var expiredKeys = _fallbackCache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string key in expiredKeys)
        {
            _fallbackCache.TryRemove(key, out _);
        }

        // Prune write metadata whose TTL has elapsed
        var expiredMetadataKeys = _writeMetadata
            .Where(kvp => (nowTicks - kvp.Value.WrittenAtTicks) >= kvp.Value.TtlTicks)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (string key in expiredMetadataKeys)
        {
            _writeMetadata.TryRemove(key, out _);
        }

        int totalCleaned = expiredKeys.Count + expiredMetadataKeys.Count;
        if (totalCleaned > 0)
        {
            RedisCacheServiceLog.CleanupExpiredCacheEntries(_logger, totalCleaned);
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

    private void RecordCacheOperation(string operation, TimeSpan duration, bool success = true)
    {
        _performanceMonitor.RecordCacheLatency(CacheType, operation, duration, success);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer.Dispose();
        _fallbackCache.Clear();
        _writeMetadata.Clear();
        _distributedIndexLock.Dispose();
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

            // Eagerly remove idle semaphores. Use TryRemove with exact KVP match
            // so we only remove if the dictionary still holds this specific instance.
            // This is safe: if another thread re-acquired, CurrentCount would be 0.
            if (_semaphore.CurrentCount == 1)
            {
                _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(_key, _semaphore));
            }
        }
    }

    private sealed class DistributedLock : IAsyncDisposable, IDisposable
    {
        private readonly string _lockKey;
        private readonly string _lockValue;
        private readonly IDatabase? _database;
        private bool _disposed;

        public bool IsAcquired { get; }

        public DistributedLock(string lockKey, string lockValue, IDatabase? database, bool isAcquired)
        {
            _lockKey = lockKey;
            _lockValue = lockValue;
            _database = database;
            IsAcquired = isAcquired;
        }

        public void Dispose()
        {
            // For synchronous disposal, we cannot safely perform async Redis operations
            // without risking deadlocks. Mark as disposed and rely on lock expiration.
            // Use DisposeAsync() for proper async cleanup when possible.
            _disposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync().ConfigureAwait(false);
        }

        private async ValueTask ReleaseAsync()
        {
            if (_disposed || !IsAcquired || _database == null || string.IsNullOrEmpty(_lockKey))
            {
                return;
            }

            _disposed = true;

            try
            {
                // Use Lua script to ensure we only delete our own lock
                const string script = """
                    if redis.call("GET", KEYS[1]) == ARGV[1] then
                        return redis.call("DEL", KEYS[1])
                    else
                        return 0
                    end
                    """;

                await _database.ScriptEvaluateAsync(script, new RedisKey[] { _lockKey }, new RedisValue[] { _lockValue }).ConfigureAwait(false);
            }
            catch
            {
                // Ignore failures during lock release - the lock will expire anyway
            }
        }
    }

    private sealed record CacheEntry(byte[] Data, DateTime ExpiresAt);

    /// <summary>
    /// Lightweight record tracking when a cache entry was written and its original TTL.
    /// Used by <see cref="GetWithMetadataAsync{T}"/> to compute remaining TTL without
    /// a separate Redis TTL command, and without requiring an <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    private readonly record struct CacheWriteInfo(long WrittenAtTicks, long TtlTicks);

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

        [LoggerMessage(1006, LogLevel.Debug, "Failed to acquire distributed lock for cache key {CacheKey}")]
        public static partial void DistributedLockFailed(ILogger logger, string cacheKey, Exception exception);

        [LoggerMessage(1007, LogLevel.Warning, "Failed to serialize cache entry {CacheKey}")]
        public static partial void CacheEntrySerializationFailed(ILogger logger, string cacheKey, Exception exception);
    }
}
