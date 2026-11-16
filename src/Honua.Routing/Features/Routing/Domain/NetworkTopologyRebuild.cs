// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public enum NetworkTopologyRebuildStage
{
    Snapshot,

    Build,

    Analyze,

    Validate,

    Cleanup,
}

public enum NetworkTopologyRebuildCheckpointStatus
{
    Pending,

    InProgress,

    Completed,

    Failed,
}

public enum NetworkTopologyRebuildAttemptState
{
    Building,

    Ready,

    Failed,
}

public sealed record NetworkTopologyRebuildAttempt(
    string DatasetId,
    long Generation,
    long Attempt,
    NetworkTopologyRebuildAttemptState State,
    string OperationId,
    long ExpectedSourceRevision,
    long ExpectedRowVersion,
    string? ShadowEdgeTable,
    string? ShadowVertexTable,
    string? EvidenceDigest,
    string? FailureCode,
    string? OwnerId,
    long FencingToken,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record NetworkTopologyRebuildCheckpoint(
    string DatasetId,
    long Generation,
    long Attempt,
    NetworkTopologyRebuildStage Stage,
    NetworkTopologyRebuildCheckpointStatus Status,
    string? Detail,
    DateTimeOffset UpdatedAt);

public enum NetworkTopologyRebuildRejection
{
    GenerationNotFound,

    GenerationNotDirty,

    StaleRowVersion,

    StaleSourceRevision,

    AttemptAlreadyActive,

    AttemptNotFound,

    StaleFencingToken,

    LeaseHeldByAnotherOwner,
}

public sealed class NetworkTopologyRebuildConflictException : Exception
{
    public NetworkTopologyRebuildConflictException(NetworkTopologyRebuildRejection reason, string message)
        : base(message)
        => Reason = reason;

    public NetworkTopologyRebuildRejection Reason { get; }
}
