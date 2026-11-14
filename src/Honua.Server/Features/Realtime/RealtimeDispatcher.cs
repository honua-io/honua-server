// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// Follows the realtime log for this task and hands each new event to this task's subscriptions
// (specification section 6.1). One probe reads every family's head; every family with
// subscriptions and a head ahead of its dispatched position is read in batches, in order.
// Dispatch never waits for a subscriber: a full channel closes that subscription. Families kept
// outside the log (features) are read through their IRealtimeFamilySource.
internal sealed partial class RealtimeDispatcher(
    PostgresRealtimeEventLog log,
    IEnumerable<IRealtimeFamilySource> sources,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeDispatcher> logger) : IRealtimeWakeSignal
{
    private readonly Dictionary<string, IRealtimeFamilySource> _sources =
        sources.ToDictionary(source => source.Family.Name, StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _dispatched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealtimeSubscription[]> _subscriptions = new(StringComparer.Ordinal);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private bool _initialized;

    public RealtimeOptions Options => options.Value;

    // Sequences of a logged family have no gaps except those retention leaves; a source's may.
    public bool IsContiguous(string family) => !_sources.ContainsKey(family);

    public Task<IReadOnlyList<RealtimeEvent>> ReadAfterAsync(
        string family,
        long after,
        IReadOnlyCollection<string>? scopeKeys,
        int limit,
        CancellationToken cancellationToken)
        => _sources.TryGetValue(family, out var source)
            ? source.ReadAfterAsync(after, scopeKeys, limit, cancellationToken)
            : log.ReadAfterAsync(new(family, after, limit, scopeKeys), cancellationToken);

    public Task<RealtimeLogWindow> ReadWindowAsync(string family, CancellationToken cancellationToken)
        => _sources.TryGetValue(family, out var source)
            ? source.ReadWindowAsync(cancellationToken)
            : log.ReadWindowAsync(family, cancellationToken);

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
        if (!RealtimeLogFamilies.Logged.Contains(request.Family.Name, StringComparer.Ordinal)
            && !_sources.ContainsKey(request.Family.Name))
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
        var heads = await ReadHeadsAsync(cancellationToken).ConfigureAwait(false);
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

        var heads = await ReadHeadsAsync(cancellationToken).ConfigureAwait(false);
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
        while (dispatched < currentHead)
        {
            var batch = await ReadAfterAsync(family, dispatched, null, options.Value.ReadBatchSize, cancellationToken)
                .ConfigureAwait(false);
            RealtimeSubscription[] subscribers;
            lock (_gate)
            {
                subscribers = _subscriptions.GetValueOrDefault(family) ?? [];
            }

            if (batch.Count == 0 && !IsContiguous(family))
            {
                dispatched = head;
                break;
            }

            if (IsContiguous(family) && (batch.Count == 0 || batch[0].Sequence != dispatched + 1))
            {
                currentHead = await DeliverRetentionGapAsync(new(family, batch, subscribers), cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    // Appends after the empty read belong to the next probe.
                    dispatched = head;
                    break;
                }
            }

            DeliverBatch(batch, subscribers, currentHead);

            delivered += batch.Count;
            dispatched = batch[^1].Sequence;
        }

        lock (_gate)
        {
            _dispatched[family] = Math.Max(_dispatched.GetValueOrDefault(family), dispatched);
        }
        return delivered;
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
        var window = await ReadWindowAsync(gap.Family, cancellationToken).ConfigureAwait(false);
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

    // The head of every logged family and of every source.
    private async Task<IReadOnlyDictionary<string, long>> ReadHeadsAsync(CancellationToken cancellationToken)
    {
        var logged = await log.ReadHeadsAsync(cancellationToken).ConfigureAwait(false);
        if (_sources.Count == 0)
        {
            return logged;
        }

        var heads = new Dictionary<string, long>(logged, StringComparer.Ordinal);
        foreach (var (family, source) in _sources)
        {
            heads[family] = await source.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        }

        return heads;
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
