// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Core.Features.Infrastructure.Coordination;

// Paces one database polling loop; one instance per loop, not thread-safe. Call BeginPoll before
// querying for work. After a pass that found work, or failed, the busy interval runs in full: the
// loop's own writes to that work ring the same signal and must not make it spin. After an idle pass
// the wait doubles up to the ceiling (5 s, 10 s, 20 s, 40 s, 60 s by default) and a ring from this
// process ends it early and resets it, though never sooner than WakeFloor after the pass began, so a
// burst of writes costs one query per floor. Without a signal nothing can end the wait early, so the
// loop keeps the busy interval. Work written by another process waits out the current interval.
public sealed class IdlePollPacer
{
    public static readonly TimeSpan DefaultBusyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaxIdleInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan WakeFloor = TimeSpan.FromSeconds(1);

    private readonly PollWakeSignal? _wake;
    private long _observedVersion;
    private long _pollStarted;
    private TimeSpan _idleInterval;

    public IdlePollPacer(PollWakeSignal? wake, TimeSpan? busyInterval = null, TimeSpan? maxIdleInterval = null)
    {
        BusyInterval = busyInterval ?? DefaultBusyInterval;
        MaxIdleInterval = maxIdleInterval ?? DefaultMaxIdleInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BusyInterval, TimeSpan.Zero, nameof(busyInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIdleInterval, BusyInterval, nameof(maxIdleInterval));

        _wake = wake;
        _idleInterval = BusyInterval;
    }

    public TimeSpan BusyInterval { get; }

    public TimeSpan MaxIdleInterval { get; }

    public void BeginPoll()
    {
        _observedVersion = _wake?.Version ?? 0;
        _pollStarted = Stopwatch.GetTimestamp();
    }

    public TimeSpan NextIdleDelay()
    {
        if (_wake is null)
        {
            return BusyInterval;
        }

        var delay = _idleInterval;
        var doubled = _idleInterval + _idleInterval;
        _idleInterval = doubled < MaxIdleInterval ? doubled : MaxIdleInterval;
        return delay;
    }

    public Task WaitAfterWorkAsync(CancellationToken cancellationToken)
    {
        _idleInterval = BusyInterval;
        return Task.Delay(BusyInterval, cancellationToken);
    }

    public async Task WaitIdleAsync(CancellationToken cancellationToken)
    {
        var delay = NextIdleDelay();
        if (_wake is null)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await _wake.WaitAsync(_observedVersion, delay, cancellationToken).ConfigureAwait(false))
        {
            _idleInterval = BusyInterval;
            var remaining = WakeFloor - Stopwatch.GetElapsedTime(_pollStarted);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
