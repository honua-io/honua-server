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
    Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);
}
