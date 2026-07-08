// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Second, independent consumer of the #2562 workflow-transition seam. Forwards every observed transition to
/// the <see cref="AdminRealtimeBroadcaster"/>, which pushes it to the <c>deploy-operations</c> and
/// <c>operate-events</c> hub groups over the Redis backplane (#2554). It does NOT build a producer of its own
/// — it rides the same seam the operate-timeline listener uses, so a transition fans to both consumers.
/// </summary>
/// <remarks>
/// The forward is a non-blocking enqueue and is exception-isolated inside the broadcaster, so this listener
/// never slows or faults the authoritative store-write path (matching the seam's listener-isolation contract).
/// </remarks>
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
