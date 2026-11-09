// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class CacheServiceResponseCacheBudgetTests
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
    public async Task RemoveByPatternAsync_NamespaceChanges_ReleasesOnlyItsReservations(bool bindKeys)
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
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(second));
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

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task BindKeyAsync_RecoveryEpochChanges_ReleasesInvalidatedReservations()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 2);
        const string first = "query:featureserver:service:alpha:layer:1:first";
        const string second = "query:featureserver:service:alpha:layer:1:second";
        var payload = new CachedResponse(new byte[32], "application/json");
        await cache.SetAsync("raw", payload, Lifetime);
        await cache.SetAsync(await cache.BindKeyAsync(first), payload, Lifetime);
        await backend.Service.SetAsync("response-version:recovery", "new-epoch", Lifetime);

        await cache.SetAsync(await cache.BindKeyAsync(second), payload, Lifetime);
        await cache.SetAsync("another-raw", payload, Lifetime);

        Assert.Equal(3, backend.ResponseWrites);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>("raw"));
        Assert.False(backend.Responses.ContainsKey("response:another-raw"));
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(second));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task BindKeyAsync_OlderEpochReadFinishesLast_RetainsNewerReservationBudget()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 1);
        const string prefix = "query:featureserver:service:alpha:layer:1:";
        var payload = new CachedResponse(new byte[32], "application/json");
        await cache.SetAsync(await cache.BindKeyAsync(prefix + "first"), payload, Lifetime);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var epochReads = 0;
        backend.ReadVersion = async (key, version) =>
        {
            if (key == "response-version:recovery" && Interlocked.Increment(ref epochReads) == 1)
            {
                readStarted.TrySetResult();
                await finishRead.Task;
            }
            return version;
        };
        var oldRead = cache.BindKeyAsync(prefix + "old-read");
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await backend.Service.SetAsync("response-version:recovery", "new-epoch", Lifetime);
            await cache.SetAsync(await cache.BindKeyAsync(prefix + "second"), payload, Lifetime);
        }
        finally
        {
            finishRead.TrySetResult();
            await oldRead;
        }

        await cache.SetAsync(await cache.BindKeyAsync(prefix + "third"), payload, Lifetime);

        Assert.Equal(2, backend.ResponseWrites);
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
        public Func<string, string?, Task<string?>>? ReadVersion { get; set; }
        public int ResponseWrites => Volatile.Read(ref _responseWrites);

        public RecordingCache()
        {
            Service.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var key = call.ArgAt<string>(0);
                    var version = _versions.GetValueOrDefault(key);
                    return ReadVersion is { } readVersion ? await readVersion(key, version) : version;
                });
            Service.GetAsync<CachedResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Responses.GetValueOrDefault(call.ArgAt<string>(0))));
            Service.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    _versions[call.ArgAt<string>(0)] = call.ArgAt<string>(1);
                    return Task.CompletedTask;
                });
            Service.SetAsync(Arg.Any<string>(), Arg.Any<CachedResponse>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var key = call.ArgAt<string>(0);
                    if (BeforeResponseWrite is { } beforeWrite)
                    {
                        await beforeWrite(key);
                    }
                    Responses[key] = call.ArgAt<CachedResponse>(1);
                    Interlocked.Increment(ref _responseWrites);
                });
            Service.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Responses.TryRemove(call.ArgAt<string>(0), out _);
                return Task.CompletedTask;
            });
        }
    }
}
