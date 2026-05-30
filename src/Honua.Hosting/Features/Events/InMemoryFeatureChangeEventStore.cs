// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
    private volatile bool _redisUnavailable;

    public bool CanPersistEvents => _redisDb is not null
        ? !_redisUnavailable || _allowInMemoryFallback
        : _allowInMemoryFallback;

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
            catch (Exception ex)
            {
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
            catch
            {
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
                var members = await _redisDb.SortedSetRangeByRankAsync(IndexKey, -1, -1, Order.Descending).ConfigureAwait(false);
                if (members.Length == 0)
                {
                    return 0;
                }

                return long.TryParse(members[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cursor)
                    ? cursor
                    : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
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
                    (long)Retention.TotalSeconds
                ])
            .ConfigureAwait(false);
        var resultArray = (RedisResult[])cursorResult!;
        var cursor = long.Parse(resultArray[0].ToString()!, CultureInfo.InvariantCulture);
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

        return results;
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
        catch
        {
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
        foreach (var member in staleMembers)
        {
            if (long.TryParse(member.ToString(), out var cursor))
            {
                var featureEvent = await TryGetRedisEventAsync(cursor).ConfigureAwait(false);
                if (featureEvent != null)
                {
                    await _redisDb.KeyDeleteAsync($"{EventIdKeyPrefix}{featureEvent.EventId}").ConfigureAwait(false);
                }

                await _redisDb.KeyDeleteAsync(GetEventKey(cursor)).ConfigureAwait(false);
            }
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
