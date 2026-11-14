// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// One subscriber of one family. It first replays from the table after its position, holding live
// events that arrive meanwhile, then hands off to live delivery. Its position only increases, so an
// event is written at most once and in sequence order.
internal sealed class RealtimeSubscription : IRealtimeSubscription
{
    private readonly RealtimeDispatcher _dispatcher;
    private readonly RealtimeSubscriptionRequest _request;
    private readonly HashSet<string>? _scopes;
    private readonly Lock _gate = new();
    private readonly Queue<RealtimeEvent> _handoff = new();
    private readonly CancellationTokenSource _stopping = new();
    private bool _live;
    private bool _handoffOverflowed;
    private bool _closed;
    private bool _positioned;
    private long _position;
    private Task _replay = Task.CompletedTask;
    private int _disposed;

    public RealtimeSubscription(RealtimeDispatcher dispatcher, RealtimeSubscriptionRequest request)
    {
        _dispatcher = dispatcher;
        _request = request;
        _scopes = request.ScopeKeys is { Count: > 0 } ? new HashSet<string>(request.ScopeKeys, StringComparer.Ordinal) : null;
    }

    public RealtimeFamily Family => _request.Family;

    public long Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    // True once the subscription has read its starting window and holds a real position.
    public bool IsPositioned
    {
        get
        {
            lock (_gate)
            {
                return _positioned;
            }
        }
    }

    // True once replay has caught up and events go straight to the channel.
    public bool IsLive
    {
        get
        {
            lock (_gate)
            {
                return _live;
            }
        }
    }

    public void Start() => _replay = Task.Run(() => ReplayAsync(_stopping.Token));

