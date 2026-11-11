// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Sessions;

// Advertised flags are recomputed on every read so they follow the operation log's current guarantees.
internal sealed class CollaborationCapabilitySource
{
    private readonly SavedMapCollaborationTopology _topology;
    private readonly ISavedMapOperationLogRepository _log;

    public CollaborationCapabilitySource(
        SavedMapCollaborationTopology topology,
        ISavedMapOperationLogRepository log)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public CollaborationCapabilities Current
    {
        get
        {
            // Live operations and presence reach only participants connected to this node, so a
            // multi-replica deployment advertises neither. Replay and checkpoints read the
            // operation log, so they depend on whether that log is shared and restart-durable.
            var liveDelivery = !_topology.IsMultiReplica;
            var sharedLog = liveDelivery || _log.SupportsReplicaSharedReplay;
            return CollaborationCapabilities.Default with
            {
                Operations = liveDelivery,
                Replay = sharedLog,
                Checkpoints = sharedLog && _log.SupportsRestartDurableCheckpointing,
                Cursors = liveDelivery,
                Selections = liveDelivery,
                Follow = liveDelivery,
            };
        }
    }
}
