// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.DuckDB.Features.FeatureStore;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that the read-only replica repository no-ops writes and returns empty reads.
/// Extract capability is stripped from DuckDB services at startup, so createReplica
/// should not normally reach this repository; the no-op upsert is a defensive
/// guarantee that the caching write-through path does not throw if it does.
/// </summary>
public class ReadOnlyReplicaRepositoryTests
{
    private readonly ReadOnlyReplicaRepository _repo = new();

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
