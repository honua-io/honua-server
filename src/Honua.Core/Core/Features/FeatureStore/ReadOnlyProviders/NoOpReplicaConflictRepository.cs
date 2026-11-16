// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class NoOpReplicaConflictRepository : IReplicaConflictRepository
{
    public bool SupportsConflictReview => false;

    public Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
        string replicaId,
        ReplicaConflictStatus? status = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaConflictRecord>>([]);

    public Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
        => Task.FromResult<ReplicaConflictRecord?>(null);

    public Task<bool> TryUpdateDetectionStateAsync(
        ReplicaConflictDetectionUpdate update,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> TryUpdateFinalizationStateAsync(
        ReplicaConflictFinalizationUpdate update,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> TryTakeOverClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset expectedResolvedAt,
        DateTimeOffset newResolvedAt,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> TryReleaseClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ReplicaConflictResolutionOutcome(null, Applied: false));
}
