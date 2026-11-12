// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

// Each node caches for itself; the CDN is the shared cache. Entries are stored
// serialized so a caller mutating a returned object cannot change the cached value.
internal sealed partial class InMemoryCacheService : ICacheService, ICacheHealthChecker, ICacheStorageMetricsProvider, IDisposable
{
    private const string CacheType = "layer-catalog";
    private const string StorageBackend = "memory";
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly CacheOptions _options;
    private readonly ILogger<InMemoryCacheService> _logger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ITimer _cleanupTimer;
    private volatile bool _disposed;

    public InMemoryCacheService(
        IOptions<CacheOptions> options,
        ILogger<InMemoryCacheService> logger,
        IPerformanceMonitor performanceMonitor,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cleanupTimer = CreateCleanupTimer();
    }

    private ITimer CreateCleanupTimer()
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return _timeProvider.CreateTimer(CleanupExpiredEntries, null, CleanupInterval, CleanupInterval);
        }

        // This singleton can first be resolved by a request or readiness probe.
        // Its periodic callback must not retain that caller's AsyncLocal graph.
        using (ExecutionContext.SuppressFlow())
        {
            return _timeProvider.CreateTimer(CleanupExpiredEntries, null, CleanupInterval, CleanupInterval);
        }
    }

    public bool IsUsingFallback => false;

    public bool IsProcessLocal => true;

    public Task<CacheStorageMetrics> GetStorageMetricsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CacheStorageMetrics
        {
            Backend = StorageBackend,
            IsUsingFallback = false,
            LocalKeyCount = _entries.Count
        });
    }

    public Task<bool> IsCacheHealthyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(true);
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<T?>(null);
        }

        using var operationScope = StartOperation("cache_get", key);
        return Task.FromResult(GetLive<T>(GetPrefixedKey(key), operationScope).Value);
    }

    public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(CacheEntryMetadata<T>.Miss());
        }

        using var operationScope = StartOperation("cache_get_metadata", key);
        return Task.FromResult(GetLive<T>(GetPrefixedKey(key), operationScope));
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
        => SetAsync(key, value, _options.GetDefaultTtlWithJitter(), cancellationToken);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        var prefixedKey = GetPrefixedKey(key);
        var data = Serialize(value, prefixedKey);
        if (data is null || ExceedsQueryResponseEntryCap(prefixedKey, data))
        {
            return Task.CompletedTask;
        }

        EvictEntriesIfNeeded();
        _entries[prefixedKey] = new CacheEntry(data, UtcNow.Add(ttl));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(GetPrefixedKey(key), out _);
        RecordCacheMetric("eviction");
        return Task.CompletedTask;
    }

    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefixedPattern = GetPrefixedKey(pattern);

        foreach (var key in _entries.Keys.Where(key => MatchesPattern(key, prefixedPattern)).ToList())
        {
            _entries.TryRemove(key, out _);
            RecordCacheMetric("eviction");
        }

        return Task.CompletedTask;
    }

    public Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken = default) where T : class
        => GetOrSetAsync(key, factory, _options.GetDefaultTtlWithJitter(), cancellationToken);

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

        var cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        // Concurrent callers for one key wait here so the factory runs once per miss.
        using var keyLock = await AcquireKeyLockAsync(GetPrefixedKey(key), cancellationToken).ConfigureAwait(false);

        cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        if (value != null)
        {
            await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);
        }

        return value;
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private IOperationScope StartOperation(string operation, string key)
        => _performanceMonitor.StartOperation(operation)
            .WithTag("cache_type", CacheType)
            .WithTag("key_family", CacheKeyUtilities.GetCacheKeyFamily(key));

    private CacheEntryMetadata<T> GetLive<T>(string prefixedKey, IOperationScope operationScope)
        where T : class
    {
        if (_entries.TryGetValue(prefixedKey, out var entry))
        {
            var remaining = entry.ExpiresAt - UtcNow;
            var value = remaining > TimeSpan.Zero ? Deserialize<T>(entry.Data, prefixedKey) : null;
            if (value != null)
            {
                RecordCacheMetric("hit");
                operationScope.WithTag("result", "hit").WithTag("source", StorageBackend);
                return new CacheEntryMetadata<T>(value, remaining);
            }

            _entries.TryRemove(new KeyValuePair<string, CacheEntry>(prefixedKey, entry));
        }

        RecordCacheMetric("miss");
        operationScope.WithTag("result", "miss").WithTag("source", StorageBackend);
        return CacheEntryMetadata<T>.Miss();
    }

    private void EvictEntriesIfNeeded()
    {
        if (_entries.Count < _options.MaxEntries * 0.9)
        {
            return;
        }

        var now = UtcNow;
        var targetSize = Math.Max(1, (int)(_options.MaxEntries * 0.75));

        foreach (var expired in _entries.Where(kvp => kvp.Value.ExpiresAt <= now).ToList())
        {
            _entries.TryRemove(expired);
        }

        var evictCount = _entries.Count - targetSize;
        if (evictCount <= 0)
        {
            return;
        }

        // Sampling twice the eviction count tolerates concurrent removals without sorting every entry.
        var candidates = _entries
            .Take(evictCount * 2)
            .Select(kvp => (kvp.Key, kvp.Value.ExpiresAt))
            .OrderBy(candidate => candidate.ExpiresAt)
            .ToList();

        var evicted = 0;
        foreach (var (key, _) in candidates)
        {
            if (_entries.TryRemove(key, out _) && ++evicted >= evictCount)
            {
                break;
            }
        }
    }

    private T? Deserialize<T>(byte[] data, string cacheKey) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(data, ResolveTypeInfo<T>()) as T;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            InMemoryCacheServiceLog.CacheEntryDeserializationFailed(
                _logger, CacheKeyUtilities.GetCacheKeyFamily(cacheKey), LogValueRedactor.Hash(cacheKey), ex);
            return null;
        }
    }

    private byte[]? Serialize<T>(T value, string cacheKey) where T : class
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes((object?)value, ResolveTypeInfo<T>());
        }
        catch (NotSupportedException ex)
        {
            InMemoryCacheServiceLog.CacheEntrySerializationFailed(
                _logger, CacheKeyUtilities.GetCacheKeyFamily(cacheKey), LogValueRedactor.Hash(cacheKey), ex);
            return null;
        }
    }

    private static JsonTypeInfo ResolveTypeInfo<T>() where T : class
        => CacheJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Type '{typeof(T).FullName}' is not registered in {nameof(CacheJsonContext)}.");

    private bool ExceedsQueryResponseEntryCap(string prefixedKey, byte[] data)
        => data.Length > _options.ResponseCacheMaxEntryBytes &&
           (prefixedKey.Contains("query:featureserver", StringComparison.Ordinal) ||
            prefixedKey.Contains("query:ogc", StringComparison.Ordinal) ||
            prefixedKey.Contains("query:odata", StringComparison.Ordinal));

    private string GetPrefixedKey(string key)
        => _options.KeyPrefix + CacheScopeKeys.EnsureScoped(key, SchemaContext.AmbientCurrentSchema);

    private static bool MatchesPattern(string key, string pattern)
        => pattern.EndsWith('*')
            ? key.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : key.Equals(pattern, StringComparison.Ordinal);

    private void CleanupExpiredEntries(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var now = UtcNow;
        var expired = _entries.Where(kvp => kvp.Value.ExpiresAt <= now).ToList();
        foreach (var kvp in expired)
        {
            _entries.TryRemove(kvp);
        }

        if (expired.Count > 0)
        {
            InMemoryCacheServiceLog.CleanupExpiredCacheEntries(_logger, expired.Count);
        }
    }

    private void RecordCacheMetric(string outcome) => _performanceMonitor.RecordCacheMetrics(CacheType, outcome);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cleanupTimer.Dispose();
        _entries.Clear();
    }

    private sealed record CacheEntry(byte[] Data, DateTimeOffset ExpiresAt);

    private static partial class InMemoryCacheServiceLog
    {
        [LoggerMessage(1004, LogLevel.Debug, "Cleaned up {Count} expired cache entries")]
        public static partial void CleanupExpiredCacheEntries(ILogger logger, int count);

        [LoggerMessage(1005, LogLevel.Warning, "Failed to deserialize cache entry {KeyFamily} {KeyHash}")]
        public static partial void CacheEntryDeserializationFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);

        [LoggerMessage(1007, LogLevel.Warning, "Failed to serialize cache entry {KeyFamily} {KeyHash}")]
        public static partial void CacheEntrySerializationFailed(ILogger logger, string keyFamily, string keyHash, Exception exception);
    }
}
