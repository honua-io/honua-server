// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.DuckDB.Features.FeatureStore;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Verifies that the read-only replica repository rejects writes and returns empty reads.
/// </summary>
public class ReadOnlyReplicaRepositoryTests
{
    private readonly ReadOnlyReplicaRepository _repo = new();

    [Fact]
    public async Task UpsertAsync_ThrowsNotSupported()
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
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _repo.UpsertAsync(record));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull()
    {
        var result = await _repo.GetAsync("test-id");
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsFalse()
    {
        var result = await _repo.RemoveAsync("test-id");
        Assert.False(result);
    }
}
