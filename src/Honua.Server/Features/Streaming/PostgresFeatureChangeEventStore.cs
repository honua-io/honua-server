// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

// Feature-change events read from honua.feature_changes (ADR-0085 replaces ADR-0082 D7). Nothing is
// appended here: the change-tracking trigger writes each change in its edit transaction, so the
// store is durable and shared by every task by construction.
internal sealed class PostgresFeatureChangeEventStore(PostgresFeatureChangeLog log)
    : IFeatureChangeEventStore, IFeatureChangeEventStoreHealth
{
    public bool CanPersistEvents => true;

    public bool IsUsingInMemoryFallback => false;

    public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
        => log.ReadHeadAsync(cancellationToken);

    public async Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
    {
        var window = await log.ReadWindowAsync(cancellationToken).ConfigureAwait(false);
        return window.Oldest;
    }

    public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
        => log.ReadAsync(new FeatureChangeLogQuery(cursor ?? 0, from, to, LayerIds: null, limit), cancellationToken);
}
