// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed partial class CacheServiceResponseCacheBudgetTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_ReservationCountAtCapacity_RejectsNewKeysAndAllowsReplacement()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 2);
        var payload = new CachedResponse(new byte[32], "application/json");
        await cache.SetAsync("first", payload, Lifetime);
        await cache.SetAsync("second", payload, Lifetime);

        await cache.SetAsync("third", payload, Lifetime);

        Assert.Equal(2, backend.Responses.Count);
        Assert.False(backend.Responses.ContainsKey("response:third"));
        await cache.SetAsync("first", payload, Lifetime);
        Assert.Equal(2, backend.Responses.Count);
        await cache.RemoveAsync("first");
        await cache.SetAsync("third", payload, Lifetime);
        Assert.True(backend.Responses.ContainsKey("response:third"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_KeyExceedsByteBudget_DoesNotAdmitASmallPayload()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 512);

        await cache.SetAsync(new string('k', 1024), new CachedResponse(new byte[1], "application/json"), Lifetime);

        Assert.Empty(backend.Responses);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_NamespaceChanges_ReclaimsOnlyEvictedPayloads(bool bindKeys)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 21_000);
        var payload = new CachedResponse(new byte[10_000], "application/json");
        const string alpha = "query:featureserver:service:alpha:layer:1:first";
        const string beta = "query:featureserver:service:beta:layer:1:first";
        const string nextAlpha = "query:featureserver:service:alpha:layer:1:next";
        var first = bindKeys ? await cache.BindKeyAsync(alpha) : alpha;
        var second = bindKeys ? await cache.BindKeyAsync(beta) : beta;
        await cache.SetAsync(first, payload, Lifetime);
        await cache.SetAsync(second, payload, Lifetime);
        Assert.Equal(2, backend.ResponseWrites);

        await cache.RemoveByPatternAsync("query:featureserver:service:alpha:layer:1:*");
        await cache.SetAsync(bindKeys ? await cache.BindKeyAsync(nextAlpha) : nextAlpha, payload, Lifetime);

        Assert.Equal(3, backend.ResponseWrites);
        Assert.Equal(2, backend.Responses.Count);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(nextAlpha));
        Assert.Null(await cache.GetAsync<CachedResponse>(first));
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(second));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_OldGenerationWriteInFlight_RetainsPayloadBudget()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        const string prefix = "query:featureserver:service:alpha:layer:1:";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeResponseWrite = async _ =>
        {
            started.TrySetResult();
            await complete.Task;
        };
        var payload = new CachedResponse(new byte[10_000], "application/json");
        var oldKey = await cache.BindKeyAsync(prefix + "first");
        var oldWrite = cache.SetAsync(oldKey, payload, Lifetime);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.RemoveByPatternAsync(prefix + "*");
        }
        finally
        {
            complete.TrySetResult();
            await oldWrite;
        }

        await cache.SetAsync(await cache.BindKeyAsync(prefix + "second"), payload, Lifetime);

        Assert.Single(backend.Responses);
        Assert.Equal(1, backend.ResponseWrites);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(oldKey));
        Assert.Null(await cache.GetAsync<CachedResponse>(prefix + "second"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_OlderWriteFails_DoesNotReleaseANewerReservation()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 15_000);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedWrites = 0;
        backend.BeforeResponseWrite = async key =>
        {
            if (key == "response:shared" && Interlocked.Increment(ref sharedWrites) == 1)
            {
                firstStarted.SetResult();
                await failFirst.Task;
                throw new IOException("Injected response write failure");
            }
        };
        var payload = new CachedResponse(new byte[10_000], "application/json");
        var olderWrite = cache.SetAsync("shared", payload, Lifetime);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync("shared", payload, Lifetime);
        }
        finally
        {
            failFirst.TrySetResult();
        }
        await Assert.ThrowsAsync<IOException>(() => olderWrite);

        await cache.SetAsync("another", payload, Lifetime);

        Assert.Single(backend.Responses);
        Assert.True(backend.Responses.ContainsKey("response:shared"));
    }

    [UnitTheory]
    [InlineData(1, 65_536)]
    [InlineData(20, 11_000)]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_NewGenerationFillDuringInvalidation_RetainsItsBudget(int maxEntries, int maxBytes)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries, maxBytes);
        const string prefix = "query:featureserver:service:alpha:layer:1:";
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInvalidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.AfterVersionWrite = async _ =>
        {
            published.TrySetResult();
            await finishInvalidation.Task;
        };
        var payload = new CachedResponse(new byte[10_000], "application/json");
        var invalidation = cache.RemoveByPatternAsync(prefix + "*");
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync(await cache.BindKeyAsync(prefix + "first"), payload, Lifetime);
        }
        finally
        {
            finishInvalidation.TrySetResult();
            await invalidation;
        }

        await cache.SetAsync(await cache.BindKeyAsync(prefix + "second"), payload, Lifetime);

        Assert.Equal(1, backend.ResponseWrites);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(prefix + "first"));
        Assert.Null(await cache.GetAsync<CachedResponse>(prefix + "second"));
    }

    [UnitTheory]
    [InlineData(1, 65_536)]
    [InlineData(20, 11_000)]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_OtherSchema_KeepsReadableReservation(int maxEntries, int maxBytes)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries, maxBytes);
        var schema = new SchemaContext();
        const string prefix = "query:featureserver:service:alpha:layer:1:";
        var payload = new CachedResponse(new byte[10_000], "application/json");
        try
        {
            schema.CurrentSchema = "schema_b";
            await cache.SetAsync(await cache.BindKeyAsync(prefix + "first"), payload, Lifetime);
            schema.CurrentSchema = "schema_a";
            await cache.RemoveByPatternAsync(prefix + "*");
            schema.CurrentSchema = "schema_b";
            Assert.Same(payload, await cache.GetAsync<CachedResponse>(prefix + "first"));
            await cache.SetAsync(await cache.BindKeyAsync(prefix + "second"), payload, Lifetime);
            Assert.Equal(1, backend.ResponseWrites);
            Assert.Null(await cache.GetAsync<CachedResponse>(prefix + "second"));
        }
        finally { schema.CurrentSchema = null; }
    }

    [UnitTheory]
    [InlineData(1, 65_536)]
    [InlineData(20, 11_000)]
    [Operation(Operations.Cache)]
    public async Task SetAsync_SameKeyInDifferentSchemas_ReservesEachPayload(int maxEntries, int maxBytes)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries, maxBytes);
        var schema = new SchemaContext();
        const string key = "query:featureserver:service:alpha:layer:1:first";
        var payload = new CachedResponse(new byte[10_000], "application/json");
        try
        {
            schema.CurrentSchema = "schema_b";
            await cache.SetAsync(await cache.BindKeyAsync(key), payload, Lifetime);
            schema.CurrentSchema = "schema_a";
            await cache.SetAsync(await cache.BindKeyAsync(key), payload, Lifetime);
            Assert.Equal(1, backend.ResponseWrites);
            Assert.Null(await cache.GetAsync<CachedResponse>(key));
            schema.CurrentSchema = "schema_b";
            Assert.Same(payload, await cache.GetAsync<CachedResponse>(key));
        }
        finally { schema.CurrentSchema = null; }
    }

    private static CacheServiceResponseCache ResponseCache(RecordingCache backend,
        int maxEntries = 20, int maxBytes = 65_536)
        => new(backend.Service, new CacheOptions
        {
            ResponseCacheMaxEntryBytes = 10_000,
            ResponseCacheMaxEntries = maxEntries,
            ResponseCacheMaxBytes = maxBytes
        });

    private sealed class RecordingCache
    {
        private readonly ConcurrentDictionary<string, string> _versions = new(StringComparer.Ordinal);
        private int _responseWrites;
        public ICacheService Service { get; } = Substitute.For<ICacheService>();
        public ConcurrentDictionary<string, CachedResponse> Responses { get; } = new(StringComparer.Ordinal);
        public Func<string, Task>? BeforeResponseWrite { get; set; }
        public Func<string, Task>? AfterVersionWrite { get; set; }
        public Func<string, Task>? BeforeResponseRemove { get; set; }
        public Func<string, Task>? AfterResponseRemove { get; set; }
        public int ResponseWrites => Volatile.Read(ref _responseWrites);

        private static string ScopedKey(string key) => SchemaContext.AmbientCurrentSchema is { } schema
            ? CacheScopeKeys.EnsureScoped(key, schema) : key;

        public RecordingCache()
        {
            Service.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var key = ScopedKey(call.ArgAt<string>(0));
                    return Task.FromResult(_versions.GetValueOrDefault(key));
                });
            Service.GetAsync<CachedResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Responses.GetValueOrDefault(ScopedKey(call.ArgAt<string>(0)))));
            Service.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var key = ScopedKey(call.ArgAt<string>(0));
                    _versions[key] = call.ArgAt<string>(1);
                    if (AfterVersionWrite is { } afterWrite)
                    {
                        await afterWrite(key);
                    }
                });
            Service.SetAsync(Arg.Any<string>(), Arg.Any<CachedResponse>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var key = ScopedKey(call.ArgAt<string>(0));
                    if (BeforeResponseWrite is { } beforeWrite)
                    {
                        await beforeWrite(key);
                    }
                    Responses[key] = call.ArgAt<CachedResponse>(1);
                    Interlocked.Increment(ref _responseWrites);
                });
            Service.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var key = ScopedKey(call.ArgAt<string>(0));
                if (BeforeResponseRemove is { } beforeRemove)
                {
                    await beforeRemove(key);
                }
                Responses.TryRemove(key, out _);
                if (AfterResponseRemove is { } afterRemove)
                {
                    await afterRemove(key);
                }
            });
        }
    }
}
