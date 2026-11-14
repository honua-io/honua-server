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
            delivered += await DispatchFamilyAsync(family, head, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    private async Task<int> DispatchFamilyAsync(string family, long head, CancellationToken cancellationToken)
    {
        var delivered = 0;
        var dispatched = GetDispatchStart(family, head);
        var currentHead = head;
        RecordPosition(family, head, dispatched);
        while (dispatched < currentHead)
        {
            var batch = await log.ReadAfterAsync(new(family, dispatched, options.Value.ReadBatchSize), cancellationToken)
                .ConfigureAwait(false);
            RealtimeSubscription[] subscribers;
            lock (_gate)
            {
                subscribers = _subscriptions.GetValueOrDefault(family) ?? [];
            }

            if (batch.Count == 0 || batch[0].Sequence != dispatched + 1)
            {
                currentHead = await DeliverRetentionGapAsync(new(family, batch, subscribers), cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    // Appends after the empty read belong to the next probe.
                    dispatched = head;
                    break;
                }
            }

            RealtimeMetrics.DispatchLagSeconds.Record(
                Math.Max(0, (DateTimeOffset.UtcNow - batch[0].CreatedAt).TotalSeconds),
                new KeyValuePair<string, object?>("family", family));
            DeliverBatch(batch, subscribers, currentHead);

            delivered += batch.Count;
            dispatched = batch[^1].Sequence;
        }

        CompleteDispatch(family, currentHead, dispatched);
        return delivered;
    }

    private void CompleteDispatch(string family, long currentHead, long dispatched)
    {
        lock (_gate)
        {
            _dispatched[family] = Math.Max(_dispatched.GetValueOrDefault(family), dispatched);
        }
        RecordPosition(family, currentHead, dispatched);
        RealtimeMetrics.DispatchLagSeconds.Record(0, new KeyValuePair<string, object?>("family", family));
    }

    private static void RecordPosition(string family, long head, long dispatched)
    {
        var tag = new KeyValuePair<string, object?>("family", family);
        RealtimeMetrics.LogHead.Record(head, tag);
        RealtimeMetrics.DispatchPosition.Record(dispatched, tag);
        RealtimeMetrics.DispatchLagEvents.Record(Math.Max(0, head - dispatched), tag);
    }

    private long GetDispatchStart(string family, long head)
    {
        long dispatched;
        lock (_gate)
        {
            dispatched = _dispatched.GetValueOrDefault(family, head);
            var subscribers = _subscriptions.GetValueOrDefault(family) ?? [];
            if (head <= dispatched || subscribers.Length == 0)
            {
                // Nobody here listens, so nothing is read; a later subscriber replays from the table.
                _dispatched[family] = Math.Max(dispatched, head);
                return head;
            }
        }

        return dispatched;
    }

    private sealed record RetentionGap(string Family, IReadOnlyList<RealtimeEvent> Batch, RealtimeSubscription[] Subscribers);

    private async Task<long> DeliverRetentionGapAsync(RetentionGap gap, CancellationToken cancellationToken)
    {
        var window = await log.ReadWindowAsync(gap.Family, cancellationToken).ConfigureAwait(false);
        var firstAfterGap = gap.Batch.Count == 0 ? window.Oldest : gap.Batch[0].Sequence;
        foreach (var subscriber in gap.Subscribers)
        {
            subscriber.DeliverGap(firstAfterGap, window.Head);
        }

        return window.Head;
    }

    private static void DeliverBatch(
        IReadOnlyList<RealtimeEvent> batch, RealtimeSubscription[] subscribers, long head)
    {
        foreach (var realtimeEvent in batch)
        {
            foreach (var subscriber in subscribers)
            {
                subscriber.Deliver(realtimeEvent, head);
            }
        }
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
