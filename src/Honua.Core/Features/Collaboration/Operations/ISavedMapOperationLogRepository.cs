// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// Persistence abstraction for saved-map collaborative edit operation logs.
/// </summary>
public interface ISavedMapOperationLogRepository
{
    /// <summary>
    /// Whether replay observes operations accepted by every application replica.
    /// </summary>
    bool SupportsReplicaSharedReplay { get; }

    /// <summary>
    /// Whether accepted operations and their server-assigned cursors survive a process restart.
    /// </summary>
    bool SupportsRestartDurableReplay { get; }

    /// <summary>
    /// Whether the last successfully checkpointed cursor survives a process restart in the same
    /// persistence implementation as the operation log.
    /// </summary>
    bool SupportsRestartDurableCheckpointCursors { get; }

    /// <summary>
    /// Whether a checkpoint can prove both operation replay and its starting cursor survive a
    /// process restart. Checkpoint consumers must gate on this aggregate rather than operation-log
    /// durability alone.
    /// </summary>
    bool SupportsRestartDurableCheckpointing =>
        SupportsRestartDurableReplay && SupportsRestartDurableCheckpointCursors;

    /// <summary>
    /// Appends a saved-map edit operation if it is idempotent, in-window, and merge-safe.
    /// </summary>
    Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays saved-map edit operations accepted after the supplied cursor.
    /// </summary>
    Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays operations after the repository-owned last-checkpointed cursor for a saved map.
    /// </summary>
    Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Monotonically records that a durable checkpoint includes every operation through the
    /// supplied cursor.
    /// </summary>
    Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Conflict policy seam for saved-map collaborative edit appends.
/// </summary>
public interface ISavedMapOperationConflictPolicy
{
    /// <summary>
    /// Returns a conflict response when the proposed operation cannot be merged safely.
    /// </summary>
    SavedMapOperationConflictResponse? DetectConflict(
        SavedMapOperationAppendRequest request,
        IReadOnlyList<SavedMapOperationEnvelope> concurrentOperations,
        SavedMapOperationCursor headCursor);
}
