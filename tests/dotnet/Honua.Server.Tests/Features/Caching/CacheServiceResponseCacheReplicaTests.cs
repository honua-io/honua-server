// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;

namespace Honua.Server.Tests.Features.Caching;

[Trait("Tier", "Fast")]
public sealed class CacheServiceResponseCacheReplicaTests
{
    public static TheoryData<string, string> NamespacePatterns => new()
    {
        { "query:odata:layer:42:hash", "query:odata:layer:42:*" },
        { "query:odata:layer:42:hash", "query:odata:layer:*" },
        { "query:ogc:collection:roads:hash", "query:ogc:collection:roads:*" },
        { "query:ogc:collection:roads:hash", "query:ogc:collection:*" },
        { "query:featureserver:service:roads:layer:42:hash", "query:featureserver:service:roads:layer:42:*" },
        { "query:featureserver:service:roads:layer:42:hash", "query:featureserver:service:*:layer:42:*" },
        { "query:featureserver:service:roads:layer:42:hash", "query:featureserver:service:roads:*" },
        { "query:featureserver:service:roads:layer:42:hash", "query:featureserver:service:*" },
        { "response:query:odata:layer:42:hash", "response:query:odata:layer:42:*" }
    };

    [Theory]
    [MemberData(nameof(NamespacePatterns))]
    public async Task GetAsync_RemoteInvalidation_WarmReplicaDoesNotReturnOldResponse(string key, string pattern)
    {
        var shared = new SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        var reader = new CacheServiceResponseCache(shared);
        await reader.SetAsync(key, "before", TimeSpan.FromMinutes(5));
        Assert.Equal("before", await reader.GetAsync<string>(key));

        await writer.RemoveByPatternAsync(pattern);

        Assert.Null(await writer.GetAsync<string>(key));
        Assert.Null(await reader.GetAsync<string>(key));
        await writer.SetAsync(key, "after", TimeSpan.FromMinutes(5));
        Assert.Equal("after", await reader.GetAsync<string>(key));
    }

    [Theory]
    [MemberData(nameof(NamespacePatterns))]
    public async Task GetOrCreateAsync_RemoteInvalidation_WarmReplicaInvokesFactory(string key, string pattern)
    {
        var shared = new SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        var reader = new CacheServiceResponseCache(shared);
        await reader.SetAsync(key, "before", TimeSpan.FromMinutes(5));
        await writer.RemoveByPatternAsync(pattern);
        var factoryCalls = 0;

        var result = await reader.GetOrCreateAsync(key, () =>
        {
            factoryCalls++;
            return Task.FromResult("after");
        }, TimeSpan.FromMinutes(5));

        Assert.Equal("after", result);
        Assert.Equal(1, factoryCalls);
        Assert.Equal("after", await writer.GetAsync<string>(key));
    }

    [Fact]
    public async Task RemoveAsync_RemoteInvalidation_RemovesTheCurrentGeneration()
    {
        var shared = new SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        var reader = new CacheServiceResponseCache(shared);
        const string key = "query:odata:layer:42:hash";
        await reader.SetAsync(key, "before", TimeSpan.FromMinutes(5));
        await writer.RemoveByPatternAsync("query:odata:layer:42:*");
        await writer.SetAsync(key, "after", TimeSpan.FromMinutes(5));

        await reader.RemoveAsync(key);

        Assert.Null(await writer.GetAsync<string>(key));
    }

    [Theory]
    [MemberData(nameof(NamespacePatterns))]
    public async Task SetAsync_InvalidationDuringQuery_DoesNotPublishOldPayloadInCurrentGeneration(string key, string pattern)
    {
        var shared = new SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        IResponseCache reader = new CacheServiceResponseCache(shared);
        var fillKey = await reader.BindKeyAsync(key);
        Assert.Null(await reader.GetAsync<string>(fillKey));

        // The query observed old data before this edit. Another request on the
        // same reader then observes the new namespace before the old query finishes.
        await writer.RemoveByPatternAsync(pattern);
        Assert.Null(await reader.GetAsync<string>(key));
        await reader.SetAsync(fillKey, "before edit", TimeSpan.FromMinutes(5));

        Assert.Null(await writer.GetAsync<string>(key));
        Assert.Null(await reader.GetAsync<string>(key));
        var freshKey = await reader.BindKeyAsync(key);
        await reader.SetAsync(freshKey, "after edit", TimeSpan.FromMinutes(5));
        Assert.Equal("after edit", await writer.GetAsync<string>(key));
    }

    [Fact]
    public async Task SetAsync_RemoteInvalidation_StaleGenerationCannotBeRead()
    {
        var shared = new SharedCache();
        var writer = new CacheServiceResponseCache(shared);
        var reader = new CacheServiceResponseCache(shared);
        const string key = "query:odata:layer:42:hash";
        await reader.SetAsync(key, "before", TimeSpan.FromMinutes(5));
        await writer.RemoveByPatternAsync("query:odata:layer:42:*");

        // A write-only caller can retain its local generation. Reads must resolve
        // the shared generation and cannot return a value stored under the old one.
        await reader.SetAsync(key, "old generation fill", TimeSpan.FromMinutes(5));

        Assert.Null(await reader.GetAsync<string>(key));
        Assert.Null(await writer.GetAsync<string>(key));
    }

    private sealed class SharedCache : ICacheService
    {
        private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(_values.GetValueOrDefault(key) as T);
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
            => SetAsync(key, value, TimeSpan.FromMinutes(5), cancellationToken);
        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The production namespace path must not scan keys.");
        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => GetOrSetAsync(key, factory, TimeSpan.FromMinutes(5), cancellationToken);
        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            var cached = await GetAsync<T>(key, cancellationToken);
            if (cached is not null) return cached;
            var created = await factory(cancellationToken);
            if (created is not null) await SetAsync(key, created, ttl, cancellationToken);
            return created;
        }
        public async Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => new(await GetAsync<T>(key, cancellationToken), TimeSpan.FromMinutes(5));
    }
}
