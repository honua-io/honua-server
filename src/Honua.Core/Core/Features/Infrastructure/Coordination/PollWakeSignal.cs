// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Coordination;

// Rung by a store after it writes work that a database poller picks up. Every waiting poller wakes,
// since several loops can share one store, and a ring that lands while a poller is busy is kept for
// its next wait: the poller reads Version before it queries and waits from that version. Only this
// process hears it; pollers in other processes keep their interval.
public class PollWakeSignal
{
    private readonly Lock _gate = new();
    private long _version;
    private TaskCompletionSource _next = NewWaiter();

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public void Wake()
    {
        TaskCompletionSource rung;
        lock (_gate)
        {
            _version++;
            rung = _next;
            _next = NewWaiter();
        }

        rung.TrySetResult();
    }

    // True when rung after observedVersion, false when the timeout passed first.
    public async Task<bool> WaitAsync(long observedVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task rung;
        lock (_gate)
        {
            if (_version != observedVersion)
            {
                return true;
            }

            rung = _next.Task;
        }

        try
        {
            await rung.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// One singleton per kind of work: TWork is the store or queue whose writes ring it.
public sealed class PollWakeSignal<TWork> : PollWakeSignal;
