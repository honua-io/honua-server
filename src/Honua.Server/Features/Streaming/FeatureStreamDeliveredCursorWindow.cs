// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

// Live sends never advance the floor, a frame above the floor is still delivered, and a forgotten cursor may be sent again rather than dropped.
internal sealed class FeatureStreamDeliveredCursorWindow
{
    internal const int DefaultCapacity = 4096;

    private readonly Lock _gate = new();
    private readonly SortedSet<long> _delivered = [];
    private readonly int _capacity;
    private long _floor;

    public FeatureStreamDeliveredCursorWindow(long floor, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _floor = floor;
        _capacity = capacity;
    }

    public long Floor
    {
        get
        {
            lock (_gate)
            {
                return _floor;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _delivered.Count;
            }
        }
    }

    // A non-positive cursor identifies a frame without a durable cursor, so it is not treated as already handled.
    public bool IsHandled(long cursor)
    {
        if (cursor <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            return (_floor > 0 && cursor <= _floor) || _delivered.Contains(cursor);
        }
    }

    public bool WasDeliveredLive(long cursor)
    {
        lock (_gate)
        {
            return _delivered.Contains(cursor);
        }
    }

    public void MarkDelivered(long cursor)
    {
        if (cursor <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_floor > 0 && cursor <= _floor)
            {
                return;
            }

            _delivered.Add(cursor);
            while (_delivered.Count > _capacity)
            {
                _delivered.Remove(_delivered.Min);
            }
        }
    }

    // The floor never moves backwards.
    public void AdvanceFloor(long sweptThrough)
    {
        lock (_gate)
        {
            if (sweptThrough <= _floor)
            {
                return;
            }

            _floor = sweptThrough;
            while (_delivered.Count > 0 && _delivered.Min <= sweptThrough)
            {
                _delivered.Remove(_delivered.Min);
            }
        }
    }
}
