// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class ExternalRevisionResponseCacheTests
{
    [UnitTest]
    public async Task CommittedEdit_RetiresCachedResponsesOnBothHosts()
    {
        var revisions = new RevisionSource();
        var first = new ExternalRevisionResponseCache(new MemoryResponses(), revisions);
        var second = new ExternalRevisionResponseCache(new MemoryResponses(), revisions);
        const string key = "response:query:ogc:collection:roads:query";
        await first.SetAsync(key, "old-first", TimeSpan.FromMinutes(1));
        await second.SetAsync(key, "old-second", TimeSpan.FromMinutes(1));

        revisions.Revision = "2";

        (await first.GetAsync<string>(key)).Should().BeNull();
        (await second.GetAsync<string>(key)).Should().BeNull();
        (await second.GetOrCreateAsync(key, () => Task.FromResult("fresh"), TimeSpan.FromMinutes(1)))
            .Should().Be("fresh");
    }

    [UnitTest]
    public async Task EditDuringQuery_PreventsLateFillFromEnteringNewGeneration()
    {
        var revisions = new RevisionSource();
        var cache = new ExternalRevisionResponseCache(new MemoryResponses(), revisions);
        const string key = "response:query:odata:layer:3:query";
        var bound = await cache.BindKeyAsync(key);

        revisions.Revision = "2";
        await cache.SetAsync(bound, "old-query-result", TimeSpan.FromMinutes(1));

        (await cache.GetAsync<string>(key)).Should().BeNull();
        (await cache.GetAsync<string>(bound)).Should().Be("old-query-result");
        (await cache.BindKeyAsync(bound)).Should().Be(bound);
        revisions.Reads.Should().Be(2);
    }

    [UnitTest]
    public async Task RevisionReadFailure_DoesNotReplayAnExistingEntry()
    {
        var revisions = new RevisionSource();
        var cache = new ExternalRevisionResponseCache(new MemoryResponses(), revisions);
        await cache.SetAsync("key", "old", TimeSpan.FromMinutes(1));
        revisions.Fail = true;

        var read = () => cache.GetAsync<string>("key");

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task PatternInvalidation_PreservesInnerNamespaceContract()
    {
        var inner = new MemoryResponses();
        var cache = new ExternalRevisionResponseCache(inner, new RevisionSource());

        await cache.RemoveByPatternAsync("response:query:ogc:collection:roads:*");

        inner.RemovedPattern.Should().Be("response:query:ogc:collection:roads:*");
    }

    private sealed class RevisionSource : IExternalLayerRevisionSource
    {
        public string Revision { get; set; } = "1";
        public bool Fail { get; set; }
        public int Reads { get; private set; }

        public Task<string> GetRevisionAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Fail ? Task.FromException<string>(new InvalidOperationException("unavailable")) : Task.FromResult(Revision);
        }
    }

    private sealed class MemoryResponses : IResponseCache
    {
        private readonly Dictionary<string, object> _values = [];
        public string? RemovedPattern { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(_values.TryGetValue(key, out var value) ? (T)value : null);

        public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        {
            var value = await GetAsync<T>(key, cancellationToken);
            if (value is not null)
            {
                return value;
            }
            value = await factory();
            await SetAsync(key, value, expiration, cancellationToken);
            return value;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        {
            RemovedPattern = pattern;
            return Task.CompletedTask;
        }
    }
}
