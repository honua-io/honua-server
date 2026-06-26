// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Process-local in-memory store for managed WFS 2.0 stored queries. This is the
/// single-node default; Redis-enabled deployments register
/// <see cref="RedisWfsStoredQueryStore"/> instead so the set is shared across
/// replicas and survives restarts.
/// </summary>
internal sealed class InMemoryWfsStoredQueryStore : IWfsStoredQueryStore
{
    private readonly ConcurrentDictionary<string, WfsStoredQueryDefinition> _queries =
        new(StringComparer.OrdinalIgnoreCase);

    // Guards the capacity check + insert so concurrent creates cannot overshoot the cap.
    private readonly object _writeLock = new();

    public Task<WfsStoredQueryDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_queries.TryGetValue(id, out var definition) ? definition : null);
    }

    public Task<IReadOnlyList<WfsStoredQueryDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<WfsStoredQueryDefinition> snapshot = _queries.Values
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(snapshot);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_queries.Count);
    }

    public Task<WfsStoredQueryCreateStatus> TryCreateAsync(
        WfsStoredQueryDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_writeLock)
        {
            if (_queries.ContainsKey(definition.Id))
            {
                return Task.FromResult(WfsStoredQueryCreateStatus.DuplicateId);
            }

            if (_queries.Count >= IWfsStoredQueryStore.MaxManagedStoredQueryCount)
            {
                return Task.FromResult(WfsStoredQueryCreateStatus.CapacityExceeded);
            }

            _queries[definition.Id] = definition;
            return Task.FromResult(WfsStoredQueryCreateStatus.Created);
        }
    }

    public Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_queries.TryRemove(id, out _));
    }
}
