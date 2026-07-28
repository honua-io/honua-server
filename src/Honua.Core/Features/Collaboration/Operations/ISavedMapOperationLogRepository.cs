// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// Persistence abstraction for saved-map collaborative edit operation logs.
/// </summary>
public interface ISavedMapOperationLogRepository
{
    /// <summary>
    /// Whether the log is shared across replicas and process restarts, so a replay observes
    /// every operation accepted by any node. When <see langword="false"/> (the in-process
    /// implementation), consumers that persist durable state derived from a replay — such as
    /// saved-map checkpoints — must not trust a replay for continuity in multi-node
    /// deployments, because an append and its replay can land on different nodes.
    /// </summary>
    bool SupportsReplicaSharedReplay { get; }

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
