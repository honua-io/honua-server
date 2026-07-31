// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Persists and queries feature-change events for replay.
/// </summary>
internal interface IFeatureChangeEventStore
{
    Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default);

    Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the lowest cursor still present in the retained window, or 0 when the store
    /// holds no events. Consumers compare a client-supplied resume cursor against this
    /// value to tell "no events since your cursor" apart from "the events since your cursor
    /// have been trimmed or expired" — the latter requires a replacement snapshot rather
    /// than silently continuing with deltas.
    /// <para>
    /// Because the value gates that decision, an implementation that cannot determine the
    /// oldest retained cursor must fail CLOSED by returning <see cref="long.MaxValue"/>,
    /// which reads as "everything you are missing is gone" and forces a replacement
    /// snapshot. Returning a lower bound instead would admit a resume whose events are
    /// actually unavailable, leaving the client permanently short of state it believes it
    /// has.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The oldest retained cursor, 0 when the store is empty, or <see cref="long.MaxValue"/>
    /// when the retained window cannot be determined.
    /// </returns>
    Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes runtime durability state for feature-change event storage.
/// </summary>
internal interface IFeatureChangeEventStoreHealth
{
    bool CanPersistEvents { get; }

    /// <summary>
    /// True when events are currently persisted to node-local in-memory storage instead of
    /// durable Redis storage — either because no Redis is configured (explicit single-node
    /// mode) or because Redis is temporarily unavailable and fallback is permitted.
    /// Mirrors <c>ICacheHealthChecker.IsUsingFallback</c> (ADR-0017) so readiness can report
    /// healthy-with-degraded-note instead of failing.
    /// </summary>
    bool IsUsingInMemoryFallback { get; }
}

/// <summary>
/// Publishes normalized feature-change notifications.
/// </summary>
internal interface IFeatureChangeEventPublisher
{
    /// <summary>
    /// Best-effort publish: callers tolerate the publisher silently swapping a
    /// failed durable append for a retry-queue enqueue. Used by the inline
    /// post-commit publish path (legacy non-outbox deployments) and by the
    /// retry queue itself.
    /// </summary>
    Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strict publish: throws when the durable append fails so the caller can
    /// keep its own durability state intact. Used by the transactional outbox
    /// dispatcher (#692) so a failed append leaves the outbox row claimed/failed
    /// for a future retry instead of being silently moved to the best-effort
    /// retry queue, which can be in-memory when no distributed cache is available.
    /// </summary>
    Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);
}
