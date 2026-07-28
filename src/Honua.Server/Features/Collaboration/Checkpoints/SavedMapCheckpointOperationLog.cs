// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Checkpoint-facing facade over the saved-map operation log (honua-server#2999). Pairs the
/// replay the checkpoint needs (always the full retained log from the earliest provable cursor)
/// with the replica-continuity proof that must gate it, so the endpoint consumes one cohesive
/// seam instead of injecting the repository and the backplane separately.
/// </summary>
internal sealed class SavedMapCheckpointOperationLog
{
    private readonly ISavedMapOperationLogRepository _repository;
    private readonly ICollaborationSessionBackplane _backplane;

    public SavedMapCheckpointOperationLog(
        ISavedMapOperationLogRepository repository,
        ICollaborationSessionBackplane backplane)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _backplane = backplane ?? throw new ArgumentNullException(nameof(backplane));
    }

    /// <summary>
    /// Whether a replay from this node provably observes every accepted edit. False when the
    /// deployment is distributed (the session backplane spans replicas) but the op log is
    /// process-local, because an append and its checkpoint replay can land on different nodes.
    /// </summary>
    public bool CanProveReplayContinuity =>
        !_backplane.IsDistributed || _repository.SupportsReplicaSharedReplay;

    /// <summary>
    /// Replays the full retained log from the earliest provable cursor (0). The start is
    /// deliberately server-derived — see the checkpoint endpoint for the trust rationale.
    /// </summary>
    public Task<SavedMapOperationReplayResult> ReplayAllAsync(
        string canonicalMapId,
        CancellationToken cancellationToken) =>
        _repository.ReplayAsync(
            new SavedMapId(canonicalMapId),
            new SavedMapOperationCursor(0),
            cancellationToken);
}
