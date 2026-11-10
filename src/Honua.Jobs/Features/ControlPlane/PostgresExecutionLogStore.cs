// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

/// <summary>
/// Job logs in PostgreSQL (ADR-0081). A stream row counts what was appended and dropped; an entry's
/// sequence is its absolute position, so a cursor stays valid when older entries are trimmed.
/// </summary>
internal sealed partial class PostgresExecutionLogStore : IExecutionLogStore
{
    internal const string EventIdentityLimitConstraint = "execution_job_log_streams_event_identity_limit";

    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 500;
    private const long TailStart = -1;

    private readonly JobStoreDatabase _database;
    private readonly JobStoreOptions _options;
    private readonly ILogger<PostgresExecutionLogStore> _logger;
    private readonly string _streams;
    private readonly string _lockStream;
    private readonly string _append;
    private readonly string _readWindow;

    public PostgresExecutionLogStore(
        JobStoreDatabase database,
        IOptions<JobStoreOptions> options,
        ILogger<PostgresExecutionLogStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        _database = database;
        _options = options.Value;
        _logger = logger;
        _streams = database.Table("execution_job_log_streams");
        var entries = database.Table("execution_job_log_entries");
        var eventIdentities = database.Table("execution_job_log_event_identities");

        // Taking the stream's row lock serializes appends to one job. Each append moves the expiry,
        // so a log is kept for the retention period after its last entry until a caller sets one.
        _lockStream = $"""
            INSERT INTO {_streams} (operation_id, expires_at) VALUES (@operation_id, now() + @retention)
            ON CONFLICT (operation_id) DO UPDATE SET expires_at = EXCLUDED.expires_at
            """;

        _append = $"""
            WITH accepted AS (
                INSERT INTO {eventIdentities} (operation_id, event_id)
                SELECT @operation_id, @event_id WHERE @event_id IS NOT NULL
                ON CONFLICT DO NOTHING
                RETURNING 1),
            advanced AS (
                UPDATE {_streams}
                SET appended_count = appended_count + 1,
                    dropped_count = GREATEST(dropped_count, appended_count + 1 - @max_entries),
                    event_identity_count = event_identity_count + (SELECT count(*) FROM accepted)::int
                WHERE operation_id = @operation_id AND (@event_id IS NULL OR EXISTS (SELECT 1 FROM accepted))
                RETURNING appended_count - 1 AS sequence, dropped_count),
            inserted AS (
                INSERT INTO {entries} (operation_id, sequence, entry)
                SELECT @operation_id, sequence, @entry FROM advanced
                RETURNING 1)
            DELETE FROM {entries} AS entry USING advanced
            WHERE entry.operation_id = @operation_id AND entry.sequence < advanced.dropped_count
            """;

        _readWindow = $"""
            WITH stream AS (
                SELECT appended_count, dropped_count,
                       CASE WHEN @start < 0 THEN GREATEST(dropped_count, appended_count - @limit)
                            ELSE GREATEST(dropped_count, @start) END AS window_start
                FROM {_streams}
                WHERE operation_id = @operation_id)
            SELECT stream.appended_count, stream.dropped_count, stream.window_start, entry.entry
            FROM stream
            LEFT JOIN LATERAL (
                SELECT sequence, entry FROM {entries}
                WHERE operation_id = @operation_id AND sequence >= stream.window_start
                ORDER BY sequence
                LIMIT @limit + 1) AS entry ON TRUE
            ORDER BY entry.sequence
            """;
    }

