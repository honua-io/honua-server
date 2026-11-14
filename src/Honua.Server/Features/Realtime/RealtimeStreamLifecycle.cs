// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

internal sealed class RealtimeStreamLifecycle(TimeProvider clock, IOptions<RealtimeOptions> options) : IRealtimeStreamLifecycle
{
    private readonly Lock _gate = new();
    private readonly HashSet<StreamSession> _sessions = [];
    private readonly TaskCompletionSource _empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _draining;

    public bool IsDraining { get { lock (_gate) { return _draining is not null; } } }

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(15);

    public IRealtimeStreamSession? TryOpen(RealtimeStreamRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_draining is not null)
            {
                return null;
            }

            var session = new StreamSession(this, registration, clock);
            _sessions.Add(session);
            RealtimeMetrics.Subscribers.Add(1,
                new("family", registration.Family.Name), new("transport", registration.Transport));
            return session;
        }
    }

    public void BeginDrain()
    {
        lock (_gate)
        {
            // Defer signaling until the admission lock is released, including synchronous callbacks.
            _draining ??= Task.Run(DrainSessionsAsync);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        BeginDrain();
        Task draining;
        lock (_gate)
        {
            draining = _draining!;
        }

        try
        {
            await draining.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortRemaining();
            throw;
        }
    }

    private async Task DrainSessionsAsync()
    {
        StreamSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        if (sessions.Length == 0)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(options.Value.ShutdownDrain, clock);
        var signals = sessions.Select((session, index) => SignalCloseAsync(
            session, TimeSpan.FromTicks(options.Value.ShutdownDrain.Ticks * index / sessions.Length), deadline.Token));
        try
        {
            await Task.WhenAll(signals).WaitAsync(deadline.Token).ConfigureAwait(false);
            await _empty.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // The grace period expired; force the remaining transports closed below.
        }
        finally
        {
            AbortRemaining();
        }
    }

    private async Task SignalCloseAsync(StreamSession session, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        await session.SignalCloseAsync().ConfigureAwait(false);
    }

    private void AbortRemaining()
    {
        StreamSession[] remaining;
        lock (_gate)
        {
            remaining = [.. _sessions];
        }

        List<Exception>? failures = null;
        foreach (var session in remaining)
        {
            try { session.Abort(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Realtime streams could not all be aborted.", failures);
        }
    }

    private void Release(StreamSession session, RealtimeStreamRegistration registration)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
            RealtimeMetrics.Subscribers.Add(-1,
                new("family", registration.Family.Name), new("transport", registration.Transport));
            if (_draining is not null && _sessions.Count == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    private sealed class StreamSession(RealtimeStreamLifecycle owner, RealtimeStreamRegistration registration, TimeProvider clock) : IRealtimeStreamSession
    {
        private readonly CancellationTokenSource _closing = new();
        private readonly Lock _transportGate = new();
        private int _released;
        private bool _aborted;

        public CancellationToken DrainingToken => _closing.Token;

        public int RetryMilliseconds { get; } = Random.Shared.Next(1000, 5001);

        public void RecordDelivery(DateTimeOffset committedAt)
            => RealtimeMetrics.DeliveryLatency.Record(Math.Max(0, (clock.GetUtcNow() - committedAt).TotalSeconds),
                new("family", registration.Family.Name), new("transport", registration.Transport));

        public void RecordReset(string reason)
            => RealtimeMetrics.Resets.Add(1, new("family", registration.Family.Name),
                new("transport", registration.Transport), new("reason", reason));

        public void RecordAuthorizationFilter()
            => RealtimeMetrics.FramesFiltered.Add(1,
                new("family", registration.Family.Name), new("transport", registration.Transport));

        public void RecordRevocation()
            => RealtimeMetrics.StreamsRevoked.Add(1,
                new("family", registration.Family.Name), new("transport", registration.Transport));

        public Task SignalCloseAsync() => Volatile.Read(ref _released) == 0 ? _closing.CancelAsync() : Task.CompletedTask;

        public void Abort()
        {
            lock (_transportGate)
            {
                if (_released == 0 && !_aborted)
                {
                    _aborted = true;
                    registration.Abort();
                }
            }
        }

        public void Dispose()
        {
            lock (_transportGate)
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    owner.Release(this, registration);
                }
            }
        }
    }
}
