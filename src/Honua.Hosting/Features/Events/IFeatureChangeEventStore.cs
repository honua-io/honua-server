// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

internal readonly record struct FeatureChangeRetentionWindow(
    long CurrentCursor,
    long OldestRetainedCursor,
    bool IsEmpty,
    bool IsDeterminate)
{
    internal static FeatureChangeRetentionWindow Retained(long currentCursor, long oldestRetainedCursor)
        => new(currentCursor, oldestRetainedCursor, IsEmpty: false, IsDeterminate: true);

    internal static FeatureChangeRetentionWindow KnownEmpty(long currentCursor)
        => new(currentCursor, OldestRetainedCursor: 0, IsEmpty: true, IsDeterminate: true);

    internal static FeatureChangeRetentionWindow Indeterminate(long currentCursor)
        => new(currentCursor, OldestRetainedCursor: long.MaxValue, IsEmpty: false, IsDeterminate: false);

    internal bool HasGapAfter(long cursor)
    {
        if (!IsDeterminate)
        {
            return true;
        }

        if (cursor >= CurrentCursor)
        {
            return false;
        }

        if (IsEmpty)
        {
            return true;
        }

        return cursor < long.MaxValue && OldestRetainedCursor > cursor + 1;
    }
}

internal interface IFeatureChangeEventStore
{
    Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default);

    Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default);

    // An implementation that cannot determine the oldest retained cursor must fail closed, because a low guess would resume over events that are gone.
    Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    async Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await GetOldestRetainedCursorAsync(cancellationToken).ConfigureAwait(false);

        if (oldest == long.MaxValue)
        {
            return FeatureChangeRetentionWindow.Indeterminate(current);
        }

        return oldest == 0
            ? FeatureChangeRetentionWindow.KnownEmpty(current)
            : FeatureChangeRetentionWindow.Retained(current, oldest);
    }

    Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default);
}

internal interface IFeatureChangeEventStoreHealth
{
    bool CanPersistEvents { get; }

    bool IsUsingInMemoryFallback { get; }
}

internal interface IFeatureChangeEventPublisher
{
    // Best-effort publish may replace a failed durable append with a retry enqueue.
    Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);

    // Strict publish throws on a failed append so the outbox row stays claimed instead of moving to a queue that may be memory-only.
    Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);
}
