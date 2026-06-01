// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Conflict repository for read-only feature providers (DuckDB, MySQL/MariaDB). These providers do
/// not support disconnected-sync write workflows, so durable conflict review is unsupported:
/// <see cref="SupportsConflictReview"/> is <c>false</c> and the conflict-review endpoints return a
/// not-supported denial rather than an empty result. Writes are no-ops so any code path that reaches
/// the repository on a read-only deployment does not throw.
/// </summary>
public sealed class NoOpReplicaConflictRepository : IReplicaConflictRepository
{
    /// <inheritdoc />
    public bool SupportsConflictReview => false;

    /// <inheritdoc />
    public Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
        string replicaId,
        ReplicaConflictStatus? status = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaConflictRecord>>([]);

    /// <inheritdoc />
    public Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
        => Task.FromResult<ReplicaConflictRecord?>(null);

    /// <inheritdoc />
    public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ReplicaConflictResolutionOutcome(null, Applied: false));
}
