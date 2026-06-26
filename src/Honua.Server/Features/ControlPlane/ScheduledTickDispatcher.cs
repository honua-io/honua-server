// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Routes a <see cref="ScheduledTickKind"/> to the <see cref="IScheduledTickHandler"/> that owns it
/// and runs that handler's idempotent per-tick body exactly once.
/// <para>
/// This is the PERIODIC (bucket-b) sibling of <see cref="OperationReconcileDispatcher"/>: it adds no
/// behavior of its own. Handlers are contributed by the assemblies that own the underlying background
/// services and collected here as <see cref="IEnumerable{T}"/>, so the dispatcher stays a thin map and
/// the tick services keep their internal visibility. Under <c>TriggerMode=Poll</c> the in-process
/// timers call the tick bodies directly; under <c>Event</c> EventBridge Scheduler calls
/// <see cref="RunTickAsync"/> through the scheduled-tick endpoint. The tick bodies' own idempotency
/// (claim + cursor, optimistic CAS, expiry-state) keeps invocation safe in both.
/// </para>
/// </summary>
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
