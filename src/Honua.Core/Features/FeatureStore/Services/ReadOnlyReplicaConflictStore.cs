// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// No-op replica conflict store for read-only feature providers that cannot persist
/// disconnected-sync conflict state.
/// </summary>
public sealed class ReadOnlyReplicaConflictStore : IReplicaConflictStore
{
    /// <inheritdoc />
    public Task AppendAsync(ReplicaConflict conflict, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<ReplicaConflict>> ListByReplicaAsync(
        string replicaId,
        bool pendingOnly,
        int limit,
        Guid? afterConflictId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaConflict>>(Array.Empty<ReplicaConflict>());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> CountPendingByReplicaAsync(
        IReadOnlyCollection<string> replicaIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.Ordinal));

    /// <inheritdoc />
    public Task<ReplicaConflict?> GetAsync(Guid conflictId, CancellationToken cancellationToken = default)
        => Task.FromResult<ReplicaConflict?>(null);

    /// <inheritdoc />
    public Task<IReplicaConflictResolutionClaim?> TryClaimResolutionAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReplicaConflictResolutionClaim?>(null);

    /// <inheritdoc />
    public Task<bool> ResolveAsync(
        Guid conflictId,
        ReplicaConflictResolution resolution,
        string resolvedBy,
        string? resolutionPayloadJson,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
