using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Infrastructure.Caching;
using Xunit;

public sealed class ResponseCacheConcurrencyTests
{
    [Fact]
    public async Task RemoteInvalidation_PreviouslyWarmReplica_DoesNotServeOldResponse()
    {
        // Two independent process-local version caches share one authoritative backing store.
        var shared = new SharedCache();
        var writerReplica = new CacheServiceResponseCache(shared);
        var readerReplica = new CacheServiceResponseCache(shared);
        const string key = "query:odata:layer:42:query-hash";
        await readerReplica.SetAsync(key, "before-edit", TimeSpan.FromMinutes(5));
        Assert.Equal("before-edit", await readerReplica.GetAsync<string>(key));

        await writerReplica.RemoveByPatternAsync("query:odata:layer:42:*");

        Assert.Null(await writerReplica.GetAsync<string>(key));
        Assert.Null(await readerReplica.GetAsync<string>(key));
    }

    private sealed class SharedCache : ICacheService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
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
            _values.Remove(key);
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
