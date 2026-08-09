// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Checkpoint-facing facade over the saved-map operation log (honua-server#2999). Pairs the
/// replay the checkpoint needs with the replica-continuity proof that must gate it, and records
/// the last successfully checkpointed cursor in the same repository so later checkpoints replay
/// only the not-yet-applied suffix.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory implementation keeps its cursor beside its process-local log and reports that
/// restart-durable checkpointing is unavailable. The Postgres implementation persists both in the
/// same store and unlocks the checkpoint endpoint only when the aggregate durability signal proves
/// that neither side is lost across a restart (honua-server#3067).
/// </para>
/// </remarks>
internal sealed class SavedMapCheckpointOperationLog
{
    private readonly ISavedMapOperationLogRepository _repository;
    private readonly SavedMapCollaborationTopology _topology;

    public SavedMapCheckpointOperationLog(
        ISavedMapOperationLogRepository repository,
        SavedMapCollaborationTopology topology)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    }

    /// <summary>
    /// Whether a replay from this node provably observes every accepted edit. False only when
    /// the deployment is declared multi-replica but the op log is process-local, because an
    /// append and its checkpoint replay can then land on different nodes. A single instance is
    /// always authoritative over its own log — including the very common single-instance
    /// deployment that uses Redis for cache/jobs.
    /// </summary>
    public bool CanProveReplayContinuity =>
        !_topology.IsMultiReplica || _repository.SupportsReplicaSharedReplay;

    /// <summary>
    /// Whether a replay from this node provably observes every accepted edit ACROSS a process
    /// restart. False whenever either the operation replay or its checkpoint cursor is not
    /// restart-durable: losing either side can omit accepted edits or replay an already-versioned
    /// prefix after restart (honua-server#2999 review, honua-server#3067).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT satisfiable by configuration: no operator declaration can make a
    /// process-local log survive a restart. It is unlocked by registering a restart-durable
    /// <see cref="ISavedMapOperationLogRepository"/> implementation.
    /// </remarks>
    public bool CanProveRestartContinuity => _repository.SupportsRestartDurableCheckpointing;

    /// <summary>
    /// Replays every operation accepted after the last successfully checkpointed cursor for
    /// this map (cursor 0 when no checkpoint has been recorded). The start is fully
    /// server-derived — client-supplied cursors are deliberately not accepted — and a
    /// <see cref="SavedMapOperationReplayStatus.ResyncRequired"/> result means the recorded
    /// cursor itself has fallen out of the retained window, i.e. operations that were never
    /// checkpointed have been pruned and completeness cannot be proven.
    /// </summary>
    public Task<SavedMapOperationReplayResult> ReplayPendingAsync(
        string canonicalMapId,
        CancellationToken cancellationToken)
        => _repository.ReplayPendingCheckpointAsync(
            new SavedMapId(canonicalMapId),
            cancellationToken);

    /// <summary>
    /// Records that a checkpoint successfully persisted every operation up to and including
    /// <paramref name="headCursor"/>. Monotonic: concurrent checkpoints keep the highest cursor.
    /// </summary>
    /// <remarks>
    /// The repository owns monotonicity and cursor durability alongside the operation log.
    /// </remarks>
    public Task MarkCheckpointedAsync(
        string canonicalMapId,
        long headCursor,
        CancellationToken cancellationToken) =>
        _repository.RecordCheckpointAsync(
            new SavedMapId(canonicalMapId),
            new SavedMapOperationCursor(headCursor),
            cancellationToken);
}
