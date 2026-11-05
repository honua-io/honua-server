// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin;

// This listener enqueues on the shared transition seam and does not build its own producer, so a hub fault cannot fail the store write.
internal sealed class RealtimeOperationTransitionListener(AdminRealtimeBroadcaster broadcaster)
    : IWorkflowOperationTransitionListener
{
    private readonly AdminRealtimeBroadcaster _broadcaster =
        broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));

    public Task OnTransitionAsync(WorkflowOperationTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        _broadcaster.EnqueueTransition(transition);
        return Task.CompletedTask;
    }
}
