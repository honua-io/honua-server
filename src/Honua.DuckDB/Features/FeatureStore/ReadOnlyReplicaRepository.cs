// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.DuckDB.Features.FeatureStore;

/// <summary>
/// Replica repository that rejects all operations.
/// Registered when the DuckDB provider is active since DuckDB is read-only in V1.
/// </summary>
internal sealed class ReadOnlyReplicaRepository : IReplicaRepository
{
    /// <inheritdoc />
    public Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider is read-only. Replica registration is not supported.");
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
