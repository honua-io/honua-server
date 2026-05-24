// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

/// <summary>
/// PostgreSQL read model for alert events plus operator lifecycle state (#1168).
/// </summary>
internal sealed class PostgresAlertEventQuery : IAlertEventQuery
{
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;
    internal const int DefaultPageSize = 50;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _eventsTable;
    private readonly string _lifecycleTable;
    private readonly string _rulesTable;

    public PostgresAlertEventQuery(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _eventsTable = SchemaSearchPath.QualifyTable("alert_events", schemaName);
        _lifecycleTable = SchemaSearchPath.QualifyTable("alert_event_lifecycle", schemaName);
        _rulesTable = SchemaSearchPath.QualifyTable("alert_rules", schemaName);
    }

    public async Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pageSize = ClampPageSize(filter.PageSize);
        var cursor = AlertEventCursor.TryDecode(filter.Cursor);

        var sql = new StringBuilder();
        sql.Append("SELECT e.event_id, e.rule_id, r.rule_name, e.zone_id, e.service_id, e.layer_id, e.objectid,");
        sql.Append(" e.trigger_type, e.severity, e.occurred_at, e.incident_status, e.incident_duration_ms,");
        sql.Append(" COALESCE(l.lifecycle_status, 0) AS lifecycle_status,");
        sql.Append(" l.acknowledged_at, l.acknowledged_by, l.suppressed_until, l.resolved_at, l.resolved_by");
        sql.Append(" FROM ").Append(_eventsTable).Append(" e");
        sql.Append(" LEFT JOIN ").Append(_lifecycleTable).Append(" l ON l.event_id = e.event_id");
        sql.Append(" LEFT JOIN ").Append(_rulesTable).Append(" r ON r.rule_id = e.rule_id");
        sql.Append(" WHERE 1=1");

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };

        AppendTimeRangeFilters(filter, sql, command);
        AppendScopeFilters(filter, sql, command);
        AppendSeverityFilter(filter, sql, command);
        AppendIncidentFilter(filter, sql, command);
        AppendLifecycleFilter(filter, sql, command);
        AppendCursorPredicate(cursor, sql, command);

        sql.Append(" ORDER BY e.occurred_at DESC, e.event_id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        command.CommandText = sql.ToString();

        var rows = new List<AlertEventSummary>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var lastKept = rows[pageSize - 1];
            rows.RemoveAt(rows.Count - 1);
            nextCursor = AlertEventCursor.Encode(lastKept.OccurredAt, lastKept.EventId);
        }

        return new AlertEventPage
        {
            Items = rows,
            NextCursor = nextCursor
        };
    }

    public async Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT e.event_id, e.rule_id, r.rule_name, e.zone_id, e.service_id, e.layer_id, e.objectid,
                   e.trigger_type, e.severity, e.occurred_at, e.incident_status, e.incident_duration_ms,
                   COALESCE(l.lifecycle_status, 0) AS lifecycle_status,
                   l.acknowledged_at, l.acknowledged_by,
                   l.suppressed_until,
                   l.resolved_at, l.resolved_by
            FROM {_eventsTable} e
            LEFT JOIN {_lifecycleTable} l ON l.event_id = e.event_id
            LEFT JOIN {_rulesTable} r ON r.rule_id = e.rule_id
            WHERE e.event_id = @event_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapSummary(reader);
    }

    internal static int ClampPageSize(int requested)
    {
        if (requested <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(MaxPageSize, Math.Max(MinPageSize, requested));
    }

    private static AlertEventSummary MapSummary(NpgsqlDataReader reader)
    {
        return new AlertEventSummary
        {
            EventId = reader.GetInt64(0),
            RuleId = reader.GetInt64(1),
            RuleName = reader.IsDBNull(2) ? null : reader.GetString(2),
            ZoneId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            ServiceId = reader.GetString(4),
            LayerId = reader.GetInt32(5),
            ObjectId = reader.GetInt64(6),
            TriggerType = AlertStoreConversions.ToTriggerType(reader.GetInt16(7)),
            Severity = AlertStoreConversions.ToSeverity(reader.GetString(8)),
            OccurredAt = reader.GetFieldValue<DateTimeOffset>(9),
            IncidentStatus = AlertStoreConversions.ToIncidentStatus(reader.GetInt16(10)),
            IncidentDurationMs = reader.GetInt64(11),
            LifecycleStatus = AlertLifecycleConversions.ToLifecycleStatus(reader.GetInt16(12)),
            AcknowledgedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            AcknowledgedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            SuppressedUntil = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
            ResolvedAt = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            ResolvedBy = reader.IsDBNull(17) ? null : reader.GetString(17)
        };
    }

    private static void AppendTimeRangeFilters(AlertEventFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.From is { } from)
        {
            sql.Append(" AND e.occurred_at >= @from");
            command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from);
        }

        if (filter.To is { } to)
        {
            sql.Append(" AND e.occurred_at < @to");
            command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to);
        }
    }

    private static void AppendScopeFilters(AlertEventFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (!string.IsNullOrWhiteSpace(filter.ServiceId))
        {
            sql.Append(" AND e.service_id = @service_id");
            command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, filter.ServiceId);
        }

        if (filter.LayerId is { } layerId)
        {
            sql.Append(" AND e.layer_id = @layer_id");
            command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        }

        if (filter.ObjectId is { } objectId)
        {
            sql.Append(" AND e.objectid = @object_id");
            command.Parameters.AddWithValue("object_id", NpgsqlDbType.Bigint, objectId);
        }

        if (filter.RuleId is { } ruleId)
        {
            sql.Append(" AND e.rule_id = @rule_id");
            command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);
        }
    }

    private static void AppendSeverityFilter(AlertEventFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.Severities is null || filter.Severities.Count == 0)
        {
            return;
        }

        var values = new string[filter.Severities.Count];
        for (var i = 0; i < filter.Severities.Count; i++)
        {
            values[i] = filter.Severities[i].ToDbValue();
        }

        sql.Append(" AND e.severity = ANY(@severities)");
        command.Parameters.AddWithValue("severities", NpgsqlDbType.Array | NpgsqlDbType.Text, values);
    }

    private static void AppendIncidentFilter(AlertEventFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.IncidentStatuses is null || filter.IncidentStatuses.Count == 0)
        {
            return;
        }

        var values = new short[filter.IncidentStatuses.Count];
        for (var i = 0; i < filter.IncidentStatuses.Count; i++)
        {
            values[i] = filter.IncidentStatuses[i].ToDbValue();
        }

        sql.Append(" AND e.incident_status = ANY(@incident_statuses)");
        command.Parameters.AddWithValue("incident_statuses", NpgsqlDbType.Array | NpgsqlDbType.Smallint, values);
    }

    private static void AppendLifecycleFilter(AlertEventFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.LifecycleStatuses is null || filter.LifecycleStatuses.Count == 0)
        {
            return;
        }

        var values = new short[filter.LifecycleStatuses.Count];
        for (var i = 0; i < filter.LifecycleStatuses.Count; i++)
        {
            values[i] = AlertLifecycleConversions.ToDbValue(filter.LifecycleStatuses[i]);
        }

        sql.Append(" AND COALESCE(l.lifecycle_status, 0) = ANY(@lifecycle_statuses)");
        command.Parameters.AddWithValue("lifecycle_statuses", NpgsqlDbType.Array | NpgsqlDbType.Smallint, values);
    }

    private static void AppendCursorPredicate(AlertEventCursor? cursor, StringBuilder sql, NpgsqlCommand command)
    {
        if (cursor is null)
        {
            return;
        }

        sql.Append(" AND (e.occurred_at, e.event_id) < (@cursor_occurred_at, @cursor_event_id)");
        command.Parameters.AddWithValue("cursor_occurred_at", NpgsqlDbType.TimestampTz, cursor.OccurredAt);
        command.Parameters.AddWithValue("cursor_event_id", NpgsqlDbType.Bigint, cursor.EventId);
    }
}

