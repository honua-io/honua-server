// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

/// <summary>
/// PostgreSQL completion side of the alert domain audit outbox (#3865).
/// </summary>
internal sealed class PostgresAlertAuditOutbox : IAlertAuditOutbox
{
    private const string Columns =
        "outbox_id, event_id, action, actor, note, details, correlation_id, " +
        "idempotency_key, occurred_at, audit_id, completed_at";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresAlertAuditOutbox(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _table = SchemaSearchPath.QualifyTable("alert_audit_outbox", schemaName);
    }

    public async Task<IReadOnlyList<AlertAuditOutboxEntry>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var sql = $"""
            SELECT {Columns}
            FROM {_table}
            WHERE completed_at IS NULL
              AND (claimed_until IS NULL OR claimed_until < now())
            ORDER BY outbox_id
            LIMIT @limit
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, bounded);

        var entries = new List<AlertAuditOutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(AlertAuditOutboxMapper.Read(reader));
        }

        return entries;
    }

    public async Task<AlertAuditOutboxEntry?> GetAsync(long outboxId, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT {Columns} FROM {_table} WHERE outbox_id = @outbox_id";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? AlertAuditOutboxMapper.Read(reader)
            : null;
    }

    public async Task<bool> TryClaimAsync(
        long outboxId,
        DateTimeOffset claimedUntil,
        CancellationToken cancellationToken = default)
    {
        // A single conditional UPDATE is the fence: exactly one caller can move an
        // unclaimed (or lease-expired) pending intent into a claimed state.
        var sql = $"""
            UPDATE {_table}
            SET claimed_until = @claimed_until
            WHERE outbox_id = @outbox_id
              AND completed_at IS NULL
              AND (claimed_until IS NULL OR claimed_until < now())
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);
        command.Parameters.AddWithValue("claimed_until", NpgsqlDbType.TimestampTz, claimedUntil);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteAsync(
        long outboxId,
        string auditId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditId);

        // The completed_at IS NULL predicate is the fence: two workers racing to
        // complete the same intent cannot both win, so a crash-recovered intent
        // can never yield a second domain audit action.
        var sql = $"""
            UPDATE {_table}
            SET audit_id = @audit_id, completed_at = @completed_at, last_error = NULL
            WHERE outbox_id = @outbox_id AND completed_at IS NULL
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);
        command.Parameters.AddWithValue("audit_id", NpgsqlDbType.Text, auditId);
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, completedAt);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task RecordAttemptFailureAsync(
        long outboxId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_table}
            SET attempts = attempts + 1, last_error = @last_error, claimed_until = NULL
            WHERE outbox_id = @outbox_id AND completed_at IS NULL
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);
        command.Parameters.AddWithValue("last_error", NpgsqlDbType.Text, Truncate(error, 512));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max];
}

/// <summary>Shared reader projection for <c>honua.alert_audit_outbox</c> rows.</summary>
internal static class AlertAuditOutboxMapper
{
    /// <summary>Reads one outbox row in the column order declared by the queries above.</summary>
    /// <param name="reader">Positioned reader.</param>
    public static AlertAuditOutboxEntry Read(NpgsqlDataReader reader) => new()
    {
        OutboxId = reader.GetInt64(0),
        EventId = reader.GetInt64(1),
        Action = reader.GetString(2),
        Actor = reader.GetString(3),
        Note = reader.IsDBNull(4) ? null : reader.GetString(4),
        Details = reader.GetString(5),
        CorrelationId = reader.GetString(6),
        IdempotencyKey = reader.GetString(7),
        OccurredAt = reader.GetFieldValue<DateTimeOffset>(8),
        AuditId = reader.IsDBNull(9) ? null : reader.GetString(9),
        CompletedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
    };
}
