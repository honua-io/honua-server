// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Collaboration.Sessions;

internal sealed class CollaborationCapabilitySource(
    SavedMapCollaborationTopology topology,
    ISavedMapOperationLogRepository log,
    IRealtimeFanoutProvider? provider = null)
{
    public CollaborationCapabilities Current
    {
        get
        {
            var sharedLog = !topology.IsMultiReplica || log.SupportsReplicaSharedReplay;
            var ordered = !topology.IsMultiReplica ||
                provider?.Capabilities is { CrossNode: true, OrderedPerFamily: true, Resumable: true };
            var presence = !topology.IsMultiReplica ||
                provider?.Capabilities is { CrossNode: true, Resumable: true };
            return CollaborationCapabilities.Default with
            {
                Operations = sharedLog && ordered,
                Replay = sharedLog,
                Checkpoints = sharedLog && log.SupportsRestartDurableCheckpointing,
                Cursors = presence,
                Selections = presence,
                Follow = presence
            };
        }
    }
}
