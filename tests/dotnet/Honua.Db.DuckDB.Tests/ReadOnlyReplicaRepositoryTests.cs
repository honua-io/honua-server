// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;

namespace Honua.Db.DuckDB.Tests;

public sealed class ReadOnlyReplicaRepositoryTests
{
    private readonly NoOpReplicaRepository _repo = new();

    [Fact]
    public void SupportsReplicaPersistence_IsFalse()
    {
        // Read-only providers cannot durably persist replicas, so the synchronize adapter emits a
        // conformant "operation not supported" response instead of silently no-op'ing a sync (#2136).
        Assert.False(_repo.SupportsReplicaPersistence);
    }

    [Fact]
    public async Task UpsertAsync_NoOps()
    {
        var record = new ReplicaRecord
        {
            ReplicaId = "test-id",
            ReplicaName = "test",
            ServiceId = "svc",
            SyncModel = "none",
            LayerIds = [0],
            CreatedAt = DateTimeOffset.UtcNow,
            LastSyncTime = DateTimeOffset.UtcNow,
            LastSyncGeneration = 0L
        };

        // Should not throw — the read-only provider drops the write silently.
        await _repo.UpsertAsync(record);

        // Reads still return null because nothing is persisted on the DuckDB side.
        var roundTrip = await _repo.GetAsync(record.ReplicaId);
        Assert.Null(roundTrip);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull()
    {
        var result = await _repo.GetAsync("test-id");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListByServiceAsync_ReturnsEmpty()
    {
        var results = await _repo.ListByServiceAsync("svc");
        Assert.Empty(results);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsFalse()
    {
        var result = await _repo.RemoveAsync("test-id");
        Assert.False(result);
    }
}
