// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Persists and queries feature-change events for replay.
/// </summary>
internal interface IFeatureChangeEventStore
{
    Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default);

    Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default);

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
