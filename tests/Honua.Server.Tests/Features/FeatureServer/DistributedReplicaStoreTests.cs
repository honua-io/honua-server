// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.FeatureServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FeatureServer;

[Protocol(Protocols.FeatureServer)]
public sealed class DistributedReplicaStoreTests
{
    [UnitTest]
    [Operation(Operations.CreateReplica)]
    public async Task SetAsync_WithDistributedMemoryCache_GetAsyncReturnsReplica()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedReplicaStore(
            distributedCache,
            NullLogger<DistributedReplicaStore>.Instance);

        var createdAt = DateTimeOffset.UtcNow;
        var replica = CreateReplicaState("replica-a", "svc-a", createdAt);

        await store.SetAsync(replica);
        var result = await store.GetAsync(replica.ReplicaId);

        result.Should().NotBeNull();
        result!.ReplicaId.Should().Be(replica.ReplicaId);
        result.ServiceId.Should().Be("svc-a");
        result.SyncModel.Should().Be("perReplica");
    }

    [UnitTest]
    [Operation(Operations.CreateReplica)]
    public async Task SetAsync_WithoutDistributedCache_UsesFallbackUntilExpiry()
    {
        var store = new DistributedReplicaStore(
            cache: null,
            NullLogger<DistributedReplicaStore>.Instance);

        var replica = CreateReplicaState("replica-b", "svc-b", DateTimeOffset.UtcNow);

        await store.SetAsync(replica, TimeSpan.FromMilliseconds(60));
        var immediate = await store.GetAsync(replica.ReplicaId);
        immediate.Should().NotBeNull();

        await Task.Delay(120);
        var expired = await store.GetAsync(replica.ReplicaId);
        expired.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetAsync_WhenDistributedCacheThrows_DoesNotReturnNodeLocalFallback()
    {
        var cache = new ThrowingDistributedCache(
            throwOnGetAsync: true,
            throwOnSetAsync: true);
        var store = new DistributedReplicaStore(
            cache,
            NullLogger<DistributedReplicaStore>.Instance);

        var replica = CreateReplicaState("replica-c", "svc-c", DateTimeOffset.UtcNow);

        await store.SetAsync(replica);
        var result = await store.GetAsync(replica.ReplicaId);

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.UnRegisterReplica)]
    public async Task RemoveAsync_WhenDistributedCacheThrows_ReturnsFalseWithoutNodeLocalFallback()
    {
        var cache = new ThrowingDistributedCache(
            throwOnGetAsync: true,
            throwOnSetAsync: true,
            throwOnRemoveAsync: true);
        var store = new DistributedReplicaStore(
            cache,
            NullLogger<DistributedReplicaStore>.Instance);

        var replica = CreateReplicaState("replica-d", "svc-d", DateTimeOffset.UtcNow);
        await store.SetAsync(replica);

        var removed = await store.RemoveAsync(replica.ReplicaId);
        removed.Should().BeFalse();

        var shouldBeMissing = await store.GetAsync(replica.ReplicaId);
        shouldBeMissing.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SetGetRemove_ConcurrentOperations_DoNotThrowAndLeaveNoReplica()
    {
        var store = new DistributedReplicaStore(
            cache: null,
            NullLogger<DistributedReplicaStore>.Instance);
        var replicaIds = Enumerable.Range(0, 40)
            .Select(i => $"replica-{i}")
            .ToArray();

        await Task.WhenAll(replicaIds.Select(async replicaId =>
        {
            var replica = CreateReplicaState(replicaId, "svc-concurrent", DateTimeOffset.UtcNow);
            await store.SetAsync(replica, TimeSpan.FromMinutes(5));
            var loaded = await store.GetAsync(replicaId);
            loaded.Should().NotBeNull();
            var removed = await store.RemoveAsync(replicaId);
            removed.Should().BeTrue();
        }));

        await Task.WhenAll(replicaIds.Select(async replicaId =>
        {
            var loaded = await store.GetAsync(replicaId);
            loaded.Should().BeNull();
        }));
    }

    [UnitTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetAsync_DoesNotExtendFallbackPastOriginalDistributedExpiry()
    {
        var distributedCache = new ExpiringDistributedCache();
        var store = new DistributedReplicaStore(
            distributedCache,
            NullLogger<DistributedReplicaStore>.Instance);

        var replica = CreateReplicaState("replica-expiry", "svc-expiry", DateTimeOffset.UtcNow);
        await store.SetAsync(replica, TimeSpan.FromMilliseconds(80));

        var distributedRead = await store.GetAsync(replica.ReplicaId);
        distributedRead.Should().NotBeNull();

        await Task.Delay(120);
        distributedCache.ThrowOnGet = true;

        var expiredFallback = await store.GetAsync(replica.ReplicaId);
        expiredFallback.Should().BeNull();
    }

    private static ReplicaState CreateReplicaState(string replicaId, string serviceId, DateTimeOffset createdAt)
    {
        return new ReplicaState(
            ReplicaId: replicaId,
            ReplicaName: $"name-{replicaId}",
            ServiceId: serviceId,
            SyncModel: "perReplica",
            LayerIds: [0, 1],
            CreatedAt: createdAt);
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private readonly bool _throwOnGetAsync;
        private readonly bool _throwOnSetAsync;
        private readonly bool _throwOnRemoveAsync;

        public ThrowingDistributedCache(
            bool throwOnGetAsync = false,
            bool throwOnSetAsync = false,
            bool throwOnRemoveAsync = false)
        {
            _throwOnGetAsync = throwOnGetAsync;
            _throwOnSetAsync = throwOnSetAsync;
            _throwOnRemoveAsync = throwOnRemoveAsync;
        }

        public byte[]? Get(string key) => null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (_throwOnGetAsync)
            {
                throw new InvalidOperationException("Simulated get failure");
            }

            return Task.FromResult<byte[]?>(null);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (_throwOnSetAsync)
            {
                throw new InvalidOperationException("Simulated set failure");
            }

            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            if (_throwOnRemoveAsync)
            {
                throw new InvalidOperationException("Simulated remove failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ExpiringDistributedCache : IDistributedCache
    {
        private byte[]? _value;
        private DateTimeOffset _expiresAt;

        public bool ThrowOnGet { get; set; }

        public byte[]? Get(string key) => _value;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (ThrowOnGet)
            {
                throw new InvalidOperationException("Simulated get failure");
            }

            if (_value == null || _expiresAt <= DateTimeOffset.UtcNow)
            {
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult<byte[]?>(_value);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _value = value;
            _expiresAt = DateTimeOffset.UtcNow.Add(options.AbsoluteExpirationRelativeToNow ?? TimeSpan.FromMinutes(5));
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
            _value = null;
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
