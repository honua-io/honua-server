// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

using Honua.Postgres.Features.Infrastructure;
namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertDispatchStore : IAlertDispatchStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAlertDispatchStore> _logger;

    public PostgresAlertDispatchStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAlertDispatchStore> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var channelTypes = AlertOutboxCommands.ChannelDbValues(channels);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(AlertOutboxCommands.EnqueueDispatchSql, connection);
        AlertOutboxCommands.BindEnqueue(command, eventId, channelTypes);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        AlertLog.DispatchEnqueued(_logger, channelTypes.Length, eventId);
    }

    async Task<IReadOnlyList<AlertDispatchItem>> IAlertDispatchStore.ClaimPendingAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await ClaimPendingByChannelAsync(maxCount, now, AlertChannelType.Digest, excludeChannel: true, cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<AlertDispatchItem>> IAlertDispatchStore.ClaimPendingDigestAsync(
        int maxCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
        // status IN (0, 3) claims Pending/Failed rows that are due. The extra
        // status = 1 arm reclaims abandoned in-flight claims: a worker that
        // crashed (or threw) after claiming leaves rows wedged at Processing
        // forever, because nothing else ever re-selects status 1. The claim
        // UPDATE below stamps updated_at, so a Processing row whose updated_at
        // is older than the claim TTL has no live worker and is safe to
        // re-claim (poor man's lease, mirroring the feature-change outbox's
        // claim_expires_at + RecoverExpiredClaimsAsync pattern).
        const string sql = """
            WITH claim AS (
                SELECT dispatch_id
                FROM honua.alert_dispatch
                WHERE (
                    (status IN (0, 3) AND next_attempt_at <= @now)
                    OR (status = 1 AND updated_at < @now - INTERVAL '5 minutes')
                  )
                  AND (
                    @channel_type IS NULL
                    OR (@exclude_channel = true AND channel_type <> @channel_type)
                    OR (@exclude_channel = false AND channel_type = @channel_type)
                  )
                  -- Channel pause: never claim rows for a paused channel. Paused rows
                  -- stay enqueued and become claimable again once the channel resumes.
                  AND channel_type NOT IN (
                    SELECT channel_type FROM honua.alert_channel_state WHERE is_paused = true
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        AlertLog.DispatchClaimed(_logger, rows.Count);
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        string? errorMessage,
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("dispatch_id", NpgsqlDbType.Bigint, dispatchId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, deadLetter ? AlertDispatchStatus.DeadLetter.ToDbValue() : AlertDispatchStatus.Failed.ToDbValue());
        command.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, nextAttemptAt);
        command.Parameters.AddWithValue("attempted_at", NpgsqlDbType.TimestampTz, attemptedAt);
        command.Parameters.AddWithValue("last_error", NpgsqlDbType.Text, (object?)errorMessage ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (deadLetter)
        {
            AlertLog.DispatchDeadLettered(_logger, dispatchId);
        }
        else
        {
            AlertLog.DispatchFailed(_logger, dispatchId, deadLetter);
        }
    }

    public async Task RescheduleAsync(
        long dispatchId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        // Rate-cap deferral: return the claimed (Processing) row to Pending with a
        // later next_attempt_at. Attempts are deliberately NOT incremented so the cap
        // never consumes the retry budget or dead-letters a deliverable notification.
        const string sql = """
            UPDATE honua.alert_dispatch
            SET status = 0,
                next_attempt_at = @next_attempt_at,
                updated_at = now()
            WHERE dispatch_id = @dispatch_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("dispatch_id", NpgsqlDbType.Bigint, dispatchId);
        command.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, nextAttemptAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlertDispatchBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
    {
        // One grouped scan supplies both the per-channel projection and the aggregate
        // totals folded below. The `status <> 2` predicate lets the planner use the
        // partial active index and skip delivered rows entirely.
        const string sql = """
            SELECT
                d.channel_type,
                COALESCE(s.is_paused, false) AS is_paused,
                COUNT(*) FILTER (WHERE d.status IN (0, 1)) AS pending,
                COUNT(*) FILTER (WHERE d.status = 3) AS retrying,
                COUNT(*) FILTER (WHERE d.status = 4) AS dead_lettered,
                MIN(d.created_at) AS oldest_item_at
            FROM honua.alert_dispatch d
            LEFT JOIN honua.alert_channel_state s ON s.channel_type = d.channel_type
            WHERE d.status <> 2
            GROUP BY d.channel_type, s.is_paused
            ORDER BY d.channel_type
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var channels = new List<AlertDispatchChannelBacklog>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            channels.Add(new AlertDispatchChannelBacklog
            {
                ChannelType = AlertStoreConversions.ToChannelType(reader.GetInt16(0)),
                IsPaused = reader.GetBoolean(1),
                PendingCount = reader.GetInt64(2),
                RetryingCount = reader.GetInt64(3),
                DeadLetteredCount = reader.GetInt64(4),
                OldestItemAt = reader.GetFieldValue<DateTimeOffset>(5),
            });
        }

        return new AlertDispatchBacklog
        {
            PendingCount = channels.Sum(static channel => channel.PendingCount + channel.RetryingCount),
            RetryingCount = channels.Sum(static channel => channel.RetryingCount),
            DeadLetteredCount = channels.Sum(static channel => channel.DeadLetteredCount),
            OldestItemAt = channels.Count == 0 ? null : channels.Min(static channel => channel.OldestItemAt),
            Channels = channels,
        };
    }

    public async Task<int> PurgeDeliveredAsync(
        DateTimeOffset deliveredBefore,
        int batchLimit,
        CancellationToken cancellationToken = default)
    {
        if (batchLimit <= 0)
        {
            return 0;
        }

        // Bounded delete of delivered (status = 2) rows older than the retention
        // cutoff. The ix_alert_dispatch_delivered partial index serves the victim
        // selection; the LIMIT keeps each sweep's transaction short.
        const string sql = """
            WITH victims AS (
                SELECT dispatch_id
                FROM honua.alert_dispatch
                WHERE status = 2 AND delivered_at < @delivered_before
                ORDER BY delivered_at
                LIMIT @limit
            )
            DELETE FROM honua.alert_dispatch d
            USING victims v
            WHERE d.dispatch_id = v.dispatch_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("delivered_before", NpgsqlDbType.TimestampTz, deliveredBefore);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, batchLimit);

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            AlertLog.DispatchDeliveredPurged(_logger, deleted);
        }

        return deleted;
    }

    public async Task<int> RedriveDeadLettersAsync(
        DateTimeOffset now,
        int batchLimit,
        CancellationToken cancellationToken = default)
    {
        if (batchLimit <= 0)
        {
            return 0;
        }

        // Re-enqueue dead-lettered rows (status = 4) for redelivery: return them to
        // Pending (0), reset the attempt counter and last error, and make them due now.
        // Bounded by @limit so one redrive pass stays a short transaction; idempotent
        // because a pass with no dead-letters updates nothing and returns 0.
        const string sql = """
            WITH candidates AS (
                SELECT dispatch_id
                FROM honua.alert_dispatch
                WHERE status = 4
                ORDER BY dispatch_id
                LIMIT @limit
            )
            UPDATE honua.alert_dispatch d
            SET status = 0,
                attempts = 0,
                next_attempt_at = @now,
                last_error = NULL,
                updated_at = now()
            FROM candidates c
            WHERE d.dispatch_id = c.dispatch_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, batchLimit);

        var redriven = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (redriven > 0)
        {
            AlertLog.DispatchDeadLettersRedriven(_logger, redriven);
        }

        return redriven;
    }

    public async Task SetChannelPausedAsync(
        AlertChannelType channel,
        bool paused,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO honua.alert_channel_state (channel_type, is_paused, paused_at, updated_at)
            VALUES (@channel_type, @is_paused, @paused_at, @updated_at)
            ON CONFLICT (channel_type) DO UPDATE
            SET is_paused = EXCLUDED.is_paused,
                paused_at = EXCLUDED.paused_at,
                updated_at = EXCLUDED.updated_at
            """;

        var channelValue = channel.ToDbValue();
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("channel_type", NpgsqlDbType.Smallint, channelValue);
        command.Parameters.AddWithValue("is_paused", NpgsqlDbType.Boolean, paused);
        command.Parameters.AddWithValue("paused_at", NpgsqlDbType.TimestampTz, paused ? now : (object)DBNull.Value);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        AlertLog.DispatchChannelPauseChanged(_logger, channelValue, paused);
    }

    public async Task<IReadOnlyDictionary<AlertChannelType, bool>> GetChannelPauseStatesAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT channel_type, is_paused FROM honua.alert_channel_state";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var states = new Dictionary<AlertChannelType, bool>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states[AlertStoreConversions.ToChannelType(reader.GetInt16(0))] = reader.GetBoolean(1);
        }

        return states;
    }
}
