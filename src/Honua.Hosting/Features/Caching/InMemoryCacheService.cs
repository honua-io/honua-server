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
    private readonly ConcurrentDictionary<string, KeyLockEntry> _keyLocks = new(StringComparer.Ordinal);
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
        return Task.FromResult(TryGetLive(GetPrefixedKey(key), operationScope, out T? value, out _) ? value : null);
    }

    public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(CacheEntryMetadata<T>.Miss());
        }

        using var operationScope = StartOperation("cache_get_metadata", key);
        return Task.FromResult(TryGetLive(GetPrefixedKey(key), operationScope, out T? value, out var remainingTtl)
            ? new CacheEntryMetadata<T>(value, remainingTtl)
            : CacheEntryMetadata<T>.Miss());
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
        if (!TrySerialize(value, prefixedKey, out var data) || ExceedsQueryResponseEntryCap(prefixedKey, data))
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

    private bool TryGetLive<T>(string prefixedKey, IOperationScope operationScope, out T? value, out TimeSpan remainingTtl)
        where T : class
    {
        value = null;
        remainingTtl = TimeSpan.Zero;
        if (_entries.TryGetValue(prefixedKey, out var entry))
        {
            var remaining = entry.ExpiresAt - UtcNow;
            if (remaining > TimeSpan.Zero && TryDeserialize(entry.Data, prefixedKey, out value))
            {
                remainingTtl = remaining;
                RecordCacheMetric("hit");
                operationScope.WithTag("result", "hit").WithTag("source", StorageBackend);
                return true;
            }

            _entries.TryRemove(new KeyValuePair<string, CacheEntry>(prefixedKey, entry));
        }

        RecordCacheMetric("miss");
        operationScope.WithTag("result", "miss").WithTag("source", StorageBackend);
        return false;
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

    private bool TryDeserialize<T>(byte[] data, string cacheKey, out T? value) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize(data, ResolveTypeInfo<T>()) as T;
            return value != null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            value = null;
            InMemoryCacheServiceLog.CacheEntryDeserializationFailed(
                _logger, CacheKeyUtilities.GetCacheKeyFamily(cacheKey), LogValueRedactor.Hash(cacheKey), ex);
            return false;
        }
    }

    private bool TrySerialize<T>(T value, string cacheKey, out byte[] data) where T : class
    {
        try
        {
            data = JsonSerializer.SerializeToUtf8Bytes((object?)value, ResolveTypeInfo<T>());
            return true;
        }
        catch (NotSupportedException ex)
        {
            data = [];
            InMemoryCacheServiceLog.CacheEntrySerializationFailed(
                _logger, CacheKeyUtilities.GetCacheKeyFamily(cacheKey), LogValueRedactor.Hash(cacheKey), ex);
            return false;
        }
    }

    private static JsonTypeInfo ResolveTypeInfo<T>() where T : class
        => CacheJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"Type '{typeof(T).FullName}' is not registered in {nameof(CacheJsonContext)}.");

    // An entry leaves the dictionary only when its last holder or waiter leaves, so every
    // caller for one key waits on the same semaphore and the dictionary holds only keys in use.
    private async ValueTask<KeyLock> AcquireKeyLockAsync(string key, CancellationToken cancellationToken)
    {
        var entry = RetainKeyLock(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseKeyLockReference(key, entry);
            throw;
        }

        return new KeyLock(this, key, entry);
    }

    private KeyLockEntry RetainKeyLock(string key)
    {
        while (true)
        {
            var entry = _keyLocks.GetOrAdd(key, static _ => new KeyLockEntry());
            lock (entry)
            {
                if (!entry.Retired)
                {
                    entry.References++;
                    return entry;
                }
            }
        }
    }

    private void ReleaseKeyLockReference(string key, KeyLockEntry entry)
    {
        lock (entry)
        {
            if (--entry.References > 0)
            {
                return;
            }

            entry.Retired = true;
            _keyLocks.TryRemove(new KeyValuePair<string, KeyLockEntry>(key, entry));
        }
    }

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

    // The semaphores never allocate a wait handle, so in-flight callers may still release
    // theirs after the service is disposed.
    private sealed class KeyLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class KeyLock(InMemoryCacheService owner, string key, KeyLockEntry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            entry.Semaphore.Release();
            owner.ReleaseKeyLockReference(key, entry);
        }
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
