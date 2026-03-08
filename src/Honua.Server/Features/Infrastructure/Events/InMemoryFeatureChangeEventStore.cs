// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Feature-change event store with Redis/distributed-cache persistence and in-memory fallback.
/// </summary>
internal sealed class InMemoryFeatureChangeEventStore(
    IOptions<FeatureChangeEventOptions> options,
    IDistributedCache? distributedCache = null,
    IConnectionMultiplexer? redis = null) : IFeatureChangeEventStore, IDisposable
{
    private const string CursorKey = "featurechange:cursor";
    private const string IndexKey = "featurechange:index";
    private const string EventKeyPrefix = "featurechange:event:";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly object _sync = new();
    private readonly List<FeatureChangeEvent> _events = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maxRetained = Math.Max(100, options.Value.MaxRetainedEvents);
    private readonly IDistributedCache? _distributedCache = distributedCache;
    private readonly IDatabase? _redisDb = redis?.GetDatabase();
    private long _nextCursor = 1;

    public async Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedOperation = NormalizeOperation(request.Operation);
        var normalizedProtocol = string.IsNullOrWhiteSpace(request.Protocol)
            ? "unknown"
            : request.Protocol.Trim();
        var normalizedServiceId = string.IsNullOrWhiteSpace(request.ServiceId)
            ? "unknown"
            : request.ServiceId.Trim();
        var normalizedRequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? "unknown"
            : request.RequestId.Trim();

        if (_redisDb != null)
        {
            return await AppendWithRedisAsync(
                    normalizedServiceId,
                    request.LayerId,
                    request.ObjectId,
                    normalizedOperation,
                    normalizedProtocol,
                    normalizedRequestId,
                    request.Timestamp,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (_distributedCache != null)
        {
            return await AppendWithDistributedCacheAsync(
                    normalizedServiceId,
                    request.LayerId,
                    request.ObjectId,
                    normalizedOperation,
                    normalizedProtocol,
                    normalizedRequestId,
                    request.Timestamp,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        FeatureChangeEvent created;
        lock (_sync)
        {
            created = CreateEvent(
                _nextCursor++,
                normalizedServiceId,
                request.LayerId,
                request.ObjectId,
                normalizedOperation,
                normalizedProtocol,
                normalizedRequestId,
                request.Timestamp);

            _events.Add(created);
            TrimIfNeeded();
        }

        return created;
    }

    public async Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveLimit = Math.Clamp(limit, 1, 5_000);

        if (_redisDb != null)
        {
            return await QueryRedisAsync(cursor, from, to, effectiveLimit, cancellationToken).ConfigureAwait(false);
        }

        if (_distributedCache != null)
        {
            return await QueryDistributedCacheAsync(cursor, from, to, effectiveLimit, cancellationToken).ConfigureAwait(false);
        }

        List<FeatureChangeEvent> snapshot;
        lock (_sync)
        {
            snapshot = _events.ToList();
        }

        return snapshot
            .Where(e => !cursor.HasValue || e.Cursor > cursor.Value)
            .Where(e => !from.HasValue || e.Timestamp >= from.Value)
            .Where(e => !to.HasValue || e.Timestamp <= to.Value)
            .OrderBy(e => e.Cursor)
            .Take(effectiveLimit)
            .ToArray();
    }

    private async Task<FeatureChangeEvent> AppendWithRedisAsync(
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string requestId,
        DateTimeOffset? timestamp,
        CancellationToken cancellationToken)
    {
        var cursor = (long)await _redisDb!.StringIncrementAsync(CursorKey).ConfigureAwait(false);
        var created = CreateEvent(cursor, serviceId, layerId, objectId, operation, protocol, requestId, timestamp);
        var eventKey = GetEventKey(cursor);
        var eventJson = JsonSerializer.Serialize(created, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);

        await _redisDb.StringSetAsync(eventKey, eventJson, Retention).ConfigureAwait(false);
        await _redisDb.SortedSetAddAsync(IndexKey, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), cursor).ConfigureAwait(false);
        await TrimRedisAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task<FeatureChangeEvent> AppendWithDistributedCacheAsync(
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string requestId,
        DateTimeOffset? timestamp,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cursors = await ReadCachedIndexAsync(cancellationToken).ConfigureAwait(false);
            var cursor = await ReadCachedCursorAsync(cancellationToken).ConfigureAwait(false) + 1;
            var created = CreateEvent(cursor, serviceId, layerId, objectId, operation, protocol, requestId, timestamp);
            cursors.Add(cursor);

            while (cursors.Count > _maxRetained)
            {
                var removedCursor = cursors[0];
                cursors.RemoveAt(0);
                await _distributedCache!.RemoveAsync(GetEventKey(removedCursor), cancellationToken).ConfigureAwait(false);
            }

            await _distributedCache!.SetStringAsync(
                    GetEventKey(cursor),
                    JsonSerializer.Serialize(created, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Retention },
                    cancellationToken)
                .ConfigureAwait(false);
            await _distributedCache!.SetStringAsync(
                    CursorKey,
                    cursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Retention },
                    cancellationToken)
                .ConfigureAwait(false);
            await _distributedCache!.SetStringAsync(
                    IndexKey,
                    JsonSerializer.Serialize(cursors),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Retention },
                    cancellationToken)
                .ConfigureAwait(false);

            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<FeatureChangeEvent>> QueryRedisAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        var minimumCursor = (cursor ?? 0) + 1;
        var members = await _redisDb!.SortedSetRangeByScoreAsync(
                IndexKey,
                minimumCursor,
                double.PositiveInfinity,
                Exclude.None,
                Order.Ascending,
                take: Math.Max(limit * 4, limit))
            .ConfigureAwait(false);

        var results = new List<FeatureChangeEvent>(Math.Min(limit, members.Length));
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!long.TryParse(member.ToString(), out var eventCursor))
            {
                continue;
            }

            var json = await _redisDb.StringGetAsync(GetEventKey(eventCursor)).ConfigureAwait(false);
            if (!json.HasValue)
            {
                await _redisDb.SortedSetRemoveAsync(IndexKey, member).ConfigureAwait(false);
                continue;
            }

            var featureEvent = JsonSerializer.Deserialize(json.ToString()!, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
            if (featureEvent == null || !MatchesWindow(featureEvent, from, to))
            {
                continue;
            }

            results.Add(featureEvent);
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<FeatureChangeEvent>> QueryDistributedCacheAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cursors = await ReadCachedIndexAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<FeatureChangeEvent>(Math.Min(limit, cursors.Count));
            foreach (var eventCursor in cursors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor.HasValue && eventCursor <= cursor.Value)
                {
                    continue;
                }

                var json = await _distributedCache!.GetStringAsync(GetEventKey(eventCursor), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var featureEvent = JsonSerializer.Deserialize(json, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
                if (featureEvent == null || !MatchesWindow(featureEvent, from, to))
                {
                    continue;
                }

                results.Add(featureEvent);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TrimRedisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var length = await _redisDb!.SortedSetLengthAsync(IndexKey).ConfigureAwait(false);
        if (length <= _maxRetained)
        {
            return;
        }

        var removeCount = length - _maxRetained;
        var staleMembers = await _redisDb.SortedSetRangeByRankAsync(IndexKey, 0, removeCount - 1, Order.Ascending).ConfigureAwait(false);
        foreach (var member in staleMembers)
        {
            if (long.TryParse(member.ToString(), out var cursor))
            {
                await _redisDb.KeyDeleteAsync(GetEventKey(cursor)).ConfigureAwait(false);
            }
        }

        if (staleMembers.Length > 0)
        {
            await _redisDb.SortedSetRemoveAsync(IndexKey, staleMembers).ConfigureAwait(false);
        }
    }

    private async Task<List<long>> ReadCachedIndexAsync(CancellationToken cancellationToken)
    {
        var json = await _distributedCache!.GetStringAsync(IndexKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<long>>(json) ?? [];
    }

    private async Task<long> ReadCachedCursorAsync(CancellationToken cancellationToken)
    {
        var value = await _distributedCache!.GetStringAsync(CursorKey, cancellationToken).ConfigureAwait(false);
        return long.TryParse(value, out var cursor) ? cursor : 0L;
    }

    private static FeatureChangeEvent CreateEvent(
        long cursor,
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string requestId,
        DateTimeOffset? timestamp)
        => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Cursor = cursor,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = objectId,
            Operation = operation,
            Protocol = protocol,
            RequestId = requestId
        };

    private void TrimIfNeeded()
    {
        if (_events.Count <= _maxRetained)
        {
            return;
        }

        var removeCount = _events.Count - _maxRetained;
        _events.RemoveRange(0, removeCount);
    }

    private static bool MatchesWindow(FeatureChangeEvent featureEvent, DateTimeOffset? from, DateTimeOffset? to)
        => (!from.HasValue || featureEvent.Timestamp >= from.Value)
           && (!to.HasValue || featureEvent.Timestamp <= to.Value);

    private static string GetEventKey(long cursor) => $"{EventKeyPrefix}{cursor}";

    private static string NormalizeOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "update";
        }

        var normalized = operation.Trim().ToLowerInvariant();
        return normalized switch
        {
            "create" or "update" or "delete" => normalized,
            _ => "update"
        };
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
