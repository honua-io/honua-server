// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class ReleaseTimelineBuffer
{
    internal const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<OperateEvent> _events;
    private readonly int _capacity;

    public ReleaseTimelineBuffer()
        : this(DefaultCapacity)
    {
    }

    public ReleaseTimelineBuffer(int capacity)
    {
        _capacity = capacity < 1 ? DefaultCapacity : capacity;
        _events = new Queue<OperateEvent>(_capacity);
    }

    public void Append(OperateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            if (_events.Count >= _capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(value);
        }
    }

    public IReadOnlyList<OperateEvent> Snapshot()
    {
        lock (_gate)
        {
            if (_events.Count == 0)
            {
                return Array.Empty<OperateEvent>();
            }

            var snapshot = _events.ToArray();
            Array.Reverse(snapshot);
            return snapshot;
        }
    }
}
