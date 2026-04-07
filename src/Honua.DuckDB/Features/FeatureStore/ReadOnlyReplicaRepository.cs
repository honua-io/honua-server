// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.DuckDB.Features.FeatureStore;

/// <summary>
/// Replica repository for the read-only DuckDB provider. Persistence is a no-op
/// because the underlying database does not support write workflows; the Extract
/// capability is also stripped from DuckDB services at startup so the createReplica
/// endpoint should not normally be reached. The no-op upsert prevents the
/// caching write-through path from throwing if the endpoint is invoked anyway —
/// any replica state then lives only in the distributed replica cache for the
/// configured TTL, which is acceptable for V1 read-only analytics workloads.
/// </summary>
internal sealed class ReadOnlyReplicaRepository : IReplicaRepository
{
    /// <inheritdoc />
    public Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ReplicaRecord?>(null);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