    public Task AppendAsync(string operationId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => AppendCoreAsync(operationId, eventId: null, entry, cancellationToken);

    public Task AppendOnceAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (eventId.Length > ExecutionLogEntryBounds.MaxEventIdLength)
        {
            throw new ArgumentException("Event identity exceeds the storage limit.", nameof(eventId));
        }

        return AppendCoreAsync(operationId, eventId, entry, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(string operationId, CancellationToken cancellationToken = default)
        => (await TailAsync(operationId, MaxQueryLimit, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<ExecutionLogTail> TailAsync(string operationId, int limit, CancellationToken cancellationToken = default)
    {
        var window = await ReadWindowAsync(
            operationId, TailStart, Math.Clamp(limit, MinQueryLimit, MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        return new ExecutionLogTail
        {
            Items = window.Items,
            TotalCount = ClampToInt(window.AppendedCount),
            DroppedCount = ClampToInt(window.DroppedCount)
        };
    }

    public async Task<ExecutionLogPage> QueryAsync(string operationId, ExecutionLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ExecutionLogCursor.TryDecode(query.Cursor, out var start))
        {
            throw new ArgumentException(ExecutionLogCursor.InvalidCursorMessage, nameof(query));
        }

        var window = await ReadWindowAsync(
            operationId, start, Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit), cancellationToken).ConfigureAwait(false);
        var nextPosition = window.Start + window.Items.Count;
        return new ExecutionLogPage
        {
            Items = window.Items,
            DroppedCount = ClampToInt(window.DroppedCount),
            NextCursor = window.HasMore ? ExecutionLogCursor.Encode(nextPosition) : null,
            TotalCount = Math.Max(ClampToInt(window.AppendedCount), ClampToInt(nextPosition))
        };
    }

    public Task SetRetentionAsync(string operationId, TimeSpan ttl, CancellationToken cancellationToken = default)
        => _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"UPDATE {_streams} SET expires_at = now() + @retention WHERE operation_id = @operation_id",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = ttl });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private async Task AppendCoreAsync(string operationId, string? eventId, ExecutionLogEntry entry, CancellationToken cancellationToken)
    {
        var payload = ExecutionLogEntryBounds.SerializeBounded(entry);
        try
        {
            await _database.ExecuteAsync(
                async (connection, token) =>
                {
                    // One batch is one round trip and one implicit transaction.
                    await using var batch = new NpgsqlBatch(connection);
                    var lockStream = StreamCommand(_lockStream, operationId);
                    lockStream.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = _options.LogRetention });
                    batch.BatchCommands.Add(lockStream);
                    var append = StreamCommand(_append, operationId);
                    append.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = (object?)eventId ?? DBNull.Value });
                    append.Parameters.Add(new NpgsqlParameter("entry", NpgsqlDbType.Json) { Value = payload });
                    append.Parameters.Add(new NpgsqlParameter("max_entries", NpgsqlDbType.Bigint) { Value = (long)ExecutionLogEntryBounds.MaxStoredEntries });
                    batch.BatchCommands.Add(append);
                    await batch.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                eventId is null ? StatementRepeat.NotAfterSend : StatementRepeat.SafeToRepeat,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.CheckViolation
            && string.Equals(exception.ConstraintName, EventIdentityLimitConstraint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Execution event identity limit exceeded.", exception);
        }

        Log.LogEntryAppended(_logger, operationId, entry.Level.ToString());
    }

    private async Task<LogWindow> ReadWindowAsync(string operationId, long start, int limit, CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(_readWindow, connection);
                command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
                command.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.Bigint) { Value = start });
                command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Bigint) { Value = (long)limit });

                long appendedCount = 0;
                long droppedCount = 0;
                var windowStart = Math.Max(start, 0);
                var items = new List<ExecutionLogEntry>(limit);
                var hasMore = false;
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    appendedCount = reader.GetInt64(0);
                    droppedCount = reader.GetInt64(1);
                    windowStart = reader.GetInt64(2);
                    if (reader.IsDBNull(3))
                    {
                        continue;
                    }

                    if (items.Count == limit)
                    {
                        hasMore = true;
                        break;
                    }

                    var entry = JsonSerializer.Deserialize(reader.GetString(3), ControlPlaneJsonContext.Default.ExecutionLogEntry);
                    if (entry is not null)
                    {
                        items.Add(entry);
                    }
                }

                return new LogWindow(items, appendedCount, droppedCount, windowStart, hasMore);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    private static NpgsqlBatchCommand StreamCommand(string statement, string operationId)
    {
        var command = new NpgsqlBatchCommand(statement);
        command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = operationId });
        return command;
    }

    private static int ClampToInt(long value) => (int)Math.Min(value, int.MaxValue);

    private sealed record LogWindow(
        IReadOnlyList<ExecutionLogEntry> Items,
        long AppendedCount,
        long DroppedCount,
        long Start,
        bool HasMore);

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Debug, "Execution log entry appended: {OperationId}, Level={Level}")]
        public static partial void LogEntryAppended(ILogger logger, string operationId, string level);
    }
}
