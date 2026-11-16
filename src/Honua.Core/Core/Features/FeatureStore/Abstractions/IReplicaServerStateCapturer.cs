// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IReplicaServerStateCapturer
{
    Task<IReadOnlyDictionary<(int PublicLayerId, long ObjectId), ReplicaConflictServerStateCapture>> CaptureAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<long, ReplicaConflictStateTokenCapture>> CaptureTokensAsync(
        ImmutableArray<ReplicaConflictCaptureTarget> targets,
        CancellationToken cancellationToken = default);
}

public readonly record struct ReplicaConflictStateTokenCapture(string StateToken, long StorageObjectId);

public readonly record struct ReplicaConflictServerStateCapture(string StateJson, string StateToken);

public readonly record struct ReplicaConflictCaptureTarget(
    int PublicLayerId,
    int StorageLayerId,
    long ObjectId);
