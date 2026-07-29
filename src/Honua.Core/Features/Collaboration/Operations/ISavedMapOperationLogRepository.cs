// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// Persistence abstraction for saved-map collaborative edit operation logs.
/// </summary>
public interface ISavedMapOperationLogRepository
{
    /// <summary>
    /// Whether the log is shared across replicas, so a replay observes every operation accepted
    /// by any node. When <see langword="false"/> (the in-process implementation), consumers that
    /// persist durable state derived from a replay — such as saved-map checkpoints — must not
    /// trust a replay for continuity in multi-node deployments, because an append and its replay
    /// can land on different nodes.
    /// </summary>
    /// <remarks>
    /// This says nothing about surviving a process restart; that is
    /// <see cref="SupportsRestartDurableReplay"/>. The two are deliberately separate signals: a
    /// single-replica deployment is authoritative over its own log yet can still lose accepted
    /// operations when the process restarts.
    /// </remarks>
    bool SupportsReplicaSharedReplay { get; }

    /// <summary>
    /// Whether accepted operations survive a process restart, so a replay after a restart still
    /// observes every operation this server acknowledged. When <see langword="false"/>, an
    /// operation can be accepted (cursor assigned, echoed to live clients) and then vanish with
    /// the process, and a later replay cannot distinguish "nothing was ever appended" from
    /// "appended operations were lost".
    /// </summary>
    /// <remarks>
    /// Consumers that mint durable state from a replay — saved-map checkpoints, which write an
    /// immutable Studio content version — must require this before proceeding, otherwise they
    /// would create a version that silently omits an accepted edit (honua-server#2999 review).
    /// Live fan-out and the op-log surface itself do not require it: those paths are explicitly
    /// resumable and tell a reconnecting client to resync when their cursor is unknown.
    /// </remarks>
    bool SupportsRestartDurableReplay { get; }

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
