// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

/// <summary>
/// PostgreSQL store for the operator lifecycle row attached to an alert event.
/// </summary>
internal sealed class PostgresAlertLifecycleStore : IAlertLifecycleStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _lifecycleTable;
    private readonly string _eventsTable;

    public PostgresAlertLifecycleStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _lifecycleTable = SchemaSearchPath.QualifyTable("alert_event_lifecycle", schemaName);
        _eventsTable = SchemaSearchPath.QualifyTable("alert_events", schemaName);
    }

    public async Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT lifecycle_status, acknowledged_at, acknowledged_by,
                   suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at
            FROM {_lifecycleTable}
            WHERE event_id = @event_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AlertEventLifecycle
        {
            EventId = eventId,
            Status = AlertLifecycleConversions.ToLifecycleStatus(reader.GetInt16(0)),
            AcknowledgedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            AcknowledgedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
            SuppressedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            SuppressedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
            ResolvedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            ResolvedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            Note = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8)
        };
    }

    public Task<AlertEventLifecycle?> AcknowledgeAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            INSERT INTO {_lifecycleTable} (
                event_id, lifecycle_status, acknowledged_at, acknowledged_by, note, updated_at)
            SELECT @event_id, @status, @acknowledged_at, @acknowledged_by, @note, @acknowledged_at
            WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
            ON CONFLICT (event_id) DO UPDATE SET
                lifecycle_status = EXCLUDED.lifecycle_status,
                acknowledged_at  = EXCLUDED.acknowledged_at,
                acknowledged_by  = EXCLUDED.acknowledged_by,
                note             = EXCLUDED.note,
                updated_at       = EXCLUDED.updated_at
            RETURNING lifecycle_status, acknowledged_at, acknowledged_by,
                      suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at
            """;

        return UpsertAsync(
            sql,
            eventId,
            command =>
            {
                command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
                    AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Acknowledged));
                command.Parameters.AddWithValue("acknowledged_at", NpgsqlDbType.TimestampTz, acknowledgedAt);
                command.Parameters.AddWithValue("acknowledged_by", NpgsqlDbType.Text, actor);
                command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
            },
            cancellationToken);
    }

    public Task<AlertEventLifecycle?> SuppressAsync(
        long eventId,
        string actor,
        DateTimeOffset suppressUntil,
        string? note,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            INSERT INTO {_lifecycleTable} (
                event_id, lifecycle_status, suppressed_until, suppressed_by, note, updated_at)
            SELECT @event_id, @status, @suppressed_until, @suppressed_by, @note, @updated_at
            WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
            ON CONFLICT (event_id) DO UPDATE SET
                lifecycle_status = EXCLUDED.lifecycle_status,
                suppressed_until = EXCLUDED.suppressed_until,
                suppressed_by    = EXCLUDED.suppressed_by,
                note             = EXCLUDED.note,
                updated_at       = EXCLUDED.updated_at
            RETURNING lifecycle_status, acknowledged_at, acknowledged_by,
                      suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at
            """;

        return UpsertAsync(
            sql,
            eventId,
            command =>
            {
                command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
                    AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Suppressed));
                command.Parameters.AddWithValue("suppressed_until", NpgsqlDbType.TimestampTz, suppressUntil);
                command.Parameters.AddWithValue("suppressed_by", NpgsqlDbType.Text, actor);
                command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
                command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, suppressedAt);
            },
            cancellationToken);
    }

    public Task<AlertEventLifecycle?> ResolveAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            INSERT INTO {_lifecycleTable} (
                event_id, lifecycle_status, resolved_at, resolved_by, note, updated_at)
            SELECT @event_id, @status, @resolved_at, @resolved_by, @note, @resolved_at
            WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
            ON CONFLICT (event_id) DO UPDATE SET
                lifecycle_status = EXCLUDED.lifecycle_status,
                resolved_at      = EXCLUDED.resolved_at,
                resolved_by      = EXCLUDED.resolved_by,
                note             = EXCLUDED.note,
                updated_at       = EXCLUDED.updated_at
            RETURNING lifecycle_status, acknowledged_at, acknowledged_by,
                      suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at
            """;

        return UpsertAsync(
            sql,
            eventId,
            command =>
            {
                command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
                    AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Resolved));
                command.Parameters.AddWithValue("resolved_at", NpgsqlDbType.TimestampTz, resolvedAt);
                command.Parameters.AddWithValue("resolved_by", NpgsqlDbType.Text, actor);
                command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
            },
            cancellationToken);
    }

    private async Task<AlertEventLifecycle?> UpsertAsync(
        string sql,
        long eventId,
        Action<NpgsqlCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
        bindParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AlertEventLifecycle
        {
            EventId = eventId,
            Status = AlertLifecycleConversions.ToLifecycleStatus(reader.GetInt16(0)),
            AcknowledgedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            AcknowledgedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
            SuppressedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            SuppressedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
            ResolvedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            ResolvedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            Note = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8)
        };
    }
}
