// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// The in-repo consumer of the workflow-transition seam: maps each observed deploy/release transition to
/// an <see cref="OperateEventKind.Release"/> event and appends it to the <see cref="ReleaseTimelineBuffer"/>
/// so it appears on the unified Operate timeline (#2562). The realtime hub (#2554) attaches to the SAME
/// seam as a second, independent listener.
/// </summary>
internal sealed class ReleaseTimelineTransitionListener : IWorkflowOperationTransitionListener
{
    private readonly ReleaseTimelineBuffer _buffer;

    public ReleaseTimelineTransitionListener(ReleaseTimelineBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public Task OnTransitionAsync(WorkflowOperationTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        _buffer.Append(ReleaseTimelineEventFactory.Create(transition));
        return Task.CompletedTask;
    }
}
