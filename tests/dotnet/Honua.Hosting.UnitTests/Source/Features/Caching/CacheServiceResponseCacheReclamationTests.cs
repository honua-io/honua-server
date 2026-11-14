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
    private const string AlphaLayer = "query:featureserver:service:alpha:layer:1:";

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveAsync_BackendDeletionInFlight_BlocksReplacementUntilBudgetRelease()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 1);
        var payload = new CachedResponse(new byte[32], "application/json");
        var boundKey = await cache.BindKeyAsync(AlphaLayer + "first");
        await cache.SetAsync(boundKey, payload, Lifetime);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.AfterResponseRemove = async _ =>
        {
            removed.TrySetResult();
            await finishRemoval.Task;
        };
        var removal = cache.RemoveAsync(boundKey);
        try
        {
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync(boundKey, payload, Lifetime);
            Assert.Equal(1, backend.ResponseWrites);
        }
        finally
        {
            finishRemoval.TrySetResult();
            await removal;
        }

        await cache.SetAsync(boundKey, payload, Lifetime);
        await cache.SetAsync("overflow", payload, Lifetime);
        Assert.Equal(2, backend.ResponseWrites);
        Assert.Single(backend.Responses);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(boundKey));
        Assert.Null(await cache.GetAsync<CachedResponse>("overflow"));
    }

    [UnitTheory]
    [InlineData(1, 65_536)]
    [InlineData(20, 11_000)]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_EvictedPayload_ReleasesEntryAndByteCapacity(int maxEntries, int maxBytes)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries, maxBytes);
        var payload = new CachedResponse(new byte[10_000], "application/json");
        var oldKey = await cache.BindKeyAsync(AlphaLayer + "first");
        await cache.SetAsync(oldKey, payload, Lifetime);

        await cache.RemoveByPatternAsync(AlphaLayer + "*");
        await cache.SetAsync(await cache.BindKeyAsync(AlphaLayer + "second"), payload, Lifetime);

        Assert.Equal(2, backend.ResponseWrites);
        Assert.Single(backend.Responses);
        Assert.Null(await cache.GetAsync<CachedResponse>(oldKey));
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(AlphaLayer + "second"));
    }

    [UnitTheory]
    [InlineData("query:odata:layer:1:first", "query:odata:layer:1:*", "query:odata:layer:2:first")]
    [InlineData("query:ogc:collection:alpha:first", "query:ogc:collection:alpha:*", "query:ogc:collection:beta:first")]
    [InlineData(AlphaLayer + "first", "query:featureserver:service:alpha:*", "query:featureserver:service:beta:layer:1:first")]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_SelectedNamespace_PreservesOtherPayloads(string key, string pattern, string otherKey)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 2);
        var payload = new CachedResponse(new byte[32], "application/json");
        var boundKey = await cache.BindKeyAsync(key);
        var otherBoundKey = await cache.BindKeyAsync(otherKey);
        await cache.SetAsync(boundKey, payload, Lifetime);
        await cache.SetAsync(otherBoundKey, payload, Lifetime);

        await cache.RemoveByPatternAsync(pattern);
        await cache.SetAsync("replacement", payload, Lifetime);
        await cache.SetAsync("overflow", payload, Lifetime);

        Assert.Null(await cache.GetAsync<CachedResponse>(boundKey));
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(otherBoundKey));
        Assert.Same(payload, await cache.GetAsync<CachedResponse>("replacement"));
        Assert.Null(await cache.GetAsync<CachedResponse>("overflow"));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_BackendFailure_RetainsBudgetAndUnlocksBoundKey(bool failVersionWrite)
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 1);
        var payload = new CachedResponse(new byte[32], "application/json");
        var boundKey = await cache.BindKeyAsync(AlphaLayer + "first");
        await cache.SetAsync(boundKey, payload, Lifetime);
        static Task Fail(string _) => throw new IOException("Injected invalidation failure");
        if (failVersionWrite)
        {
            backend.AfterVersionWrite = Fail;
        }
        else
        {
            backend.BeforeResponseRemove = Fail;
        }

        await Assert.ThrowsAsync<IOException>(() => cache.RemoveByPatternAsync(AlphaLayer + "*"));
        await cache.SetAsync("overflow", payload, Lifetime);
        await cache.SetAsync(boundKey, payload, Lifetime);

        Assert.Equal(2, backend.ResponseWrites);
        Assert.Single(backend.Responses);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(boundKey));
        Assert.Null(await cache.GetAsync<CachedResponse>("overflow"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_EvictionInFlight_DoesNotAdmitBoundReplacement()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 1);
        var payload = new CachedResponse(new byte[32], "application/json");
        var boundKey = await cache.BindKeyAsync(AlphaLayer + "first");
        await cache.SetAsync(boundKey, payload, Lifetime);
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeResponseRemove = async _ =>
        {
            removing.TrySetResult();
            await finishRemoval.Task;
        };
        var invalidation = cache.RemoveByPatternAsync(AlphaLayer + "*");
        try
        {
            await removing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync(boundKey, payload, Lifetime);
            await cache.SetAsync("overflow", payload, Lifetime);
            Assert.Equal(1, backend.ResponseWrites);
        }
        finally
        {
            finishRemoval.TrySetResult();
            await invalidation;
        }

        await cache.SetAsync("replacement", payload, Lifetime);
        Assert.Single(backend.Responses);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>("replacement"));
        Assert.Null(await cache.GetAsync<CachedResponse>(boundKey));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_OlderWritePendingAfterReplacement_RetainsPayloadBudget()
    {
        var backend = new RecordingCache();
        var cache = ResponseCache(backend, maxEntries: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        backend.BeforeResponseWrite = async _ =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                started.TrySetResult();
                await finishWrite.Task;
            }
        };
        var payload = new CachedResponse(new byte[32], "application/json");
        var boundKey = await cache.BindKeyAsync(AlphaLayer + "first");
        var olderWrite = cache.SetAsync(boundKey, payload, Lifetime);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.SetAsync(boundKey, payload, Lifetime);
            await cache.RemoveByPatternAsync(AlphaLayer + "*");
        }
        finally
        {
            finishWrite.TrySetResult();
            await olderWrite;
        }

        await cache.SetAsync("overflow", payload, Lifetime);
        Assert.Equal(2, backend.ResponseWrites);
        Assert.Single(backend.Responses);
        Assert.Same(payload, await cache.GetAsync<CachedResponse>(boundKey));
    }
}
