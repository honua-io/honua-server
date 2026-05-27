// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Replica repository for read-only feature providers (DuckDB, MySQL/MariaDB). Persistence
/// is a no-op because the underlying databases do not support replica write workflows; the
/// Extract capability is also stripped from read-only services at startup so the
/// createReplica endpoint should not normally be reached. The no-op upsert prevents the
/// caching write-through path from throwing if the endpoint is invoked anyway — any replica
/// state then lives only in the distributed replica cache for the configured TTL, which is
/// acceptable for V1 read-only analytics workloads.
/// </summary>
public sealed class NoOpReplicaRepository : IReplicaRepository
{
    /// <inheritdoc />
    public Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
        => Task.FromResult<ReplicaRecord?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaRecord>>([]);

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
