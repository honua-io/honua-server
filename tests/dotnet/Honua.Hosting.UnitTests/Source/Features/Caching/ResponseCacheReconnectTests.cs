// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
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

    [UnitTheory]
    [InlineData("affected")]
    [InlineData("another")]
    [Operation(Operations.Cache)]
    public async Task GetAsync_NamespaceInvalidatedDuringOutage_RejectsSharedAndLateOfflineEntriesAfterRecovery(string recoverySchema)
    {
        var schema = new SchemaContext();
        try
        {
            schema.CurrentSchema = "affected";
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
            schema.CurrentSchema = recoverySchema;
            Assert.True(await RestoreAsync(ownerService));
            schema.CurrentSchema = "affected";

            Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
            await owner.SetAsync(offlineKey, oldPayload, Lifetime);
            Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
        }
        finally
        {
            schema.CurrentSchema = null;
        }
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
            if (key.Contains("response-version:", StringComparison.Ordinal)
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

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_NamespaceWriteIsPending_RecoveryHidesOldAndLateResponses()
    {
        var shared = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var ownerBackend = new FaultyCache(shared);
        await using var ownerService = CacheNode(ownerBackend.Cache);
        await using var replicaService = CacheNode(new FaultyCache(shared).Cache);
        var owner = new CacheServiceResponseCache(ownerService, Settings());
        var replica = new CacheServiceResponseCache(replicaService, Settings());
        var namespaceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishNamespace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var namespaceWrites = 0;
        ownerBackend.BeforeWrite = async key =>
        {
            if (key.Contains("response-version:query:featureserver", StringComparison.Ordinal)
                && Interlocked.Increment(ref namespaceWrites) == 1)
            {
                namespaceEntered.SetResult();
                await finishNamespace.Task;
            }
        };
        var oldPayload = new CachedResponse(new byte[32], "application/json");
        var oldKey = await owner.BindKeyAsync(QueryKey);
        await owner.SetAsync(oldKey, oldPayload, Lifetime);
        var invalidating = owner.RemoveByPatternAsync("query:featureserver:service:*");
        try
        {
            await namespaceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ownerBackend.WriteUnavailable = true;
            await ownerService.SetAsync("partition-marker", "unavailable", Lifetime);
            ownerBackend.WriteUnavailable = false;

            Assert.True(await RestoreAsync(ownerService).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
            await owner.SetAsync(oldKey, oldPayload, Lifetime);
            Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
        }
        finally
        {
            finishNamespace.TrySetResult();
            await invalidating;
        }

        Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_RecoveryFencesExhausted_WaitsBeforeTryingAgain()
    {
        var shared = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var backend = new FaultyCache(shared);
        await using var service = CacheNode(backend.Cache);
        backend.WriteUnavailable = true;
        await service.SetAsync("partition-marker", "unavailable", Lifetime);
        backend.WriteUnavailable = false;
        typeof(RedisCacheService).GetField("_lastRedisFailureTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, DateTime.UtcNow.AddHours(-1).Ticks);
        var fences = 0;
        backend.AfterWrite = async key =>
        {
            if (key.EndsWith("response-version:recovery", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fences);
                await service.SetAsync("response-version:query:featureserver", Guid.NewGuid().ToString("N"), Lifetime);
            }
        };

        Assert.False(await RestoreAsync(service));
        var exhaustedFences = fences;
        Assert.True(exhaustedFences > 0);
        await service.GetAsync<string>("probe");

        Assert.Equal(exhaustedFences, fences);
        Assert.True(service.IsUsingFallback);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_PendingLiveWriteFailsDuringRecovery_RepeatsFenceBeforeGoingLive()
    {
        var shared = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var backend = new FaultyCache(shared);
        await using var service = CacheNode(backend.Cache);
        await using var replicaService = CacheNode(new FaultyCache(shared).Cache);
        var replica = new CacheServiceResponseCache(replicaService, Settings());
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeWrite = async key =>
        {
            if (key.EndsWith("pending-live", StringComparison.Ordinal))
            {
                writeStarted.TrySetResult();
                await failWrite.Task;
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Injected pending write failure");
            }
        };
        var pendingWrite = service.SetAsync("pending-live", "pending", Lifetime);
        var fences = 0;
        try
        {
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            backend.ReadUnavailable = true;
            await service.GetAsync<string>("partition-marker");
            backend.ReadUnavailable = false;
            Assert.True(service.IsUsingFallback);
            backend.AfterWrite = async key =>
            {
                if (key.EndsWith("response-version:recovery", StringComparison.Ordinal)
                    && Interlocked.Increment(ref fences) == 1)
                {
                    var intermediate = await replica.BindKeyAsync(QueryKey);
                    await replica.SetAsync(intermediate, new CachedResponse(new byte[32], "application/json"), Lifetime);
                    failWrite.TrySetResult();
                    await pendingWrite;
                }
            };

            Assert.True(await RestoreAsync(service).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, fences);
            Assert.Null(await replica.GetAsync<CachedResponse>(QueryKey));
        }
        finally
        {
            failWrite.TrySetResult();
            await pendingWrite;
        }
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
        public bool ReadUnavailable { get; set; }
        public Func<string, Task>? BeforeWrite { get; set; }
        public Func<string, Task>? AfterWrite { get; set; }

        public FaultyCache(ConcurrentDictionary<string, byte[]> shared)
        {
            Cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => ReadUnavailable
                    ? Task.FromException<byte[]?>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Injected Redis read failure"))
                    : Task.FromResult(shared.TryGetValue(call.ArgAt<string>(0), out var bytes) ? bytes.ToArray() : null));
            Cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(),
                Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    if (WriteUnavailable)
                    {
                        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Injected Redis write failure");
                    }
                    var key = call.ArgAt<string>(0);
                    if (BeforeWrite is { } beforeWrite)
                    {
                        await beforeWrite(key);
                    }
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
