// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// Replica repository for the read-only MySQL/MariaDB provider. Persistence is a no-op
/// because the underlying database does not support replica write workflows; the Extract
/// capability is also stripped from MySQL services at startup so the createReplica
/// endpoint should not normally be reached. Mirrors the DuckDB read-only stub so DI
/// activation succeeds when the provider is configured.
/// </summary>
internal sealed class ReadOnlyMySqlReplicaRepository : IReplicaRepository
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
    public Task<IReadOnlyList<ReplicaRecord>> ListAllAsync(
        string? serviceId,
        string? status,
        int limit,
        string? afterReplicaId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaRecord>>([]);

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
