// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// Follows the realtime log for this task and hands each new event to this task's subscriptions
// (specification section 6.1). One probe reads every family's head; every family with
// subscriptions and a head ahead of its dispatched position is read in batches, in order.
// Dispatch never waits for a subscriber: a full channel closes that subscription.
internal sealed partial class RealtimeDispatcher(
    PostgresRealtimeEventLog log,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeDispatcher> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _dispatched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeSubscription[]> _subscriptions = new(StringComparer.Ordinal);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private bool _initialized;

    public RealtimeOptions Options => options.Value;

    internal PostgresRealtimeEventLog Log => log;

    // Asks for a probe as soon as possible; any number of calls before it runs coalesce.
    public void Wake() => _wake.Writer.TryWrite(true);

    // True when woken, false when the interval passed first.
    public async Task<bool> WaitForWakeAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(interval);
        try
        {
            await _wake.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Drops wake-ups that arrived while a probe was pending, so they do not cause a second probe.
    public void DrainWakeups()
    {
        while (_wake.Reader.TryRead(out _))
        {
        }
    }

    public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Writer);
        if (!RealtimeLogFamilies.Logged.Contains(request.Family.Name, StringComparer.Ordinal))
        {
            throw new NotSupportedException($"The realtime log does not carry the '{request.Family.Name}' family.");
        }

        var subscription = new RealtimeSubscription(this, request);
        lock (_gate)
        {
            _subscriptions[request.Family.Name] = _subscriptions.TryGetValue(request.Family.Name, out var current)
                ? [.. current, subscription]
                : [subscription];
        }

        subscription.Start();
        Wake();
        return subscription;
    }

    // Sets every family's dispatched position to its head, so live subscribers never see history.
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var heads = await log.ReadHeadsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, RealtimeSubscription[]> early;
        lock (_gate)
        {
            early = new Dictionary<string, RealtimeSubscription[]>(_subscriptions, StringComparer.Ordinal);
        }

        // A subscription that went live before this task first probed must not miss what committed
        // in between, so dispatch starts no later than its position. Positions are read outside
        // _gate: a subscription takes its own lock before the dispatcher's, never after.
        // A subscription that has not yet read its starting window reads the table after these heads,
        // so it needs nothing from dispatch before them.
        var starts = heads.ToDictionary(
            entry => entry.Key,
            entry => early.TryGetValue(entry.Key, out var subscribers)
                ? subscribers.Where(subscription => subscription.IsPositioned)
                    .Select(subscription => subscription.Position)
                    .Append(entry.Value)
                    .Min()
                : entry.Value,
            StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var (family, start) in starts)
            {
                _dispatched[family] = start;
            }

            _initialized = true;
        }
    }

    // One probe and the reads it calls for. Returns the number of events handed to subscriptions.
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var heads = await log.ReadHeadsAsync(cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        foreach (var (family, head) in heads)
        {
            RealtimeSubscription[] subscribers;
            long dispatched;
            lock (_gate)
            {
                dispatched = _dispatched.GetValueOrDefault(family, head);
                subscribers = _subscriptions.GetValueOrDefault(family) ?? [];
                if (head <= dispatched || subscribers.Length == 0)
                {
                    // Nobody here listens, so nothing is read; a later subscriber replays from the table.
                    _dispatched[family] = Math.Max(dispatched, head);
                    continue;
                }
            }

            var currentHead = head;
            while (dispatched < currentHead)
            {
                var batch = await log.ReadAfterAsync(new(family, dispatched, options.Value.ReadBatchSize), cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    subscribers = _subscriptions.GetValueOrDefault(family) ?? [];
                }

                // Sequences have no gaps except those retention leaves, so a missing next sequence
                // means rows this task never dispatched were swept. Every subscriber is told to
                // resynchronize; dispatch then carries on in order. It never skips silently.
                if (batch.Count == 0 || batch[0].Sequence != dispatched + 1)
                {
                    var window = await log.ReadWindowAsync(family, cancellationToken).ConfigureAwait(false);
                    currentHead = window.Head;
                    var firstAfterGap = batch.Count == 0 ? window.Oldest : batch[0].Sequence;
                    foreach (var subscriber in subscribers)
                    {
                        subscriber.DeliverGap(firstAfterGap, currentHead);
                    }

                    if (batch.Count == 0)
                    {
                        // Rows may have been appended after the empty read. Leave them for the
                        // next probe, including for subscribers already past the retention gap.
                        dispatched = head;
                        break;
                    }
                }

                foreach (var realtimeEvent in batch)
                {
                    foreach (var subscriber in subscribers)
                    {
                        subscriber.Deliver(realtimeEvent, currentHead);
                    }
                }

                delivered += batch.Count;
                dispatched = batch[^1].Sequence;
            }

            lock (_gate)
            {
                _dispatched[family] = Math.Max(_dispatched.GetValueOrDefault(family), dispatched);
            }
        }

        return delivered;
    }

    internal void Remove(RealtimeSubscription subscription)
    {
        lock (_gate)
        {
            var family = subscription.Family.Name;
            if (_subscriptions.TryGetValue(family, out var current))
            {
                var remaining = current.Where(existing => !ReferenceEquals(existing, subscription)).ToArray();
                if (remaining.Length == 0)
                {
                    _subscriptions.Remove(family);
                }
                else
                {
                    _subscriptions[family] = remaining;
                }
            }
        }
    }

    internal int SubscriberCount(string family)
    {
        lock (_gate)
        {
            return _subscriptions.GetValueOrDefault(family)?.Length ?? 0;
        }
    }

    internal void LogSubscriptionFailed(Exception exception, string family) => LogSubscriptionFailure(logger, family, exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A realtime subscription to {Family} failed and was closed.")]
    private static partial void LogSubscriptionFailure(ILogger logger, string family, Exception exception);
}

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
            var options = _dispatcher.Options;
            var log = _dispatcher.Log;
            var family = _request.Family.Name;
            var window = await log.ReadWindowAsync(family, cancellationToken).ConfigureAwait(false);
            var (position, reset) = StartPosition(window, options);
            if (reset is not null)
            {
                await _request.Writer.WriteAsync(reset, cancellationToken).ConfigureAwait(false);
            }

            SetPosition(position);
            while (true)
            {
                var before = position;
                var page = await log.ReadAfterAsync(new(family, position, options.ReplayBatchSize, _scopes), cancellationToken)
                    .ConfigureAwait(false);

                // The sweep may have run since the window was read. An unscoped page that starts
                // right after the position proves nothing was lost; otherwise the oldest retained
                // sequence, read after the page, says whether anything after the position is gone.
                if (_scopes is not null || page.Count == 0 || page[0].Sequence != before + 1)
                {
                    var current = await log.ReadWindowAsync(family, cancellationToken).ConfigureAwait(false);
                    if (current.Oldest > before + 1 && before < current.Head)
                    {
                        await _request.Writer.WriteAsync(
                                new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, current.Head),
                                cancellationToken)
                            .ConfigureAwait(false);
                        position = current.Head;
                        SetPosition(position);
                        window = current;
                        continue;
                    }
                }

                foreach (var realtimeEvent in page)
                {
                    if (Matches(realtimeEvent))
                    {
                        await _request.Writer.WriteAsync(new RealtimeDelivery.Event(realtimeEvent), cancellationToken).ConfigureAwait(false);
                    }

                    position = realtimeEvent.Sequence;
                    SetPosition(position);
                }

                if (page.Count < options.ReplayBatchSize && !TryGoLive(window.Head))
                {
                    return;
                }
            }
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

        if (window.Head - after > options.ResumeMaxEvents)
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
