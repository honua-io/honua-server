// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis-backed append-only store for structured execution log entries.
/// Uses a Redis list per operation for chronological ordering.
/// </summary>
internal sealed partial class RedisExecutionLogStore(
    IConnectionMultiplexer redis,
    ILogger<RedisExecutionLogStore> logger) : IExecutionLogStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 500;
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task AppendAsync(
        string operationId,
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(entry, ControlPlaneJsonContext.Default.ExecutionLogEntry);
        var key = GetLogKey(operationId);
        await _database.ListRightPushAsync(key, payload).ConfigureAwait(false);

        // Ensure the key has a TTL so logs don't persist indefinitely if
        // SetRetentionAsync is never called.
        var ttl = await _database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
        if (!ttl.HasValue)
        {
            await _database.KeyExpireAsync(key, DefaultRetention).ConfigureAwait(false);
        }

        Log.LogEntryAppended(logger, operationId, entry.Level.ToString());
    }

    public async Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await _database.ListRangeAsync(GetLogKey(operationId)).ConfigureAwait(false);
        var entries = new List<ExecutionLogEntry>(values.Length);

        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize(value.ToString(), ControlPlaneJsonContext.Default.ExecutionLogEntry);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task<ExecutionLogTail> TailAsync(
        string operationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var boundedLimit = Math.Clamp(limit, MinQueryLimit, MaxQueryLimit);
        var key = GetLogKey(operationId);
        var length = await _database.ListLengthAsync(key).ConfigureAwait(false);
        if (length <= 0)
        {
            return new ExecutionLogTail { Items = [], TotalCount = 0 };
        }

        var start = Math.Max(0, length - boundedLimit);
        var values = await _database.ListRangeAsync(key, start, -1).ConfigureAwait(false);
        var entries = new List<ExecutionLogEntry>(values.Length);

        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize(value.ToString(), ControlPlaneJsonContext.Default.ExecutionLogEntry);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        return new ExecutionLogTail
        {
            Items = entries,
            TotalCount = checked((int)Math.Min(length, int.MaxValue))
        };
    }

    public async Task<ExecutionLogPage> QueryAsync(
        string operationId,
        ExecutionLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryDecodeOffsetCursor(query.Cursor, out var offset, out var cursorError))
        {
            throw new ArgumentException(cursorError, nameof(query));
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit);
        var values = await _database.ListRangeAsync(
                GetLogKey(operationId),
                offset,
                offset + limit)
            .ConfigureAwait(false);

        var count = Math.Min(values.Length, limit);
        var entries = new List<ExecutionLogEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            if (!value.HasValue)
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize(value.ToString(), ControlPlaneJsonContext.Default.ExecutionLogEntry);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        return new ExecutionLogPage
        {
            Items = entries,
            NextCursor = values.Length > limit ? EncodeOffsetCursor(offset + limit) : null
        };
    }

    public async Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.KeyExpireAsync(GetLogKey(operationId), ttl).ConfigureAwait(false);
    }

    private static string GetLogKey(string operationId) => $"controlplane:job:log:{operationId}";

    private static bool TryDecodeOffsetCursor(string? cursor, out long offset, out string error)
    {
        offset = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                error = "Invalid log cursor.";
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            error = "Invalid log cursor.";
            return false;
        }
    }

    private static string EncodeOffsetCursor(long offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Debug, "Execution log entry appended: {OperationId}, Level={Level}")]
        public static partial void LogEntryAppended(ILogger logger, string operationId, string level);
    }
}
