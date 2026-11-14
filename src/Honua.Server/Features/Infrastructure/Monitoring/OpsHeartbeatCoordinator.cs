// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed class OpsHeartbeatCoordinator(IExecutionJobStore jobStore, IOpsHeartbeatPublisher publisher)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public async Task PublishAsync(string stackName, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        if (!await jobStore.TryAcquireLeaseAsync($"ops-heartbeat:{stackName}", _owner,
            TimeSpan.FromSeconds(60), deadline.Token).ConfigureAwait(false))
        {
            return;
        }

        deadline.Token.ThrowIfCancellationRequested();
        // Retain the lease through its window even when publication fails.
        await publisher.PublishAsync(stackName, deadline.Token).ConfigureAwait(false);
    }
}
