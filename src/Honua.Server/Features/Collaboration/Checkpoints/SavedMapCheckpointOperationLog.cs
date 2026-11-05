// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Checkpoints;

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

    // A replay is not authoritative when the deployment is multi-replica and the log is process-local; a single instance is authoritative even when Redis is present.
    public bool CanProveReplayContinuity =>
        !_topology.IsMultiReplica || _repository.SupportsReplicaSharedReplay;

    // Restart durability is false unless both the log and the checkpoint cursor are durable, and no operator setting can make a process-local log survive a restart.
    public bool CanProveRestartContinuity => _repository.SupportsRestartDurableCheckpointing;

    // The replay start is server-derived, and a cursor that fell out of the window means pruned operations and unproven completeness.
    public Task<SavedMapOperationReplayResult> ReplayPendingAsync(
        string canonicalMapId,
        CancellationToken cancellationToken)
        => _repository.ReplayPendingCheckpointAsync(
            new SavedMapId(canonicalMapId),
            cancellationToken);

    public Task MarkCheckpointedAsync(
        string canonicalMapId,
        long headCursor,
        CancellationToken cancellationToken) =>
        _repository.RecordCheckpointAsync(
            new SavedMapId(canonicalMapId),
            new SavedMapOperationCursor(headCursor),
            cancellationToken);
}