/// <summary>
/// Cursor encoding for alert-event pagination.
/// </summary>
internal sealed record AlertEventCursor(DateTimeOffset OccurredAt, long EventId)
{
    public static string Encode(DateTimeOffset occurredAt, long eventId)
    {
        return PostgresCursorCodec.Encode(occurredAt, eventId.ToString(CultureInfo.InvariantCulture));
    }

    public static AlertEventCursor? TryDecode(string? cursor)
    {
        if (!PostgresCursorCodec.TryDecodeTimestampAndSuffix(cursor, out var occurredAt, out var suffix) ||
            !long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            return null;
        }

        return new AlertEventCursor(occurredAt, eventId);
    }
}

/// <summary>
/// Helpers for converting <see cref="AlertLifecycleStatus"/> values to/from the
/// SMALLINT representation used in <c>honua.alert_event_lifecycle</c>.
/// </summary>
internal static class AlertLifecycleConversions
{
    public static AlertLifecycleStatus ToLifecycleStatus(short value) => value switch
    {
        0 => AlertLifecycleStatus.Open,
        1 => AlertLifecycleStatus.Acknowledged,
        2 => AlertLifecycleStatus.Suppressed,
        3 => AlertLifecycleStatus.Resolved,
        _ => throw new InvalidOperationException($"Unsupported alert lifecycle status '{value}'.")
    };

    public static short ToDbValue(AlertLifecycleStatus value) => value switch
    {
        AlertLifecycleStatus.Open => 0,
        AlertLifecycleStatus.Acknowledged => 1,
        AlertLifecycleStatus.Suppressed => 2,
        AlertLifecycleStatus.Resolved => 3,
        _ => throw new InvalidOperationException($"Unsupported alert lifecycle status '{value}'.")
    };
}
