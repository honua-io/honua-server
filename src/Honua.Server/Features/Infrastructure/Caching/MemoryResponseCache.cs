// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Server.Features.Infrastructure.Caching;

internal sealed class MemoryResponseCache : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private bool _disposed;

    public MemoryResponseCache(IMemoryCache cache, ILogger<MemoryResponseCache> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        ValidateKey(key);

        if (_cache.TryGetValue(key, out var value) && value is T typed)
        {
            return Task.FromResult<T?>(typed);
        }

        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration)
    {
        ValidateKey(key);

        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration
        };
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            if (evictedKey is string evicted)
            {
                _keys.TryRemove(evicted, out _);
            }
        });

        _cache.Set(key, value, options);
        _keys[key] = 0;
        return Task.CompletedTask;
    }

    public async Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration)
    {
        ValidateKey(key);

        ArgumentNullException.ThrowIfNull(factory);

        var existing = await GetAsync<T>(key);
        if (existing is not null)
        {
            return existing;
        }

        var created = await factory();
        ArgumentNullException.ThrowIfNull(created, nameof(factory));

        await SetAsync(key, created, expiration);
        return created;
    }

    public Task RemoveAsync(string key)
    {
        ValidateKey(key);

        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPatternAsync(string pattern)
    {
        ValidateKey(pattern);

        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var key in _keys.Keys)
        {
            if (!regex.IsMatch(key))
            {
                continue;
            }

            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }

        return Task.CompletedTask;
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

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        }
    }
}
