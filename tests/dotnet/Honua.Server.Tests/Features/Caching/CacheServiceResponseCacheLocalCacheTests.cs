// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Verifies the in-process namespace-version cache and concurrent-fetch behaviour
/// introduced in PA-052.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class CacheServiceResponseCacheLocalCacheTests
{
    /// <summary>
    /// The second call with the same namespace keys must NOT issue any additional
    /// GetAsync calls to the backing cache service (local cache hit within TTL).
    /// </summary>
    [Fact]
    public async Task SetAsync_SecondCallWithSameNamespaces_ServesLocalCacheWithoutRedis()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response-version:query:featureserver"] = "v1",
            ["response-version:query:featureserver:layer:3"] = "v2",
            ["response-version:query:featureserver:service:alpha"] = "v3",
            ["response-version:query:featureserver:service:alpha:layer:3"] = "v4"
        };
        var cache = new CountingCacheService(versions);
        var responseCache = new CacheServiceResponseCache(cache);

        // First call — 4 namespace version lookups expected.
        await responseCache.SetAsync("query:featureserver:service:alpha:layer:3:keyA", "payload", TimeSpan.FromMinutes(1));
        var getCountAfterFirst = cache.GetCallCount;

        // Second call with the SAME four namespace keys — should hit local cache.
        await responseCache.SetAsync("query:featureserver:service:alpha:layer:3:keyB", "payload", TimeSpan.FromMinutes(1));
        var getCountAfterSecond = cache.GetCallCount;

        Assert.Equal(4, getCountAfterFirst);
        // No additional GetAsync calls for namespace versions on the second call.
        Assert.Equal(getCountAfterFirst, getCountAfterSecond);
    }

    /// <summary>
    /// After a RemoveByPatternAsync invalidates a namespace version, the local
    /// cache entry for that namespace must be evicted so the next call re-fetches
    /// from Redis rather than serving a stale version key.
    /// </summary>
    [Fact]
    public async Task RemoveByPatternAsync_InvalidatesLocalCache_NextCallRefetchesFromRedis()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response-version:query:featureserver:service:alpha:layer:3"] = "v1"
        };
        var cache = new CountingCacheService(versions);
        var responseCache = new CacheServiceResponseCache(cache);

        // Warm the local cache.
        await responseCache.SetAsync("query:featureserver:service:alpha:layer:3:key1", "payload", TimeSpan.FromMinutes(1));
        var warmGetCount = cache.GetCallCount;

        // Invalidate the namespace — this should evict the local cache entry.
        await responseCache.RemoveByPatternAsync("query:featureserver:service:alpha:layer:3:*");

        // Next SetAsync must re-fetch namespace versions from Redis (local cache is cold).
        await responseCache.SetAsync("query:featureserver:service:alpha:layer:3:key2", "payload", TimeSpan.FromMinutes(1));
        var postInvalidationGetCount = cache.GetCallCount;

        Assert.True(postInvalidationGetCount > warmGetCount,
            "Expected at least one additional namespace version lookup after invalidation, " +
            $"but GetCallCount stayed at {warmGetCount}.");
    }

    /// <summary>
    /// Namespace version lookups for a cold cache are issued concurrently via Task.WhenAll
    /// rather than sequentially, so N missing namespaces produce a single round-trip latency.
    /// We verify this by checking that all N GetAsync calls are started before any
    /// completes — using a gate-based fake cache service.
    /// </summary>
    [Fact]
    public async Task SetAsync_MultipleNamespaceMisses_IssuedConcurrently()
    {
        // Gate-based cache: each namespace-version GetAsync waits for all expected tasks
        // to start before any returns. If the implementation issued tasks sequentially,
        // the gate would never open (task 2 can't start until task 1 returns) and the
        // test would time out. Completion without timeout proves concurrency.
        const int expectedNamespaces = 4;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response-version:query:featureserver"] = "a",
            ["response-version:query:featureserver:layer:7"] = "b",
            ["response-version:query:featureserver:service:svc"] = "c",
            ["response-version:query:featureserver:service:svc:layer:7"] = "d"
        };

        var cache = new GatedCacheService(versions, expectedConcurrency: expectedNamespaces, onAllStarted: gate);
        var responseCache = new CacheServiceResponseCache(cache);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await responseCache
            .SetAsync("query:featureserver:service:svc:layer:7:key1", "payload", TimeSpan.FromMinutes(1), cts.Token)
            .WaitAsync(cts.Token);

        // All 4 namespace-version lookups started before any returned.
        Assert.Equal(expectedNamespaces, cache.StartedCount);
    }

    /// <summary>
    /// Missing namespace version ("0") default is preserved by the new batched path.
    /// </summary>
    [Fact]
    public async Task SetAsync_MissingNamespaceVersion_DefaultsToZero()
    {
        // Provide a cache that returns null for all namespace version keys.
        var cache = new CountingCacheService(new Dictionary<string, string>(StringComparer.Ordinal));
        var responseCache = new CacheServiceResponseCache(cache);

        await responseCache.SetAsync("query:featureserver:service:alpha:layer:3:keyA", "payload", TimeSpan.FromMinutes(1));

        // Storage key must contain :v:0:0:0:0 (four namespaces all defaulting to "0").
        Assert.Single(cache.ValueWrites);
        Assert.Contains(":v:0:0:0:0", cache.ValueWrites[0], StringComparison.Ordinal);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private sealed class CountingCacheService : ICacheService
    {
        private readonly Dictionary<string, string> _versionValues;
        private int _getCallCount;

        public CountingCacheService(IReadOnlyDictionary<string, string>? versions = null)
        {
            _versionValues = versions is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(versions, StringComparer.Ordinal);
        }

        public int GetCallCount => Volatile.Read(ref _getCallCount);
        public List<string> ValueWrites { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (key.StartsWith("response-version:", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _getCallCount);
                if (typeof(T) == typeof(string) && _versionValues.TryGetValue(key, out var v))
                    return Task.FromResult<T?>((T?)(object?)v);
            }
            return Task.FromResult<T?>(null);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
            => SetAsync(key, value, TimeSpan.FromMinutes(1), cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            if (!key.StartsWith("response-version:", StringComparison.Ordinal))
            {
                ValueWrites.Add(key);
            }
            else
            {
                _versionValues[key] = value?.ToString() ?? string.Empty;
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => GetOrSetAsync(key, factory, TimeSpan.FromMinutes(1), cancellationToken);

        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            var v = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (v != null) return v;
            v = await factory(cancellationToken).ConfigureAwait(false);
            if (v != null) await SetAsync(key, v, ttl, cancellationToken).ConfigureAwait(false);
            return v;
        }

        public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(CacheEntryMetadata<T>.Miss());
    }

    /// <summary>
    /// A <see cref="ICacheService"/> whose namespace-version GetAsync calls wait
    /// for all expected concurrent calls to start before any returns, proving
    /// that the callers issued them concurrently via Task.WhenAll.
    /// </summary>
    private sealed class GatedCacheService : ICacheService
    {
        private readonly Dictionary<string, string> _versions;
        private readonly int _expectedConcurrency;
        private readonly TaskCompletionSource<bool> _gate;
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);

        public GatedCacheService(
            Dictionary<string, string> versions,
            int expectedConcurrency,
            TaskCompletionSource<bool> onAllStarted)
        {
            _versions = versions;
            _expectedConcurrency = expectedConcurrency;
            _gate = onAllStarted;
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (key.StartsWith("response-version:", StringComparison.Ordinal))
            {
                var count = Interlocked.Increment(ref _startedCount);
                if (count >= _expectedConcurrency)
                {
                    _gate.TrySetResult(true);
                }
                // Wait until all expected tasks have started before any returns.
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (typeof(T) == typeof(string) && _versions.TryGetValue(key, out var v))
                    return (T?)(object?)v;
            }
            return null;
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => GetOrSetAsync(key, factory, TimeSpan.FromMinutes(1), cancellationToken);

        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            var v = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
            if (v != null) return v;
            v = await factory(cancellationToken).ConfigureAwait(false);
            return v;
        }

        public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(CacheEntryMetadata<T>.Miss());
    }
}
