// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Per-connection delivery fence for the default subscription of a live feature stream
/// (honua-server#6104).
/// </summary>
/// <remarks>
/// <para>
/// The fence has two parts. <see cref="Floor"/> is the durable-store sweep cursor: every
/// cursor at or below it was read by a store sweep and either delivered or filtered, so a
/// live frame at or below it is a duplicate. Live sends never advance the floor. Instead,
/// each live-delivered cursor above the floor is remembered in a bounded set, which both
/// drops true duplicates (the same event re-broadcast locally or echoed over Redis) and
/// lets the next store sweep skip frames the live path already sent.
/// </para>
/// <para>
/// Out-of-order arrival is routine (publisher continuations on different threads, cross-node
/// events over Redis pub/sub), so a live frame whose cursor is below the highest cursor
/// delivered so far but above the floor is still delivered. Because the sweep resumes from
/// the floor rather than from the live high-water mark, an event whose live broadcast never
/// arrives is also recovered from the durable store.
/// </para>
/// <para>
/// Memory is bounded by <see cref="DefaultCapacity"/>. If more live frames than that are
/// delivered between two sweeps, the lowest remembered cursors are forgotten; the next sweep
/// may then re-send them, which keeps the at-least-once contract rather than dropping events.
/// </para>
/// </remarks>
internal sealed class FeatureStreamDeliveredCursorWindow
{
    /// <summary>Maximum number of live-delivered cursors remembered above the floor.</summary>
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

    /// <summary>Cursor through which the durable store has been swept.</summary>
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

    /// <summary>Number of live-delivered cursors currently remembered above the floor.</summary>
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

    /// <summary>
    /// Returns <see langword="true"/> when a live frame at <paramref name="cursor"/> was already
    /// handled: at or below the swept floor, or already delivered live. Non-positive cursors
    /// (frames without a durable cursor) are never treated as handled.
    /// </summary>
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

    /// <summary>Returns <see langword="true"/> when <paramref name="cursor"/> was delivered live above the floor.</summary>
    public bool WasDeliveredLive(long cursor)
    {
        lock (_gate)
        {
            return _delivered.Contains(cursor);
        }
    }

    /// <summary>Records a cursor delivered by the live path.</summary>
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

    /// <summary>
    /// Advances the swept floor after a durable-store sweep and forgets live-delivered cursors
    /// it now covers. The floor never moves backwards.
    /// </summary>
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
