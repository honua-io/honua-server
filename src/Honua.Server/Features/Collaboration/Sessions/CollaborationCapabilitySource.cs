// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Sessions;

// Advertised flags are recomputed on every read and require the backplane's ordering or retained-presence guarantee, because a value captured at construction is taken before the backplane activates.
internal sealed class CollaborationCapabilitySource
{
    private readonly SavedMapCollaborationTopology _topology;
    private readonly ISavedMapOperationLogRepository _log;
    private readonly ICollaborationSessionBackplane _backplane;

    public CollaborationCapabilitySource(
        SavedMapCollaborationTopology topology,
        ISavedMapOperationLogRepository log,
        ICollaborationSessionBackplane backplane)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backplane = backplane ?? throw new ArgumentNullException(nameof(backplane));
    }

    public CollaborationCapabilities Current
    {
        get
        {
            // Live operation delivery needs BOTH: the shared log lets a peer replica replay an
            // operation, but SavedMapOperationAppendCoordinator.PublishOperation reaches other
            // replicas exclusively through the backplane, so with the no-op backplane a
            // participant on another replica never receives a committed operation even though
            // the log is shared. Replay stays dependent on the log alone, which is the seam it
            // actually uses (honua-server#2999 review).
            var sharedLog = !_topology.IsMultiReplica || _log.SupportsReplicaSharedReplay;
            var crossReplica = !_topology.IsMultiReplica || _backplane.SupportsCrossReplicaDelivery;
            var orderedOperations = !_topology.IsMultiReplica ||
                crossReplica && _backplane.SupportsOrderedOperationDelivery;
            var replicaWidePresence = !_topology.IsMultiReplica ||
                crossReplica && _backplane.SupportsReplicaWidePresence;

            // Presence (cursors/selections/follow) rides the BACKPLANE, not the operation log,
            // so it is advertised only when one actually reaches peer replicas. MultiReplica can
            // be declared without Redis — configuration validation does not require it for that
            // override alone — and the no-op backplane would otherwise promise live presence
            // that participants on other replicas never receive (honua-server#2999 review).
            //
            // Checkpointing additionally needs restart-durable operation replay AND a durable
            // checkpoint cursor: it mints an immutable version and must not claim completeness
            // it cannot prove. It proves that from the LOG, so it keys on the shared-log
            // condition rather than on live delivery.
            return CollaborationCapabilities.Default with
            {
                Operations = sharedLog && orderedOperations,
                Replay = sharedLog,
                Checkpoints = sharedLog && _log.SupportsRestartDurableCheckpointing,
                Cursors = replicaWidePresence,
                Selections = replicaWidePresence,
                Follow = replicaWidePresence,
            };
        }
    }
}
