// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

public interface ISavedMapOperationLogRepository
{
    // An in-process log is not shared across replicas, so a replay must not be trusted for durable continuity in a multi-node deployment.
    bool SupportsReplicaSharedReplay { get; }

    // When accepted operations do not survive restart, a later empty replay cannot be treated as proof that nothing was appended.
    bool SupportsRestartDurableReplay { get; }

    bool SupportsRestartDurableCheckpointCursors { get; }

    // A checkpoint consumer must require both the operation replay and the starting cursor to survive restart.
    bool SupportsRestartDurableCheckpointing =>
        SupportsRestartDurableReplay && SupportsRestartDurableCheckpointCursors;

    Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default);

    Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default);

    Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default);

    Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default);
}

public interface ISavedMapOperationConflictPolicy
{
    SavedMapOperationConflictResponse? DetectConflict(
        SavedMapOperationAppendRequest request,
        IReadOnlyList<SavedMapOperationEnvelope> concurrentOperations,
        SavedMapOperationCursor headCursor);
}
