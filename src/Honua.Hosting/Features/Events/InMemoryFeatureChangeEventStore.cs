// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Feature-change event store with Redis persistence and in-memory fallback.
/// Distributed-cache-only persistence is intentionally disabled because it cannot
/// guarantee cross-node ordering or single-writer safety.
/// </summary>
internal sealed class InMemoryFeatureChangeEventStore(
    IOptions<FeatureChangeEventOptions> options,
    IConnectionMultiplexer? redis = null,
    bool allowInMemoryFallback = true) : IFeatureChangeEventStore, IFeatureChangeEventStoreHealth, IDisposable
{
    private const string DurableRedisUnavailableMessage = "Feature-change event storage requires Redis durability in this environment.";
    private const string CursorKey = "featurechange:cursor";
    private const string IndexKey = "featurechange:index";
    private const string EventKeyPrefix = "featurechange:event:";
    private const string EventIdKeyPrefix = "featurechange:eventid:";
    private const string AppendEventScript = """
        local existingCursor = redis.call('GET', KEYS[3])
        if existingCursor then
            return { existingCursor, 0 }
        end
        local cursor = redis.call('INCR', KEYS[1])
        local event = {
            EventId = ARGV[1],
            Cursor = tonumber(cursor),
            Timestamp = ARGV[2],
            SourceId = ARGV[3],
            ServiceId = ARGV[4],
            LayerId = tonumber(ARGV[5]),
            ObjectId = tonumber(ARGV[6]),
            Operation = ARGV[7],
            Protocol = ARGV[8],
            RequestId = ARGV[9],
            GeometryChanged = ARGV[10] == '1'
        }
        if ARGV[11] ~= '' then
            event.ChangedAttributes = cjson.decode(ARGV[11])
        end
        if ARGV[12] ~= '' then
            event.GeometryEnvelope = cjson.decode(ARGV[12])
        end
        if ARGV[13] ~= '' then
            event.PropertiesJson = ARGV[13]
        end
        if ARGV[14] ~= '' then
            event.GeometryJson = ARGV[14]
        end
        if ARGV[15] ~= '' then
            event.GeometrySrid = tonumber(ARGV[15])
        end
        local minimumCursor = tonumber(ARGV[18])
        if minimumCursor and cursor <= minimumCursor then
            cursor = minimumCursor + 1
            redis.call('SET', KEYS[1], cursor)
            event.Cursor = cursor
        end
        local eventKey = ARGV[16] .. cursor
        local eventJson = cjson.encode(event)
        redis.call('SET', eventKey, eventJson, 'EX', tonumber(ARGV[17]))
        redis.call('SET', KEYS[3], cursor, 'EX', tonumber(ARGV[17]))
        redis.call('ZADD', KEYS[2], cursor, cursor)
        return { cursor, 1 }
        """;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly object _sync = new();
    private readonly List<FeatureChangeEvent> _events = [];
    private readonly Dictionary<string, FeatureChangeEvent> _eventsById = new(StringComparer.Ordinal);
    private readonly int _maxRetained = Math.Max(100, options.Value.MaxRetainedEvents);
    private readonly IDatabase? _redisDb = redis?.GetDatabase();
    private readonly bool _allowInMemoryFallback = allowInMemoryFallback;
    private long _nextCursor = 1;
    private long _lastObservedRedisCursor;
    private volatile bool _redisUnavailable;

    public bool CanPersistEvents => _redisDb is not null
        ? !_redisUnavailable || _allowInMemoryFallback
        : _allowInMemoryFallback;

    public bool IsUsingInMemoryFallback => _allowInMemoryFallback
        && (_redisDb is null || _redisUnavailable);

    public async Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_redisDb == null && !_allowInMemoryFallback)
        {
            throw new InvalidOperationException(DurableRedisUnavailableMessage);
        }

        var normalizedOperation = NormalizeOperation(request.Operation);
        var normalizedProtocol = string.IsNullOrWhiteSpace(request.Protocol)
            ? "unknown"
            : request.Protocol.Trim();
        var normalizedEventId = string.IsNullOrWhiteSpace(request.EventId)
            ? Guid.NewGuid().ToString("N")
            : request.EventId.Trim();
        var normalizedServiceId = string.IsNullOrWhiteSpace(request.ServiceId)
            ? "unknown"
            : request.ServiceId.Trim();
        var normalizedSourceId = string.IsNullOrWhiteSpace(request.SourceId)
            ? normalizedServiceId
            : request.SourceId.Trim();
        var normalizedRequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? "unknown"
            : request.RequestId.Trim();

        if (_redisDb != null && await EnsureRedisAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await AppendWithRedisAsync(
                        normalizedEventId,
                        normalizedSourceId,
                        normalizedServiceId,
                        request.LayerId,
                        request.ObjectId,
                        normalizedOperation,
                        normalizedProtocol,
                        normalizedRequestId,
                        request.Timestamp,
                        request.ChangedAttributes,
                        request.GeometryChanged,
                        request.GeometryEnvelope,
                        request.PropertiesJson,
                        request.GeometryJson,
                        request.GeometrySrid,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional: no ILogger is wired into this store (constructed by a factory
                // outside this project); degradation is surfaced via _redisUnavailable /
                // IsUsingInMemoryFallback for callers (health checks) to observe and log, and
                // the exception itself is preserved as the InnerException when fallback is
                // disallowed rather than being silently dropped.
                _redisUnavailable = true;
                if (!_allowInMemoryFallback)
                {
                    throw new InvalidOperationException(DurableRedisUnavailableMessage, ex);
                }
            }
        }

        if (!_allowInMemoryFallback)
        {
            throw new InvalidOperationException(DurableRedisUnavailableMessage);
        }

        FeatureChangeEvent created;
        lock (_sync)
        {
            if (_eventsById.TryGetValue(normalizedEventId, out var existing))
            {
                return existing;
            }

            // When entering fallback after Redis was in use, continue from the highest
            // cursor observed from Redis. Otherwise fallback events would restart at 1
            // and be silently filtered out by consumers (webhook dispatcher, replay API)
            // whose persisted cursor already advanced past them.
            var observedRedisCursor = Volatile.Read(ref _lastObservedRedisCursor);
            if (_nextCursor <= observedRedisCursor)
            {
                _nextCursor = observedRedisCursor + 1;
            }

            created = CreateEvent(
                normalizedEventId,
                _nextCursor++,
                normalizedSourceId,
                normalizedServiceId,
                request.LayerId,
                request.ObjectId,
                normalizedOperation,
                normalizedProtocol,
                normalizedRequestId,
                request.Timestamp,
                request.ChangedAttributes,
                request.GeometryChanged,
                request.GeometryEnvelope,
                request.PropertiesJson,
                request.GeometryJson,
                request.GeometrySrid);

            _events.Add(created);
            _eventsById[normalizedEventId] = created;
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

        if (_redisDb == null && !_allowInMemoryFallback)
        {
            return [];
        }

        if (_redisDb != null && await EnsureRedisAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await QueryRedisAsync(cursor, from, to, effectiveLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Intentional: same no-logger rationale as AppendAsync above — degraded state
                // is exposed via _redisUnavailable/IsUsingInMemoryFallback rather than logged here.
                _redisUnavailable = true;
                if (!_allowInMemoryFallback)
                {
                    return [];
                }
            }
        }

        if (!_allowInMemoryFallback)
        {
            return [];
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

    public async Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_redisDb != null && await EnsureRedisAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // Highest cursor = the head of the stream. With Order.Descending the set is
                // walked high→low, so rank 0 is the max score; (-1,-1) would instead return
                // the *lowest* cursor, which made a fresh Redis-backed stream connection
                // replay the entire retained history rather than resume from "now" (#2428).
                var members = await _redisDb.SortedSetRangeByRankAsync(IndexKey, 0, 0, Order.Descending).ConfigureAwait(false);
                if (members.Length == 0)
                {
                    return 0;
                }

                if (!long.TryParse(members[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cursor))
                {
                    return 0;
                }

                ObserveRedisCursor(cursor);
                return cursor;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Intentional: same no-logger rationale as AppendAsync above — degraded state
                // is exposed via _redisUnavailable/IsUsingInMemoryFallback rather than logged here.
                _redisUnavailable = true;
                if (!_allowInMemoryFallback)
                {
                    return 0;
                }
            }
        }

        lock (_sync)
        {
            return _events.Count == 0 ? 0 : _events[^1].Cursor;
        }
    }

    public async Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_redisDb != null && await EnsureRedisAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await GetOldestRetainedRedisCursorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Intentional: same no-logger rationale as GetCurrentCursorAsync above.
                _redisUnavailable = true;
                if (!_allowInMemoryFallback)
                {
                    return 0;
                }
            }
        }

        lock (_sync)
        {
            return _events.Count == 0 ? 0 : _events[0].Cursor;
        }
    }

    /// <summary>
    /// Walks the index from the low end and returns the lowest cursor whose event payload is
    /// still present, pruning index members whose payload key has already expired.
    /// </summary>
    /// <remarks>
    /// Each <c>featurechange:event:*</c> key carries its own TTL, but the matching sorted-set
    /// member is only removed by <see cref="TrimRedisAsync"/> when the index exceeds
    /// <c>MaxRetainedEvents</c>. On a low-volume stream the members therefore outlive their
    /// payloads, and rank 0 can name an event that is no longer retained. Reporting that
    /// cursor as "oldest retained" would make an expired resume cursor look replayable, and
    /// leaving the tombstones in place also lets a replay batch fill entirely with stale
    /// members and return empty before reaching live events. Pruning here fixes both.
    /// </remarks>
    private async Task<long> GetOldestRetainedRedisCursorAsync(CancellationToken cancellationToken)
    {
        const int BatchSize = 128;
        const int MaxExamined = 4096;

        // Bound the sweep so one call can never walk an arbitrarily large tombstone run;
        // each call prunes what it examined, so successive calls make progress.
        var remaining = Math.Clamp(_maxRetained, BatchSize, MaxExamined);
        long highestPruned = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Always rank 0..N-1: pruned members are removed, so the window slides forward.
            var members = await _redisDb!.SortedSetRangeByRankAsync(IndexKey, 0, BatchSize - 1, Order.Ascending).ConfigureAwait(false);
            if (members.Length == 0)
            {
                return 0;
            }

            remaining -= members.Length;

            var existence = new Task<bool>[members.Length];
            var cursors = new long[members.Length];
            for (var i = 0; i < members.Length; i++)
            {
                cursors[i] = long.TryParse(members[i].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : -1;
                existence[i] = cursors[i] < 0
                    ? Task.FromResult(false)
                    : _redisDb.KeyExistsAsync(GetEventKey(cursors[i]));
            }

            var exists = await Task.WhenAll(existence).ConfigureAwait(false);

            var stale = new List<RedisValue>(members.Length);
            for (var i = 0; i < members.Length; i++)
            {
                if (exists[i])
                {
                    if (stale.Count > 0)
                    {
                        await _redisDb.SortedSetRemoveAsync(IndexKey, [.. stale]).ConfigureAwait(false);
                    }

                    return cursors[i];
                }

                stale.Add(members[i]);
                if (cursors[i] > highestPruned)
                {
                    highestPruned = cursors[i];
                }
            }

            await _redisDb.SortedSetRemoveAsync(IndexKey, [.. stale]).ConfigureAwait(false);

            if (members.Length < BatchSize)
            {
                // The whole index was tombstones: nothing is retained.
                return 0;
            }
        }

        // Sweep bound reached without finding a live payload. Everything examined is gone,
        // so the true oldest retained cursor is at least one past the highest pruned member
        // — a lower bound is still sound for the "can this resume cursor be replayed?" test.
        return highestPruned + 1;
    }

    private async Task<FeatureChangeEvent> AppendWithRedisAsync(
        string eventId,
        string sourceId,
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string requestId,
        DateTimeOffset? timestamp,
        Dictionary<string, object?>? changedAttributes,
        bool geometryChanged,
        double[]? geometryEnvelope,
        string? propertiesJson,
        string? geometryJson,
        int? geometrySrid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Highest cursor this node has handed out (Redis or in-memory fallback). Passing it
        // to the append script lets Redis skip past cursors assigned while it was down, so
        // recovered appends never reuse cursor values consumers already advanced beyond.
        long localCursorFloor;
        lock (_sync)
        {
            localCursorFloor = Math.Max(_nextCursor - 1, Volatile.Read(ref _lastObservedRedisCursor));
        }

        var cursorResult = await _redisDb!.ScriptEvaluateAsync(
                AppendEventScript,
                [CursorKey, IndexKey, $"{EventIdKeyPrefix}{eventId}"],
                [
                    eventId,
                    (timestamp ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                    sourceId,
                    serviceId,
                    layerId,
                    objectId,
                    operation,
                    protocol,
                    requestId,
                    geometryChanged ? "1" : "0",
                    changedAttributes == null ? string.Empty : JsonSerializer.Serialize(changedAttributes, FeatureChangeEventsJsonContext.Default.DictionaryStringObject),
                    geometryEnvelope == null ? string.Empty : JsonSerializer.Serialize(geometryEnvelope, FeatureChangeEventsJsonContext.Default.DoubleArray),
                    propertiesJson ?? string.Empty,
                    geometryJson ?? string.Empty,
                    geometrySrid.HasValue ? geometrySrid.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    EventKeyPrefix,
                    (long)Retention.TotalSeconds,
                    localCursorFloor
                ])
            .ConfigureAwait(false);
        var resultArray = (RedisResult[])cursorResult!;
        var cursor = long.Parse(resultArray[0].ToString()!, CultureInfo.InvariantCulture);
        ObserveRedisCursor(cursor);
        var created = await TryGetRedisEventAsync(cursor).ConfigureAwait(false)
            ?? CreateEvent(
                eventId,
                cursor,
                sourceId,
                serviceId,
                layerId,
                objectId,
                operation,
                protocol,
                requestId,
                timestamp,
                changedAttributes,
                geometryChanged,
                geometryEnvelope,
                propertiesJson,
                geometryJson,
                geometrySrid);
        await TrimRedisAsync(cancellationToken).ConfigureAwait(false);
        _redisUnavailable = false;
        return created;
    }

    private async Task<IReadOnlyList<FeatureChangeEvent>> QueryRedisAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        // Bounded rescan: a whole batch can be consumed by tombstoned members (the event key
        // expired while its index member survived) or by events outside the from/to window.
        // Returning empty in that case would stop replay short of live events sitting just
        // past the batch, so advance beyond the examined range and retry a bounded number of
        // times instead of reporting "no more events".
        const int MaxPasses = 4;

        var minimumCursor = (cursor ?? 0) + 1;
        var take = Math.Max(limit * 4, limit);
        var results = new List<FeatureChangeEvent>(limit);

        for (var pass = 0; pass < MaxPasses && results.Count < limit; pass++)
        {
            var members = await _redisDb!.SortedSetRangeByScoreAsync(
                    IndexKey,
                    minimumCursor,
                    double.PositiveInfinity,
                    Exclude.None,
                    Order.Ascending,
                    take: take)
                .ConfigureAwait(false);

            if (members.Length == 0)
            {
                break;
            }

            var lastExamined = minimumCursor - 1;
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!long.TryParse(member.ToString(), out var eventCursor))
                {
                    continue;
                }

                lastExamined = eventCursor;
                ObserveRedisCursor(eventCursor);
                var featureEvent = await TryGetRedisEventAsync(eventCursor).ConfigureAwait(false);
                if (featureEvent == null)
                {
                    await _redisDb.SortedSetRemoveAsync(IndexKey, member).ConfigureAwait(false);
                    continue;
                }
                if (!MatchesWindow(featureEvent, from, to))
                {
                    continue;
                }

                results.Add(featureEvent);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            if (members.Length < take)
            {
                // Partial batch: the index is exhausted, nothing further to scan.
                break;
            }

            minimumCursor = lastExamined + 1;
        }

        return results;
    }

    private void ObserveRedisCursor(long cursor)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastObservedRedisCursor);
            if (cursor <= current ||
                Interlocked.CompareExchange(ref _lastObservedRedisCursor, cursor, current) == current)
            {
                return;
            }
        }
    }

    private async Task<bool> EnsureRedisAvailableAsync(CancellationToken cancellationToken)
    {
        if (_redisDb == null)
        {
            return false;
        }

        if (!_redisUnavailable)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _redisDb.PingAsync().ConfigureAwait(false);
            _redisUnavailable = false;
            return true;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentional: same no-logger rationale as AppendAsync above — a failed ping
            // just keeps _redisUnavailable set so the next call retries via EnsureRedisAvailableAsync.
            _redisUnavailable = true;
            return false;
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
        var staleCursors = staleMembers
            .Select(member => long.TryParse(member.ToString(), out var parsed) ? parsed : (long?)null)
            .Where(parsed => parsed.HasValue)
            .Select(parsed => parsed!.Value);
        foreach (var cursor in staleCursors)
        {
            var featureEvent = await TryGetRedisEventAsync(cursor).ConfigureAwait(false);
            if (featureEvent != null)
            {
                await _redisDb.KeyDeleteAsync($"{EventIdKeyPrefix}{featureEvent.EventId}").ConfigureAwait(false);
            }

            await _redisDb.KeyDeleteAsync(GetEventKey(cursor)).ConfigureAwait(false);
        }

        if (staleMembers.Length > 0)
        {
            await _redisDb.SortedSetRemoveAsync(IndexKey, staleMembers).ConfigureAwait(false);
        }
    }

    private static FeatureChangeEvent CreateEvent(
        string eventId,
        long cursor,
        string sourceId,
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string requestId,
        DateTimeOffset? timestamp,
        Dictionary<string, object?>? changedAttributes = null,
        bool geometryChanged = false,
        double[]? geometryEnvelope = null,
        string? propertiesJson = null,
        string? geometryJson = null,
        int? geometrySrid = null)
        => new()
        {
            EventId = eventId,
            Cursor = cursor,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            SourceId = sourceId,
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = objectId,
            Operation = operation,
            Protocol = protocol,
            RequestId = requestId,
            ChangedAttributes = changedAttributes,
            GeometryChanged = geometryChanged,
            GeometryEnvelope = geometryEnvelope,
            PropertiesJson = propertiesJson,
            GeometryJson = geometryJson,
            GeometrySrid = geometrySrid
        };

    private void TrimIfNeeded()
    {
        if (_events.Count <= _maxRetained)
        {
            return;
        }

        var removeCount = _events.Count - _maxRetained;
        foreach (var removed in _events.Take(removeCount))
        {
            _eventsById.Remove(removed.EventId);
        }

        _events.RemoveRange(0, removeCount);
    }

    private static bool MatchesWindow(FeatureChangeEvent featureEvent, DateTimeOffset? from, DateTimeOffset? to)
        => (!from.HasValue || featureEvent.Timestamp >= from.Value)
           && (!to.HasValue || featureEvent.Timestamp <= to.Value);

    private async Task<FeatureChangeEvent?> TryGetRedisEventAsync(long cursor)
    {
        var json = await _redisDb!.StringGetAsync(GetEventKey(cursor)).ConfigureAwait(false);
        if (!json.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize(json.ToString()!, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
    }

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
            "create" or "insert" => "insert",
            "update" or "delete" => normalized,
            _ => "update"
        };
    }

    public void Dispose()
    {
    }
}
