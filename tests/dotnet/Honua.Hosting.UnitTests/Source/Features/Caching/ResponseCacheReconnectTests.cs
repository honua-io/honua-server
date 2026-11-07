// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class ResponseCacheReconnectTests
{
    private const string QueryKey = "query:featureserver:service:alpha:layer:1:query";
    private const string LayerPattern = "query:featureserver:service:alpha:layer:1:*";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_NamespaceInvalidatedDuringOutage_RejectsSharedAndLateOfflineEntriesAfterRecovery()
    {
        var shared = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var ownerBackend = new FaultyCache(shared);
        await using var ownerService = CacheNode(ownerBackend.Cache);
        await using var replicaService = CacheNode(new FaultyCache(shared).Cache);
        var owner = new CacheServiceResponseCache(ownerService, Settings());
        var replica = new CacheServiceResponseCache(replicaService, Settings());
        var oldPayload = new CachedResponse(new byte[32], "application/json");
        await owner.SetAsync(QueryKey, oldPayload, Lifetime);
        Assert.NotNull(await replica.GetAsync<CachedResponse>(QueryKey));
        ownerBackend.WriteUnavailable = true;
        await ownerService.SetAsync("partition-marker", "unavailable", Lifetime);
        Assert.True(ownerService.IsUsingFallback);
        await owner.RemoveByPatternAsync(LayerPattern);
        var offlineKey = await owner.BindKeyAsync(QueryKey);

        ownerBackend.WriteUnavailable = false;
        Assert.True(await RestoreAsync(ownerService));

        Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
        await owner.SetAsync(offlineKey, oldPayload, Lifetime);
        Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_AnotherInvalidationArrivesDuringRecovery_DoesNotPublishAnObsoleteGeneration()
    {
        var shared = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var ownerBackend = new FaultyCache(shared);
        await using var ownerService = CacheNode(ownerBackend.Cache);
        await using var replicaService = CacheNode(new FaultyCache(shared).Cache);
        var owner = new CacheServiceResponseCache(ownerService, Settings());
        var replica = new CacheServiceResponseCache(replicaService, Settings());
        ownerBackend.WriteUnavailable = true;
        await ownerService.SetAsync("partition-marker", "unavailable", Lifetime);
        Assert.True(ownerService.IsUsingFallback);
        await owner.RemoveByPatternAsync(LayerPattern);
        ownerBackend.WriteUnavailable = false;
        var generationPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var namespaceWrites = 0;
        ownerBackend.AfterWrite = async key =>
        {
            if (key.Contains("response-version:query:featureserver", StringComparison.Ordinal)
                && Interlocked.Increment(ref namespaceWrites) == 1)
            {
                generationPublished.SetResult();
                await continueRecovery.Task;
            }
        };
        var recovery = RestoreAsync(ownerService);
        try
        {
            await generationPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var keyBeforeMutation = await replica.BindKeyAsync(QueryKey);
            await replica.SetAsync(keyBeforeMutation, new CachedResponse(new byte[32], "application/json"), Lifetime);
            await owner.RemoveByPatternAsync(LayerPattern);
        }
        finally
        {
            continueRecovery.TrySetResult();
        }

        Assert.True(await recovery.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
    }

    private static CacheOptions Settings() => new()
    {
        Enabled = true,
        ResponseCachingEnabled = true,
        EnableFallback = true,
        FallbackMaxEntries = 100,
        RetryIntervalSeconds = 300,
        KeyPrefix = "reconnect:"
    };

    private static RedisCacheService CacheNode(IDistributedCache cache)
        => new(cache, Options.Create(Settings()), NullLogger<RedisCacheService>.Instance,
            Substitute.For<IPerformanceMonitor>());

    private static Task<bool> RestoreAsync(RedisCacheService cache)
        => (Task<bool>)typeof(RedisCacheService)
            .GetMethod("TryRestoreRedisAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cache, [CancellationToken.None])!;

    private sealed class FaultyCache
    {
        public IDistributedCache Cache { get; } = Substitute.For<IDistributedCache>();
        public bool WriteUnavailable { get; set; }
        public Func<string, Task>? AfterWrite { get; set; }

        public FaultyCache(ConcurrentDictionary<string, byte[]> shared)
        {
            Cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(shared.TryGetValue(call.ArgAt<string>(0), out var bytes)
                    ? bytes.ToArray() : null));
            Cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(),
                Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    if (WriteUnavailable)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Injected Redis write failure");
                    }
                    var key = call.ArgAt<string>(0);
                    shared[key] = call.ArgAt<byte[]>(1).ToArray();
                    if (AfterWrite is { } afterWrite)
                    {
                        await afterWrite(key);
                    }
                });
            Cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                shared.TryRemove(call.ArgAt<string>(0), out _);
                return Task.CompletedTask;
            });
        }
    }
}
