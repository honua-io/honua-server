// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    public async Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.KeyExpireAsync(GetLogKey(operationId), ttl).ConfigureAwait(false);
    }

    private static string GetLogKey(string operationId) => $"controlplane:job:log:{operationId}";

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Debug, "Execution log entry appended: {OperationId}, Level={Level}")]
        public static partial void LogEntryAppended(ILogger logger, string operationId, string level);
    }
}
