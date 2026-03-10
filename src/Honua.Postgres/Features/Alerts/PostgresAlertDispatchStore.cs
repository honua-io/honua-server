// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertDispatchStore : IAlertDispatchStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertDispatchStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task EnqueueAsync(
        long eventId,
        ImmutableArray<AlertChannelType> channels,
        CancellationToken cancellationToken = default)
    {
        if (channels.IsDefaultOrEmpty)
        {
            return;
        }

        const string sql = """
            INSERT INTO honua.alert_dispatch (event_id, channel_type, status, attempts, max_attempts, next_attempt_at)
            VALUES (@event_id, @channel_type, 0, 0, 5, now())
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var channel in channels.Distinct())
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
            command.Parameters.AddWithValue("channel_type", NpgsqlDbType.Smallint, channel.ToDbValue());
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await ClaimPendingByChannelAsync(maxCount, now, AlertChannelType.Digest, excludeChannel: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingDigestAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await ClaimPendingByChannelAsync(maxCount, now, AlertChannelType.Digest, excludeChannel: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingByChannelAsync(
        int maxCount,
        DateTimeOffset now,
        AlertChannelType? channelType,
        bool excludeChannel,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH claim AS (
                SELECT dispatch_id
                FROM honua.alert_dispatch
                WHERE status IN (0, 3)
                  AND next_attempt_at <= @now
                  AND (
                    @channel_type IS NULL
                    OR (@exclude_channel = true AND channel_type <> @channel_type)
                    OR (@exclude_channel = false AND channel_type = @channel_type)
                  )
                ORDER BY next_attempt_at, dispatch_id
                FOR UPDATE SKIP LOCKED
                LIMIT @max_count
            )
            UPDATE honua.alert_dispatch d
            SET status = 1,
                updated_at = now()
            FROM claim c
            WHERE d.dispatch_id = c.dispatch_id
            RETURNING d.dispatch_id, d.event_id, d.channel_type, d.destination,
                      d.status, d.attempts, d.max_attempts, d.next_attempt_at
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);
        command.Parameters.AddWithValue(
            "channel_type",
            NpgsqlDbType.Smallint,
            channelType is null ? DBNull.Value : channelType.Value.ToDbValue());
        command.Parameters.AddWithValue("exclude_channel", NpgsqlDbType.Boolean, excludeChannel);

        var rows = new List<AlertDispatchItem>(Math.Max(1, maxCount));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new AlertDispatchItem
                {
                    DispatchId = reader.GetInt64(0),
                    EventId = reader.GetInt64(1),
                    ChannelType = AlertStoreConversions.ToChannelType(reader.GetInt16(2)),
                    Destination = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Status = AlertStoreConversions.ToDispatchStatus(reader.GetInt16(4)),
                    Attempts = reader.GetInt32(5),
                    MaxAttempts = reader.GetInt32(6),
                    NextAttemptAt = reader.GetFieldValue<DateTimeOffset>(7)
                });
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task MarkDeliveredAsync(
        long dispatchId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE honua.alert_dispatch
            SET status = 2,
                delivered_at = @delivered_at,
                last_attempt_at = @delivered_at,
                updated_at = now()
            WHERE dispatch_id = @dispatch_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("dispatch_id", NpgsqlDbType.Bigint, dispatchId);
        command.Parameters.AddWithValue("delivered_at", NpgsqlDbType.TimestampTz, deliveredAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        long dispatchId,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextAttemptAt,
        bool deadLetter,
        string? error,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE honua.alert_dispatch
            SET status = @status,
                attempts = attempts + 1,
                next_attempt_at = @next_attempt_at,
                last_attempt_at = @attempted_at,
                last_error = @last_error,
                updated_at = now()
            WHERE dispatch_id = @dispatch_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("dispatch_id", NpgsqlDbType.Bigint, dispatchId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, deadLetter ? AlertDispatchStatus.DeadLetter.ToDbValue() : AlertDispatchStatus.Failed.ToDbValue());
        command.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, nextAttemptAt);
        command.Parameters.AddWithValue("attempted_at", NpgsqlDbType.TimestampTz, attemptedAt);
        command.Parameters.AddWithValue("last_error", NpgsqlDbType.Text, (object?)error ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
