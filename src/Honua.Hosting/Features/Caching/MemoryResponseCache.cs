// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Infrastructure.Caching;

internal sealed class MemoryResponseCache : IResponseCache, IDisposable
{
    // Stable cache_name dimension for honua_cache_*_total counters; keep low-cardinality.
    internal const string CacheName = "response";

    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private bool _disposed;

    public MemoryResponseCache(IMemoryCache cache, ILogger<MemoryResponseCache> logger, int maxEntries = 10000)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        _cache = cache;
        _maxEntries = maxEntries;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cache.TryGetValue(key, out var value) && value is T typed)
        {
            HonuaCacheMetrics.RecordHit(CacheName);
            return Task.FromResult<T?>(typed);
        }

        HonuaCacheMetrics.RecordMiss(CacheName);
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration
        };
        options.RegisterPostEvictionCallback((evictedKey, _, reason, _) =>
        {
            if (evictedKey is string evicted)
            {
                _keys.TryRemove(evicted, out _);
            }

            // Explicit Remove() also fires the post-eviction callback (reason=Removed),
            // so this single call covers TTL-, capacity-, and explicit-eviction paths.
            HonuaCacheMetrics.RecordEviction(CacheName);
        });

        if (_keys.Count >= _maxEntries)
        {
            TrimEntries();
        }

        _cache.Set(key, value, options);
        _keys[key] = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, invoking
    /// <paramref name="factory"/> to produce and cache it on a miss.
    /// </summary>
    /// <remarks>
    /// This method provides <b>at-least-once</b> factory semantics, not single-flight:
    /// N concurrent misses for the same key may each invoke <paramref name="factory"/>
    /// (the last write wins). This is an intentional trade-off for a response cache,
    /// where factories are expected to be inexpensive and idempotent. Callers with
    /// expensive or non-idempotent factories must coordinate single-flight execution
    /// themselves (e.g. their own per-key lock) before calling this method.
    /// </remarks>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default) where T : class
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(factory);

        var existing = await GetAsync<T>(key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = await factory();
        ArgumentNullException.ThrowIfNull(created, nameof(factory));

        await SetAsync(key, created, expiration, cancellationToken);
        return created;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        ValidateKey(pattern);
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = TryGetSimplePrefix(pattern);
        Regex? regex = null;
        if (prefix == null)
        {
            regex = new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50));
        }

        foreach (var key in _keys.Keys)
        {
            var isMatch = prefix != null
                ? key.StartsWith(prefix, StringComparison.Ordinal)
                : regex!.IsMatch(key);
            if (!isMatch)
            {
                continue;
            }

            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private static string? TryGetSimplePrefix(string pattern)
    {
        if (pattern.Length > 0 &&
            pattern[^1] == '*' &&
            pattern.IndexOf('*') == pattern.Length - 1)
        {
            return pattern[..^1];
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _keys.Clear();
        _disposed = true;
    }

    private void TrimEntries()
    {
        var excess = _keys.Count - _maxEntries + 1;
        if (excess <= 0)
        {
            excess = Math.Max(1, _maxEntries / 20);
        }

        var keysToEvict = _keys.Keys.Take(excess).ToArray();
        foreach (var candidate in keysToEvict)
        {
            _cache.Remove(candidate);
            _keys.TryRemove(candidate, out _);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        }
    }
}
