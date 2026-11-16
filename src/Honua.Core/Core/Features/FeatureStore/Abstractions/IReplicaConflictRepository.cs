// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public readonly record struct ReplicaConflictResolution(
    string ConflictId,
    ReplicaConflictResolutionAction Action,
    string ResolvedBy,
    DateTimeOffset ResolvedAt,
    long? ResolvedServerGeneration,
    string? ResolutionInputHash = null);

public readonly record struct ReplicaConflictResolutionOutcome(
    ReplicaConflictRecord? Record,
    bool Applied);

public readonly record struct ReplicaConflictDetectionUpdate(
    string ConflictId,
    ReplicaConflictType? ConflictType,
    string? ClientStateJson,
    string? ServerStateJson,
    bool? ClientEditApplied,
    long? ResolutionBaseGeneration = null,
    bool? ClientEditOutcomeUnknown = null,
    bool? ClientEditSuperseded = null,
    bool ClearServerState = false);

public readonly record struct ReplicaConflictFinalizationUpdate(
    string ConflictId,
    string ResolvedBy,
    ReplicaConflictResolutionAction Action,
    DateTimeOffset ResolvedAt,
    bool? WriteCommitted,
    long? ResolvedServerGeneration,
    bool? Finalized,
    string? PreWriteStateToken = null,
    bool? PreWriteRowAbsent = null);

public interface IReplicaConflictRepository
{
    bool SupportsConflictReview { get; }

    Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
        string replicaId,
        ReplicaConflictStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default);

    Task<bool> TryUpdateDetectionStateAsync(
        ReplicaConflictDetectionUpdate update,
        CancellationToken cancellationToken = default);

    Task<bool> TryUpdateFinalizationStateAsync(
        ReplicaConflictFinalizationUpdate update,
        CancellationToken cancellationToken = default);

    Task<bool> TryTakeOverClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset expectedResolvedAt,
        DateTimeOffset newResolvedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReleaseClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

    Task<ReplicaConflictResolutionOutcome> ResolveAsync(
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken = default);
}
