// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.AuditLog;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

/// <summary>Atomic operator transitions with a durable, hash-chained domain receipt.</summary>
internal sealed class PostgresAlertLifecycleMutationStore(
    IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null) : IAlertLifecycleMutationStore
{
    private readonly string _events = SchemaSearchPath.QualifyTable("alert_events", schemaName);
    private readonly string _lifecycle = SchemaSearchPath.QualifyTable("alert_event_lifecycle", schemaName);
    private readonly string _audit = SchemaSearchPath.QualifyTable("audit_log", schemaName);

    public async Task<AlertEventLifecycle?> MutateAsync(long eventId, string? note,
        DateTimeOffset? suppressUntil, AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var status = auditEvent.Action switch
        {
            "alert.acknowledge" => AlertLifecycleStatus.Acknowledged,
            "alert.suppress" => AlertLifecycleStatus.Suppressed,
            "alert.resolve" => AlertLifecycleStatus.Resolved,
            _ => throw new ArgumentException("Unsupported alert action.", nameof(auditEvent))
        };
        if (auditEvent.Outcome != AuditOutcome.Success || auditEvent.EventType != AuditEventType.AdminAction ||
            auditEvent.ResourceType != "alert_event" || auditEvent.ResourceId != eventId.ToString(CultureInfo.InvariantCulture) ||
            string.IsNullOrWhiteSpace(auditEvent.Actor) || auditEvent.Actor.Length > 256 ||
            string.IsNullOrWhiteSpace(auditEvent.CorrelationId) || auditEvent.CorrelationId.Length > 64 ||
            note is { Length: > 1024 } || (status == AlertLifecycleStatus.Suppressed) != suppressUntil.HasValue)
        {
            throw new ArgumentException("Invalid alert operation identity or details.", nameof(auditEvent));
        }

        var details = BuildDetails(note, suppressUntil);
        auditEvent = auditEvent with { Details = details };
        await using var lease = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Acquire the shared audit-chain lock BEFORE touching lifecycle rows. This also
        // serializes retries across hosts, and avoids opposite lifecycle/audit lock order.
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", PostgresAuditLog.HashChainAdvisoryLockKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand($"""
            SELECT timestamp, details FROM {_audit}
            WHERE actor = @actor AND correlation_id = @correlation AND action = @action
              AND resource_type = 'alert_event' AND resource_id = @event AND outcome = 'Success'
            ORDER BY audit_id DESC LIMIT 1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("actor", auditEvent.Actor);
            command.Parameters.AddWithValue("correlation", auditEvent.CorrelationId);
            command.Parameters.AddWithValue("action", auditEvent.Action);
            command.Parameters.AddWithValue("event", auditEvent.ResourceId!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(1), details, StringComparison.Ordinal))
                {
                    throw new AlertLifecycleRetryConflictException();
                }

                // Return the original operation's timestamp, even if a later operation
                // has changed the current lifecycle. A retry never reapplies old state.
                return Result(reader.GetFieldValue<DateTimeOffset>(0));
            }
        }

        if (suppressUntil.HasValue && suppressUntil <= auditEvent.Timestamp)
        {
            throw new ArgumentException("Suppression must end after the operation timestamp.", nameof(suppressUntil));
        }

        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_lifecycle} (event_id, lifecycle_status, acknowledged_at, acknowledged_by,
                suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at)
            SELECT @event, @status, @ack_at, @ack_by, @until, @suppress_by, @resolve_at, @resolve_by, @note, @at
            WHERE EXISTS (SELECT 1 FROM {_events} WHERE event_id = @event)
            ON CONFLICT (event_id) DO UPDATE SET
                lifecycle_status = EXCLUDED.lifecycle_status, acknowledged_at = EXCLUDED.acknowledged_at,
                acknowledged_by = EXCLUDED.acknowledged_by, suppressed_until = EXCLUDED.suppressed_until,
                suppressed_by = EXCLUDED.suppressed_by, resolved_at = EXCLUDED.resolved_at,
                resolved_by = EXCLUDED.resolved_by, note = EXCLUDED.note, updated_at = EXCLUDED.updated_at
            """, connection, transaction))
        {
            var result = Result(auditEvent.Timestamp);
            command.Parameters.AddWithValue("event", eventId);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, AlertLifecycleConversions.ToDbValue(status));
            command.Parameters.AddWithValue("ack_at", NpgsqlDbType.TimestampTz, (object?)result.AcknowledgedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("ack_by", NpgsqlDbType.Text, (object?)result.AcknowledgedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, (object?)suppressUntil ?? DBNull.Value);
            command.Parameters.AddWithValue("suppress_by", NpgsqlDbType.Text, (object?)result.SuppressedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("resolve_at", NpgsqlDbType.TimestampTz, (object?)result.ResolvedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("resolve_by", NpgsqlDbType.Text, (object?)result.ResolvedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
            command.Parameters.AddWithValue("at", auditEvent.Timestamp);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }
        }

        var auditId = await PostgresAuditLog.RecordInTransactionAsync(connection, transaction, auditEvent, _audit, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(auditId))
        {
            throw new InvalidOperationException("The alert domain audit did not produce a durable identity.");
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return Result(auditEvent.Timestamp);

        AlertEventLifecycle Result(DateTimeOffset timestamp) => new()
        {
            EventId = eventId, Status = status, Note = note, UpdatedAt = timestamp,
            AcknowledgedAt = status == AlertLifecycleStatus.Acknowledged ? timestamp : null,
            AcknowledgedBy = status == AlertLifecycleStatus.Acknowledged ? auditEvent.Actor : null,
            SuppressedUntil = suppressUntil,
            SuppressedBy = status == AlertLifecycleStatus.Suppressed ? auditEvent.Actor : null,
            ResolvedAt = status == AlertLifecycleStatus.Resolved ? timestamp : null,
            ResolvedBy = status == AlertLifecycleStatus.Resolved ? auditEvent.Actor : null
        };
    }

    private static string BuildDetails(string? note, DateTimeOffset? suppressUntil)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("note", note);
            if (suppressUntil.HasValue) { writer.WriteString("suppressUntil", suppressUntil.Value.ToUniversalTime()); }
            else { writer.WriteNull("suppressUntil"); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
