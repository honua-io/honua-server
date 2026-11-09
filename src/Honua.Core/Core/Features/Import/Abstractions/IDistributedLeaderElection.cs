// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

public interface IDistributedLeaderElection
{
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    // The lease must be extended periodically or leadership is lost.
    Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default);

    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    bool IsLeader { get; }

    string InstanceId { get; }
}
