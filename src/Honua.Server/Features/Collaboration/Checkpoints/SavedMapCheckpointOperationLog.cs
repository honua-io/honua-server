// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Checkpoint-facing facade over the saved-map operation log (honua-server#2999). Pairs the
/// replay the checkpoint needs with the replica-continuity proof that must gate it, and records
/// the last successfully checkpointed cursor per canonical map id so later checkpoints replay
/// only the not-yet-applied suffix. Without that record a checkpoint would always replay from
/// cursor 0 and a long-lived session would become permanently uncheckpointable once its early
/// operations (already persisted by earlier checkpoints) age out of the retained window.
/// </summary>
/// <remarks>
/// The cursor record is process-local by design: it shares the lifetime and scope of the op log
/// it indexes into, so the pair stays consistent (both reset together). Because both reset, a
/// process-local log cannot prove continuity across a restart — checkpointing is gated on
/// <see cref="CanProveRestartContinuity"/> (and, in a declared multi-replica deployment, on
/// <see cref="CanProveReplayContinuity"/>) before either is consulted. A restart-durable or
/// replica-shared log implementation must persist this cursor alongside the log itself.
/// </remarks>
internal sealed class SavedMapCheckpointOperationLog
{
    private readonly ISavedMapOperationLogRepository _repository;
    private readonly SavedMapCollaborationTopology _topology;
    private readonly ConcurrentDictionary<string, long> _checkpointedCursors = new(StringComparer.Ordinal);

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
    /// restart. False whenever the op log is not restart-durable: an operation acknowledged
    /// before a restart is then gone, and a post-restart replay is indistinguishable from a
    /// session that never appended anything — so a checkpoint would mint an immutable version
    /// that silently omits an accepted edit (honua-server#2999 review).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT satisfiable by configuration: no operator declaration can make a
    /// process-local log survive a restart. It is unlocked by registering a restart-durable
    /// <see cref="ISavedMapOperationLogRepository"/> implementation.
    /// </remarks>
    public bool CanProveRestartContinuity => _repository.SupportsRestartDurableReplay;

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
    {
        var sinceCursor = _checkpointedCursors.TryGetValue(canonicalMapId, out var recorded) ? recorded : 0L;
        return _repository.ReplayAsync(
            new SavedMapId(canonicalMapId),
            new SavedMapOperationCursor(sinceCursor),
            cancellationToken);
    }

    /// <summary>
    /// Records that a checkpoint successfully persisted every operation up to and including
    /// <paramref name="headCursor"/>. Monotonic: concurrent checkpoints keep the highest cursor.
    /// </summary>
    public void MarkCheckpointed(string canonicalMapId, long headCursor) =>
        _checkpointedCursors.AddOrUpdate(
            canonicalMapId,
            headCursor,
            (_, existing) => Math.Max(existing, headCursor));
}
