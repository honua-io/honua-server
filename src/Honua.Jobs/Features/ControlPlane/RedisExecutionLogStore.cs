// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.ControlPlane;

internal sealed partial class RedisExecutionLogStore(
    IConnectionMultiplexer redis,
    ILogger<RedisExecutionLogStore> logger) : IExecutionLogStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 500;
    private readonly IDatabase _database = redis.GetDatabase();

    internal const int MaxStoredEntries = ExecutionLogEntryBounds.MaxStoredEntries;
    internal const int MaxEntryBytes = ExecutionLogEntryBounds.MaxEntryBytes;
    private const string AppendScript = """
        local length = redis.call('LLEN', KEYS[1])
        if length == 0 then redis.call('DEL', KEYS[2]) end
        if ARGV[4] ~= '' and redis.call('HEXISTS', KEYS[2], 'event:' .. ARGV[4]) == 1 then return 0 end
        if ARGV[4] ~= '' and redis.call('HLEN', KEYS[2]) >= 8194 then return redis.error_reply('Execution event identity limit exceeded') end
        local total = tonumber(redis.call('HGET', KEYS[2], 'total') or length)
        redis.call('RPUSH', KEYS[1], ARGV[1])
        local dropped = math.max(0, length + 1 - tonumber(ARGV[2]))
        if dropped > 0 then redis.call('LTRIM', KEYS[1], dropped, -1) end
        redis.call('HINCRBY', KEYS[2], 'offset', dropped)
        redis.call('HSET', KEYS[2], 'total', total + 1)
        if ARGV[4] ~= '' then redis.call('HSET', KEYS[2], 'event:' .. ARGV[4], '1') end
        if redis.call('TTL', KEYS[1]) < 0 then redis.call('EXPIRE', KEYS[1], ARGV[3]) end
        if redis.call('TTL', KEYS[2]) < 0 then redis.call('EXPIRE', KEYS[2], ARGV[3]) end
        return 1
        """;
    private const string ReadScript = """
        local length = redis.call('LLEN', KEYS[1])
        local offset = tonumber(redis.call('HGET', KEYS[2], 'offset') or 0)
        local start = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        if start < 0 then start = math.max(offset, offset + length - limit) else start = math.max(offset, start) end
        local values = redis.call('LRANGE', KEYS[1], start - offset, start - offset + limit)
        local result = {offset + length, offset, start}
        for _, value in ipairs(values) do
            if string.len(value) > 4096 then
                value = '{"timestamp":"1970-01-01T00:00:00+00:00","level":2,"message":"Legacy execution log payload omitted: entry limit exceeded."}'
            end
            table.insert(result, value)
        end
        return result
        """;

    public Task AppendAsync(string operationId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => AppendCoreAsync(operationId, string.Empty, entry, cancellationToken);

    public Task AppendOnceAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (eventId.Length > ExecutionLogEntryBounds.MaxEventIdLength)
        {
            throw new ArgumentException("Event identity exceeds the storage limit.", nameof(eventId));
        }
        return AppendCoreAsync(operationId, eventId, entry, cancellationToken);
    }

    private async Task AppendCoreAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = ExecutionLogEntryBounds.SerializeBounded(entry);
        var key = GetLogKey(operationId);
        await _database.ScriptEvaluateAsync(AppendScript, [key, GetStateKey(key)],
            [payload, MaxStoredEntries, (long)DefaultRetention.TotalSeconds, eventId]).ConfigureAwait(false);
        Log.LogEntryAppended(logger, operationId, entry.Level.ToString());
    }

    public async Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(string operationId, CancellationToken cancellationToken = default)
        => (await TailAsync(operationId, MaxQueryLimit, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<ExecutionLogTail> TailAsync(string operationId, int limit, CancellationToken cancellationToken = default)
    {
        var window = await ReadWindowAsync(operationId, -1, Math.Clamp(limit, MinQueryLimit, MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        return new ExecutionLogTail { Items = window.Items, TotalCount = window.Total, DroppedCount = window.Dropped };
    }

    public async Task<ExecutionLogPage> QueryAsync(string operationId, ExecutionLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ExecutionLogCursor.TryDecode(query.Cursor, out var offset))
        {
            throw new ArgumentException(ExecutionLogCursor.InvalidCursorMessage, nameof(query));
        }
        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit);
        var window = await ReadWindowAsync(operationId, offset, limit, cancellationToken).ConfigureAwait(false);
        var covered = window.Start + window.Items.Count;
        var coveredCount = covered >= int.MaxValue ? int.MaxValue : (int)covered;
        return new ExecutionLogPage
        {
            Items = window.Items,
            DroppedCount = window.Dropped,
            NextCursor = window.More ? ExecutionLogCursor.Encode(window.Start + window.Items.Count) : null,
            TotalCount = Math.Max(window.Total, coveredCount)
        };
    }

    private async Task<(IReadOnlyList<ExecutionLogEntry> Items, int Total, int Dropped, long Start, bool More)> ReadWindowAsync(
        string operationId, long offset, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetLogKey(operationId);
        var result = (RedisResult[])(await _database.ScriptEvaluateAsync(ReadScript, [key, GetStateKey(key)], [offset, limit]).ConfigureAwait(false))!;
        var total = (long)result[0];
        var dropped = (long)result[1];
        var start = (long)result[2];
        var items = new List<ExecutionLogEntry>(Math.Min(limit, result.Length - 3));
        foreach (var value in result.Skip(3).Take(limit))
        {
            var entry = JsonSerializer.Deserialize(value.ToString(), ControlPlaneJsonContext.Default.ExecutionLogEntry);
            if (entry is not null)
            {
                items.Add(entry);
            }
        }
        return (items, (int)Math.Min(total, int.MaxValue), (int)Math.Min(dropped, int.MaxValue), start, result.Length - 3 > limit);
    }

    public async Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(_database.KeyExpireAsync(GetLogKey(operationId), ttl),
            _database.KeyExpireAsync(GetStateKey(GetLogKey(operationId)), ttl)).ConfigureAwait(false);
    }

    private static string GetLogKey(string operationId) => $"controlplane:job:log:{operationId}";

    // Preserve existing list keys while choosing a state key in exactly the same Redis
    // Cluster slot. A hash tag containing the full legacy key hashes like that untagged key.
    private static string GetStateKey(string logKey)
    {
        var open = logKey.IndexOf('{');
        var close = open < 0 ? -1 : logKey.IndexOf('}', open + 1);
        return close > open + 1 ? logKey + ":state" : "{" + logKey + "}:state";
    }

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Debug, "Execution log entry appended: {OperationId}, Level={Level}")]
        public static partial void LogEntryAppended(ILogger logger, string operationId, string level);
    }
}