    // Called by the dispatcher only, in sequence order; never blocks.
    public void Deliver(RealtimeEvent realtimeEvent, long head)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!_live)
            {
                if (_handoff.Count >= _dispatcher.Options.ReplayHandoffCapacity)
                {
                    // The replay re-reads these from the table instead.
                    _handoff.Clear();
                    _handoffOverflowed = true;
                }
                else
                {
                    _handoff.Enqueue(realtimeEvent);
                }

                return;
            }

            WriteLive(realtimeEvent, head);
        }
    }

    // Called by the dispatcher when retention removed events this task had not dispatched, the first
    // surviving one being firstAfterGap. A live subscription that had not passed the gap gets
    // reset(retention, head) and resumes after head, which its client resynchronizes to; one still
    // replaying re-reads the table, where its own window check sees the gap.
    public void DeliverGap(long firstAfterGap, long head)
    {
        lock (_gate)
        {
            if (_closed || _position >= firstAfterGap - 1)
            {
                return;
            }

            if (!_live)
            {
                _handoff.Clear();
                _handoffOverflowed = true;
                return;
            }

            if (!_request.Writer.TryWrite(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, head)))
            {
                CloseLocked(new RealtimeSubscriptionClosedException(RealtimeResetReasons.SlowConsumer));
                return;
            }

            _position = Math.Max(_position, head);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Close(null);
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _replay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The replay stops with the subscription.
        }

        _stopping.Dispose();
    }

    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReplayUntilLiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed while replaying.
        }
        catch (ChannelClosedException)
        {
            Close(null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _dispatcher.LogSubscriptionFailed(exception, _request.Family.Name);
            Close(exception);
        }
    }

    private async Task ReplayUntilLiveAsync(CancellationToken cancellationToken)
    {
        var options = _dispatcher.Options;
        var family = _request.Family.Name;
        var window = await _dispatcher.ReadWindowAsync(family, cancellationToken).ConfigureAwait(false);
        var (position, reset) = StartPosition(window, options);
        if (reset is not null)
        {
            await _request.Writer.WriteAsync(reset, cancellationToken).ConfigureAwait(false);
        }

        SetPosition(position);
        while (true)
        {
            var before = position;
            var page = await _dispatcher.ReadAfterAsync(family, position, _scopes, options.ReplayBatchSize, cancellationToken)
                .ConfigureAwait(false);

            var current = await ReadRetentionGapAsync(page, before, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                await _request.Writer.WriteAsync(
                    new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, current.Head), cancellationToken).ConfigureAwait(false);
                position = current.Head;
                SetPosition(position);
                window = current;
                continue;
            }

            position = await WriteReplayPageAsync(page, position, cancellationToken).ConfigureAwait(false);

            if (page.Count < options.ReplayBatchSize && !TryGoLive(window.Head))
            {
                return;
            }
        }
    }

    private async Task<long> WriteReplayPageAsync(
        IReadOnlyList<RealtimeEvent> page, long position, CancellationToken cancellationToken)
    {
        foreach (var realtimeEvent in page)
        {
            if (Matches(realtimeEvent))
            {
                await _request.Writer.WriteAsync(new RealtimeDelivery.Event(realtimeEvent), cancellationToken).ConfigureAwait(false);
            }

            position = realtimeEvent.Sequence;
            SetPosition(position);
        }

        return position;
    }

    private async Task<RealtimeLogWindow?> ReadRetentionGapAsync(
        IReadOnlyList<RealtimeEvent> page, long position, CancellationToken cancellationToken)
    {
        if (_dispatcher.IsContiguous(_request.Family.Name) && _scopes is null && page.Count > 0 && page[0].Sequence == position + 1)
        {
            return null;
        }

        var current = await _dispatcher.ReadWindowAsync(_request.Family.Name, cancellationToken).ConfigureAwait(false);
        return current.Oldest > position + 1 && position < current.Head ? current : null;
    }

    // Where replay starts, and the reset to send first when the requested position is unusable.
    private (long Position, RealtimeDelivery.Reset? Reset) StartPosition(RealtimeLogWindow window, RealtimeOptions options)
    {
        if (_request.After is not { } after)
        {
            return (window.Head, null);
        }

        if (after < window.Oldest - 1 || after > window.Head)
        {
            return (window.Head, new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, window.Head));
        }

        if (_dispatcher.IsContiguous(_request.Family.Name) && window.Head - after > options.ResumeMaxEvents)
        {
            return (window.Head, new RealtimeDelivery.Reset(RealtimeResetReasons.TooFarBehind, window.Head));
        }

        return (after, null);
    }

    // Hands off to live delivery. Returns true instead when held events overflowed, so the replay
    // must read the table again.
    private bool TryGoLive(long head)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            if (_handoffOverflowed)
            {
                _handoffOverflowed = false;
                _handoff.Clear();
                return true;
            }

            _live = true;
            while (_handoff.TryDequeue(out var held) && !_closed)
            {
                WriteLive(held, Math.Max(head, held.Sequence));
            }

            return false;
        }
    }

    // Caller holds _gate.
    private void WriteLive(RealtimeEvent realtimeEvent, long head)
    {
        if (realtimeEvent.Sequence <= _position)
        {
            return;
        }

        _position = realtimeEvent.Sequence;
        bool matches;
        try
        {
            matches = Matches(realtimeEvent);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _dispatcher.LogSubscriptionFailed(exception, _request.Family.Name);
            CloseLocked(exception);
            return;
        }

        if (matches && !_request.Writer.TryWrite(new RealtimeDelivery.Event(realtimeEvent)))
        {
            _request.Writer.TryWrite(new RealtimeDelivery.Reset(RealtimeResetReasons.SlowConsumer, head));
            CloseLocked(new RealtimeSubscriptionClosedException(RealtimeResetReasons.SlowConsumer));
        }
    }

    private bool Matches(RealtimeEvent realtimeEvent)
        => (_scopes is null || _scopes.Contains(realtimeEvent.ScopeKey))
           && (_request.Filter is null || _request.Filter(realtimeEvent));

    private void SetPosition(long position)
    {
        lock (_gate)
        {
            _position = Math.Max(_position, position);
            _positioned = true;
        }
    }

    private void Close(Exception? error)
    {
        lock (_gate)
        {
            CloseLocked(error);
        }
    }

    private void CloseLocked(Exception? error)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _handoff.Clear();
        _dispatcher.Remove(this);
        _request.Writer.TryComplete(error);
    }
}
