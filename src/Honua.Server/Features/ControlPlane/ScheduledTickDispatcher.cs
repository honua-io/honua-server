// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

internal sealed class ScheduledTickDispatcher : IScheduledTickDispatcher
{
    private readonly Dictionary<ScheduledTickKind, IScheduledTickHandler> _handlers;

    public ScheduledTickDispatcher(IEnumerable<IScheduledTickHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<ScheduledTickKind, IScheduledTickHandler>();
        foreach (var handler in handlers)
        {
            // Last registration wins per kind; a duplicate kind is a registration mistake but must
            // not throw at composition time and crash an otherwise healthy host.
            map[handler.Kind] = handler;
        }

        _handlers = map;
    }

    public Task RunTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(kind, out var handler))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "No scheduled-tick handler is registered for this kind. The owning feature may be "
                + "disabled (for example its store/Redis dependency is absent) in this deployment.");
        }

        return handler.RunTickAsync(cancellationToken);
    }
}
