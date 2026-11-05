// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

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
