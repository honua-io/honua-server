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
        var large = new CachedResponse(new byte[10_000], "application/json");
        var small = new CachedResponse(new byte[32], "application/json");
        await cache.SetAsync("shared", large, Lifetime);

        await cache.SetAsync("shared", small, Lifetime);
        await cache.SetAsync("another", large, Lifetime);

        Assert.Same(small, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Same(large, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LargeWriteOverlapsSmallReplacement_RetainsCapacityUntilWritesSettle(bool failLargeWrite)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        var large = new CachedResponse(new byte[10_000], "application/json");
        var small = new CachedResponse(new byte[32], "application/json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        backend.BeforeResponseWrite = async _ =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                started.TrySetResult();
                await finish.Task;
                if (failLargeWrite)
                {
                    throw new IOException("Injected large write failure");
                }
            }
        };
        var largeWrite = cache.SetAsync("shared", large, Lifetime);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync("shared", small, Lifetime);
            await cache.SetAsync("another", large, Lifetime);
            Assert.Null(await cache.GetAsync<CachedResponse>("another"));
        }
        finally
        {
            finish.TrySetResult();
            if (failLargeWrite)
            {
                await Assert.ThrowsAsync<IOException>(() => largeWrite);
            }
            else
            {
                await largeWrite;
            }
        }

        await cache.SetAsync("another", large, Lifetime);
        if (failLargeWrite)
        {
            Assert.Same(small, await cache.GetAsync<CachedResponse>("shared"));
            Assert.Same(large, await cache.GetAsync<CachedResponse>("another"));
        }
        else
        {
            Assert.Same(large, await cache.GetAsync<CachedResponse>("shared"));
            Assert.Null(await cache.GetAsync<CachedResponse>("another"));
            await cache.SetAsync("shared", small, Lifetime);
            await cache.SetAsync("another", large, Lifetime);
            Assert.Same(large, await cache.GetAsync<CachedResponse>("another"));
        }
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_LargerReplacementFails_ReleasesUnusedByteCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        var small = new CachedResponse(new byte[32], "application/json");
        var large = new CachedResponse(new byte[10_000], "application/json");
        await cache.SetAsync("shared", small, Lifetime);
        backend.BeforeResponseWrite = _ => throw new IOException("Injected replacement failure");

        await Assert.ThrowsAsync<IOException>(() => cache.SetAsync("shared", large, Lifetime));
        backend.BeforeResponseWrite = null;
        await cache.SetAsync("another", large, Lifetime);

        Assert.Same(small, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Same(large, await cache.GetAsync<CachedResponse>("another"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_SmallerReplacementFails_RetainsExistingPayloadCapacity()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxBytes: 11_000);
        var large = new CachedResponse(new byte[10_000], "application/json");
        await cache.SetAsync("shared", large, Lifetime);
        backend.BeforeResponseWrite = _ => throw new IOException("Injected replacement failure");

        await Assert.ThrowsAsync<IOException>(() => cache.SetAsync("shared",
            new CachedResponse(new byte[32], "application/json"), Lifetime));
        backend.BeforeResponseWrite = null;
        await cache.SetAsync("another", large, Lifetime);

        Assert.Same(large, await cache.GetAsync<CachedResponse>("shared"));
        Assert.Null(await cache.GetAsync<CachedResponse>("another"));
    }
}
