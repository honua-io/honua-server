// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface INetworkTopologyRebuildStore
{
    Task<NetworkTopologyRebuildAttempt> CreateAttemptAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        long expectedSourceRevision,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyRebuildAttempt?> GetAttemptAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyRebuildAttempt?> GetLatestAttemptAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkTopologyRebuildAttempt>> ListExpiredLeasesAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    Task<NetworkTopologyRebuildAttempt?> TryAcquireOrTakeoverLeaseAsync(
        string datasetId,
        long generation,
        long attempt,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> TryHeartbeatAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> TryWriteCheckpointAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        NetworkTopologyRebuildStage stage,
        NetworkTopologyRebuildCheckpointStatus status,
        string? detail,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkTopologyRebuildCheckpoint>> ListCheckpointsAsync(
        string datasetId,
        long generation,
        long attempt,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string shadowEdgeTable,
        string shadowVertexTable,
        string evidenceDigest,
        CancellationToken cancellationToken = default);

    Task<bool> TryFailAsync(
        string datasetId,
        long generation,
        long attempt,
        long fencingToken,
        string failureCode,
        CancellationToken cancellationToken = default);

    Task<bool> TryAbandonUnclaimedAttemptAsync(
        string datasetId,
        long generation,
        long attempt,
        string failureCode,
        CancellationToken cancellationToken = default);

    Task CleanupOrphanShadowArtifactsAsync(
        string datasetId,
        long generation,
        long? keepAttempt,
        CancellationToken cancellationToken = default);
}
