// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.Migration;

// A single-node host with no lease store leads its own imports.
internal sealed class NodeLocalLeaderElection : IDistributedLeaderElection
{
    public bool IsLeader => true;

    public string InstanceId => LeaseLeaderElection.NodeId;

    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

// A host that must coordinate through a lease store but has none never leads.
internal sealed class UnelectedLeaderElection : IDistributedLeaderElection
{
    public bool IsLeader => false;

    public string InstanceId => LeaseLeaderElection.NodeId;

    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
