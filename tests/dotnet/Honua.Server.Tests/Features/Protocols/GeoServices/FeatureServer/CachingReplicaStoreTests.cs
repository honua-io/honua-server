// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class CachingReplicaStoreTests
{
    [Fact]
    public async Task SetAsync_WhenRepositorySucceedsAndCacheWriteFails_DoesNotThrow()
    {
        var repository = new RecordingReplicaRepository();
        var cache = CreateCache(throwOnSet: true);
        var sut = CreateStore(repository, cache.Store);
        var replica = CreateReplicaState("replica-set-cache-fails");

        await sut.Invoking(s => s.SetAsync(replica)).Should().NotThrowAsync();

        repository.UpsertCalls.Should().Be(1);
        cache.Backend.SetCalls.Should().Be(1);
        (await repository.GetAsync(replica.ReplicaId)).Should().NotBeNull();
    }

    [Fact]
    public async Task SetAsync_WhenRepositoryThrows_DoesNotTouchCache()
    {
        var repository = new RecordingReplicaRepository { ThrowOnUpsert = true };
        var cache = CreateCache();
        var sut = CreateStore(repository, cache.Store);
        var replica = CreateReplicaState("replica-set-db-fails");

        await sut.Invoking(s => s.SetAsync(replica)).Should().ThrowAsync<InvalidOperationException>();

        repository.UpsertCalls.Should().Be(1);
        cache.Backend.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAsync_WhenRepositorySucceedsAndCacheRemoveFails_ReturnsTrue()
    {
        var repository = new RecordingReplicaRepository();
        var replica = CreateReplicaState("replica-remove-cache-fails");
        await repository.UpsertAsync(ToRecord(replica));

        var cache = CreateCache(throwOnRemove: true);
        var sut = CreateStore(repository, cache.Store);

        var removed = await sut.RemoveAsync(replica.ReplicaId);

        removed.Should().BeTrue();
        repository.RemoveCalls.Should().Be(1);
        cache.Backend.RemoveCalls.Should().Be(1);
    }

    [Fact]
    public async Task RemoveAsync_WhenRepositoryThrows_DoesNotTouchCache()
    {
        var repository = new RecordingReplicaRepository { ThrowOnRemove = true };
        var cache = CreateCache();
        var sut = CreateStore(repository, cache.Store);

        await sut.Invoking(s => s.RemoveAsync("replica-remove-db-fails")).Should().ThrowAsync<InvalidOperationException>();

        repository.RemoveCalls.Should().Be(1);
        cache.Backend.RemoveCalls.Should().Be(0);
    }

    private static CachingReplicaStore CreateStore(RecordingReplicaRepository repository, DistributedReplicaStore cache)
    {
        return new CachingReplicaStore(
            cache,
            repository,
            NullLogger<CachingReplicaStore>.Instance);
    }

    private static CacheHarness CreateCache(bool throwOnSet = false, bool throwOnRemove = false)
    {
        var backend = new RecordingDistributedCache(throwOnSet, throwOnRemove);
        return new CacheHarness(
            new DistributedReplicaStore(
                backend,
                NullLogger<DistributedReplicaStore>.Instance),
            backend);
    }

    private static ReplicaState CreateReplicaState(string replicaId) => new(
        replicaId,
        $"Replica {replicaId}",
        "svc-1",
        "perReplica",
        [0, 1],
        DateTimeOffset.UtcNow);

    private static ReplicaRecord ToRecord(ReplicaState state) => new()
    {
        ReplicaId = state.ReplicaId,
        ReplicaName = state.ReplicaName,
        ServiceId = state.ServiceId,
        SyncModel = state.SyncModel,
        LayerIds = state.LayerIds,
        CreatedAt = state.CreatedAt,
        LastSyncTime = state.LastSyncTime,
        LastSyncGeneration = state.LastSyncGeneration
    };

    private sealed record CacheHarness(DistributedReplicaStore Store, RecordingDistributedCache Backend);

    private sealed class RecordingReplicaRepository : IReplicaRepository
    {
        private readonly ConcurrentDictionary<string, ReplicaRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public int UpsertCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public bool ThrowOnUpsert { get; init; }

        public bool ThrowOnRemove { get; init; }

        public Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            if (ThrowOnUpsert)
            {
                throw new InvalidOperationException("Simulated repository upsert failure");
            }

            _records[record.ReplicaId] = record;
            return Task.CompletedTask;
        }

        public Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
        {
            _records.TryGetValue(replicaId, out var record);
            return Task.FromResult<ReplicaRecord?>(record);
        }

        public Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
        {
            var records = _records.Values
                .Where(record => string.Equals(record.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ReplicaRecord>>(records);
        }

        public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("Simulated repository remove failure");
            }

            return Task.FromResult(_records.TryRemove(replicaId, out _));
        }
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private readonly bool _throwOnSet;
        private readonly bool _throwOnRemove;

        public RecordingDistributedCache(bool throwOnSet = false, bool throwOnRemove = false)
        {
            _throwOnSet = throwOnSet;
            _throwOnRemove = throwOnRemove;
        }

        public int SetCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public byte[]? Get(string key) => null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult<byte[]?>(value);
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.CompletedTask;

        public void Remove(string key)
        {
            RemoveCalls++;
            if (_throwOnRemove)
            {
                throw new InvalidOperationException("cache remove failed");
            }

            _values.TryRemove(key, out _);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            RemoveCalls++;
            if (_throwOnRemove)
            {
                throw new InvalidOperationException("cache remove failed");
            }

            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            SetCalls++;
            if (_throwOnSet)
            {
                throw new InvalidOperationException("cache set failed");
            }

            _values[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            SetCalls++;
            if (_throwOnSet)
            {
                throw new InvalidOperationException("cache set failed");
            }

            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
