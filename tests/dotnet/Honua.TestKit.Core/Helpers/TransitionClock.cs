// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Helpers;

public sealed class TransitionClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly List<TransitionTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() => _now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TransitionTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan time)
    {
        _now += time;
        foreach (var timer in _timers.ToArray())
        {
            timer.Fire();
        }
    }
    private sealed class TransitionTimer(TransitionClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;
        private TimeSpan _period;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
            _period = period;
            return true;
        }
        public void Fire()
        {
            if (_due <= clock.GetUtcNow())
            {
                Change(_period, _period);
                callback(state);
            }
        }
        public void Dispose() => _due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
