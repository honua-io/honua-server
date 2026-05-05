// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Postgres implementation of the feature-change outbox. Uses
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c> for safe multi-node claim and
/// partial indexes (defined in migration 024) to keep the dispatcher hot path
/// off the dispatched-row tail.
/// </summary>
internal sealed class PostgresFeatureChangeOutboxRepository : IFeatureChangeOutboxRepository
{
    private const string TableName = "honua.feature_change_outbox";

    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresFeatureChangeOutboxRepository(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task WriteOutboxRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(entry);

        var npgsql = connection.RequireNpgsqlConnection();
        await using var command = BuildInsertCommand(npgsql, entry);
        command.Transaction = (NpgsqlTransaction)transaction;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
        string nodeId,
        int batchSize,
        TimeSpan claimTtl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (batchSize <= 0)
        {
            return Array.Empty<FeatureChangeOutboxEntry>();
        }

        // Atomically claim N rows with FOR UPDATE SKIP LOCKED. The CTE selects
        // candidate rows by id; the outer UPDATE flips status, stamps the lease,
        // and returns the claimed rows. Concurrent dispatchers on other nodes
        // skip locked rows so each row is dispatched exactly once per claim window.
        var sql = $@"
WITH candidate AS (
    SELECT outbox_id
    FROM {TableName}
    WHERE status IN ('pending', 'failed')
    ORDER BY created_at
    FOR UPDATE SKIP LOCKED
    LIMIT @batch_size
)
UPDATE {TableName} AS o
SET status = 'claimed',
    claimed_at = now(),
    claim_node_id = @node_id,
    claim_expires_at = now() + (@ttl_seconds || ' seconds')::interval
FROM candidate
WHERE o.outbox_id = candidate.outbox_id
RETURNING
    o.outbox_id, o.service_id, o.layer_id, o.object_id, o.operation, o.protocol,
    o.source_id, o.request_id, o.event_id, o.event_payload, o.status, o.retry_count,
    o.last_error, o.created_at, o.claimed_at, o.claim_node_id, o.claim_expires_at,
    o.dispatched_at";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, batchSize);
        command.Parameters.AddWithValue("node_id", NpgsqlDbType.Text, nodeId);
        command.Parameters.AddWithValue("ttl_seconds", NpgsqlDbType.Text, ((int)claimTtl.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var results = new List<FeatureChangeOutboxEntry>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    public async Task<bool> MarkDispatchedAsync(Guid outboxId, string ownerNodeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerNodeId);

        // Bind the terminal update to the active claim owner. After RecoverExpiredClaimsAsync
        // resets a stalled lease, a different worker may have re-claimed the row; this guard
        // makes the stale worker's MarkDispatched a no-op so it cannot overwrite the new
        // owner's state.
        const string sql = @"
UPDATE honua.feature_change_outbox
SET status = 'dispatched',
    dispatched_at = now(),
    claim_node_id = NULL,
    claim_expires_at = NULL,
    last_error = NULL
WHERE outbox_id = @outbox_id
  AND status = 'claimed'
  AND claim_node_id = @owner_node_id";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Uuid, outboxId);
        command.Parameters.AddWithValue("owner_node_id", NpgsqlDbType.Text, ownerNodeId);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> MarkFailedAsync(
        Guid outboxId,
        string ownerNodeId,
        string errorMessage,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerNodeId);
        ArgumentNullException.ThrowIfNull(errorMessage);

        // Increment retry count atomically only when this node still owns the claim.
        // If the next attempt count would meet or exceed maxRetries, transition to
        // dead_lettered so the dispatcher stops re-claiming and the operator is
        // alerted via the health check. A recovered/reclaimed lease causes 0 rows
        // updated and a stale-claim outcome to the caller.
        const string sql = @"
UPDATE honua.feature_change_outbox
SET status = CASE WHEN retry_count + 1 >= @max_retries THEN 'dead_lettered' ELSE 'failed' END,
    retry_count = retry_count + 1,
    last_error = @error,
    claim_node_id = NULL,
    claim_expires_at = NULL
WHERE outbox_id = @outbox_id
  AND status = 'claimed'
  AND claim_node_id = @owner_node_id";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Uuid, outboxId);
        command.Parameters.AddWithValue("owner_node_id", NpgsqlDbType.Text, ownerNodeId);
        command.Parameters.AddWithValue("max_retries", NpgsqlDbType.Integer, maxRetries);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, Truncate(errorMessage, 4000));
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE honua.feature_change_outbox
SET status = 'pending',
    claim_node_id = NULL,
    claim_expires_at = NULL,
    claimed_at = NULL
WHERE status = 'claimed' AND claim_expires_at < now()";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxBacklogMetrics> GetBacklogMetricsAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
    COUNT(*) FILTER (WHERE status IN ('pending', 'failed', 'claimed'))                            AS pending_count,
    COUNT(*) FILTER (WHERE status = 'dead_lettered')                                              AS dead_lettered_count,
    COALESCE(EXTRACT(EPOCH FROM (now() - MIN(created_at)
        FILTER (WHERE status IN ('pending', 'failed', 'claimed')))), 0)                           AS oldest_pending_seconds
FROM honua.feature_change_outbox";

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new OutboxBacklogMetrics
            {
                PendingCount = 0,
                DeadLetteredCount = 0,
                OldestPendingAgeSeconds = 0
            };
        }

        return new OutboxBacklogMetrics
        {
            PendingCount = reader.GetInt64(0),
            DeadLetteredCount = reader.GetInt64(1),
            OldestPendingAgeSeconds = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static NpgsqlCommand BuildInsertCommand(NpgsqlConnection connection, FeatureChangeOutboxEntry entry)
    {
        const string sql = @"
INSERT INTO honua.feature_change_outbox
    (outbox_id, service_id, layer_id, object_id, operation, protocol, source_id, request_id,
     event_id, event_payload, status, retry_count, created_at)
VALUES
    (@outbox_id, @service_id, @layer_id, @object_id, @operation, @protocol, @source_id, @request_id,
     @event_id, @event_payload::jsonb, @status, @retry_count, @created_at)";

        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Uuid, entry.OutboxId);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, entry.ServiceId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, entry.LayerId);
        command.Parameters.AddWithValue("object_id", NpgsqlDbType.Bigint, entry.ObjectId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, entry.Operation);
        command.Parameters.AddWithValue("protocol", NpgsqlDbType.Text, entry.Protocol);
        command.Parameters.AddWithValue("source_id", NpgsqlDbType.Text, (object?)entry.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, entry.RequestId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, entry.EventId);
        command.Parameters.AddWithValue("event_payload", NpgsqlDbType.Text, entry.EventPayload);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, entry.Status);
        command.Parameters.AddWithValue("retry_count", NpgsqlDbType.Integer, entry.RetryCount);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, entry.CreatedAt);
        return command;
    }

    private static FeatureChangeOutboxEntry ReadEntry(NpgsqlDataReader reader)
    {
        return new FeatureChangeOutboxEntry
        {
            OutboxId = reader.GetGuid(0),
            ServiceId = reader.GetString(1),
            LayerId = reader.GetInt32(2),
            ObjectId = reader.GetInt64(3),
            Operation = reader.GetString(4),
            Protocol = reader.GetString(5),
            SourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
            RequestId = reader.GetString(7),
            EventId = reader.GetString(8),
            EventPayload = reader.GetString(9),
            Status = reader.GetString(10),
            RetryCount = reader.GetInt32(11),
            LastError = reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
            ClaimedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            ClaimNodeId = reader.IsDBNull(15) ? null : reader.GetString(15),
            ClaimExpiresAt = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            DispatchedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        };
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
