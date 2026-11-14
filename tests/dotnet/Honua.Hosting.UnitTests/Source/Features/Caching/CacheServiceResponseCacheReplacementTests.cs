// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class CacheServiceResponseCacheBudgetTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_SmallerSuccessfulReplacement_ReleasesExcessByteCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        await cache.SetAsync("shared", LargeResponse, Lifetime);

        await cache.SetAsync("shared", SmallResponse, Lifetime);
        await cache.SetAsync("another", LargeResponse, Lifetime);

        Assert.Same(SmallResponse, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LargeWriteSucceedsAfterSmallReplacement_RetainsCapacityUntilReplaced()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);

        var largeWrite = await OverlapLargeWriteWithSmallReplacementAsync(cache, backend, largeWriteFailure: null);
        await largeWrite;
        await cache.SetAsync("another", LargeResponse, Lifetime);

        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Null(await cache.GetAsync<CachedResponse>("another"));
        await cache.SetAsync("shared", SmallResponse, Lifetime);
        await cache.SetAsync("another", LargeResponse, Lifetime);
        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LargeWriteFailsAfterSmallReplacement_ReleasesItsCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);

        var largeWrite = await OverlapLargeWriteWithSmallReplacementAsync(
            cache, backend, new IOException("Injected large write failure"));
        await Assert.ThrowsAsync<IOException>(() => largeWrite);
        await cache.SetAsync("another", LargeResponse, Lifetime);

        Assert.Same(SmallResponse, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LargerReplacementFails_ReleasesUnusedByteCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        await cache.SetAsync("shared", SmallResponse, Lifetime);
        backend.BeforeResponseWrite = _ => throw new IOException("Injected replacement failure");

        await Assert.ThrowsAsync<IOException>(() => cache.SetAsync("shared", LargeResponse, Lifetime));
        backend.BeforeResponseWrite = null;
        await cache.SetAsync("another", LargeResponse, Lifetime);

        Assert.Same(SmallResponse, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_SmallerReplacementFails_RetainsExistingPayloadCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        await cache.SetAsync("shared", LargeResponse, Lifetime);
        backend.BeforeResponseWrite = _ => throw new IOException("Injected replacement failure");

        await Assert.ThrowsAsync<IOException>(() => cache.SetAsync("shared", SmallResponse, Lifetime));
        backend.BeforeResponseWrite = null;
        await cache.SetAsync("another", LargeResponse, Lifetime);

        Assert.Same(LargeResponse, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Null(await cache.GetAsync<CachedResponse>("another"));
    }

    private static readonly CachedResponse LargeResponse = new(new byte[10_000], "application/json");
    private static readonly CachedResponse SmallResponse = new(new byte[32], "application/json");

    private static async Task<Task> OverlapLargeWriteWithSmallReplacementAsync(
        CacheServiceResponseCache cache, RecordingCache backend, Exception? largeWriteFailure)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        backend.BeforeResponseWrite = async _ =>
        {
            if (Interlocked.Increment(ref writes) != 1)
            {
                return;
            }

            started.TrySetResult();
            await finish.Task;
            if (largeWriteFailure is not null)
            {
                throw largeWriteFailure;
            }
        };
        var largeWrite = cache.SetAsync("shared", LargeResponse, Lifetime);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync("shared", SmallResponse, Lifetime);
            await cache.SetAsync("another", LargeResponse, Lifetime);
            Assert.Null(await cache.GetAsync<CachedResponse>("another"));
        }
        finally
        {
            finish.TrySetResult();
        }

        return largeWrite;
    }
}
