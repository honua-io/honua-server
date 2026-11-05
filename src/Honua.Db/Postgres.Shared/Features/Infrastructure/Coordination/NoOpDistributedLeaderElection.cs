// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure.Coordination;

internal sealed class NoOpDistributedLeaderElection : IDistributedLeaderElection
{
    private readonly string _instanceId;

    public NoOpDistributedLeaderElection(string key)
    {
        // Incorporate the election key into the instance identity so that distinct
        // no-op coordinators (one per key) remain distinguishable in logs/metrics
        // rather than silently collapsing to a single machine/process identity.
        _instanceId = string.IsNullOrEmpty(key)
            ? Environment.MachineName + "_" + Environment.ProcessId
            : key + "_" + Environment.MachineName + "_" + Environment.ProcessId;
    }

    public bool IsLeader => true;

    public string InstanceId => _instanceId;

    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
