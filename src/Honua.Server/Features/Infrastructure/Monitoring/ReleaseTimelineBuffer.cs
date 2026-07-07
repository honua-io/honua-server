// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Bounded, in-memory ring buffer of recent <see cref="OperateEventKind.Release"/> events produced by
/// deploy workflow transitions. It is the <see cref="LocalOperateEventFeed"/> source for release events,
/// mirroring the in-process <c>RecentErrorBuffer</c> that backs the same Operate timeline for logs.
/// </summary>
/// <remarks>
/// This buffer is per-instance and non-durable by design: the durable source of truth for deploy
/// operations is the Redis workflow store (queryable via the deploy-operations list API), and cross-replica
/// realtime fan-out is the realtime hub's concern (#2554). The timeline surfaces the most recent release
/// activity observed by this instance so the Operate cockpit's <c>ReleaseId</c> filter returns data.
/// </remarks>
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

    /// <summary>
    /// Appends a release event, evicting the oldest entry when the buffer is full.
    /// </summary>
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

    /// <summary>
    /// Returns a newest-first snapshot of the buffered release events.
    /// </summary>
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
