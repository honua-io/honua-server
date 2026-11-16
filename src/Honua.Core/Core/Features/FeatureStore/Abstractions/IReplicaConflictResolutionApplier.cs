// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IReplicaConflictResolutionApplier
{
    Task<ReplicaConflictApplyResult> ApplyAsync(
        ReplicaConflictResolutionCommand command,
        CancellationToken cancellationToken = default);

    Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken = default);

    Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
        string serviceId,
        int publicLayerId,
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken = default)
        => CaptureStateTokenAsync(storageLayerId, objectId, cancellationToken);
}

public readonly record struct ReplicaConflictRowSnapshot(
    bool Exists,
    string? StateToken,
    string? StateJson);
