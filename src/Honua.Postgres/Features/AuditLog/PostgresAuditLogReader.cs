// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditLogReader"/> backing the Console
/// audit query surface (#1168). Uses the (timestamp DESC, audit_id DESC) index
/// added by migration 034 for stable cursor pagination.
/// </summary>
internal sealed class PostgresAuditLogReader : IAuditLogReader
{
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;
    internal const int DefaultPageSize = 50;

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresAuditLogReader(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
    }

    public async Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var pageSize = ClampPageSize(filter.PageSize);
        var cursor = AuditLogCursor.TryDecode(filter.Cursor);

        var sql = new StringBuilder();
        sql.Append("SELECT audit_id, timestamp, event_type, actor, actor_type, resource_type, resource_id, ");
        sql.Append("action, outcome, correlation_id, remote_ip, user_agent, details ");
        sql.Append("FROM ").Append(_table).Append(" WHERE 1=1");

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };

        AppendTimeRangeFilters(filter, sql, command);
        AppendExactFilters(filter, sql, command);
        AppendEnumFilters(filter, sql, command);
        AppendCursorPredicate(cursor, sql, command);

        sql.Append(" ORDER BY timestamp DESC, audit_id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        command.CommandText = sql.ToString();

        var rows = new List<AuditEventRecord>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapRecord(reader));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var lastKept = rows[pageSize - 1];
            rows.RemoveAt(rows.Count - 1);
            nextCursor = AuditLogCursor.Encode(lastKept.Timestamp, lastKept.AuditId);
        }

        return new AuditEventPage { Items = rows, NextCursor = nextCursor };
    }

    internal static int ClampPageSize(int requested)
    {
        if (requested <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(MaxPageSize, Math.Max(MinPageSize, requested));
    }

    private static AuditEventRecord MapRecord(NpgsqlDataReader reader)
    {
        return new AuditEventRecord
        {
            AuditId = reader.GetInt64(0),
            Timestamp = reader.GetFieldValue<DateTimeOffset>(1),
            EventType = ParseEventType(reader.GetString(2)),
            Actor = reader.GetString(3),
            ActorType = ParseActorType(reader.GetString(4)),
            ResourceType = reader.GetString(5),
            ResourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Action = reader.GetString(7),
            Outcome = ParseOutcome(reader.GetString(8)),
            CorrelationId = reader.GetString(9),
            RemoteIp = reader.IsDBNull(10) ? null : reader.GetString(10),
            UserAgent = reader.IsDBNull(11) ? null : reader.GetString(11),
            Details = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
        };
    }

    private static void AppendTimeRangeFilters(AuditLogFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.From is { } from)
        {
            sql.Append(" AND timestamp >= @from");
            command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from);
        }

        if (filter.To is { } to)
        {
            sql.Append(" AND timestamp < @to");
            command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to);
        }
    }

    private static void AppendExactFilters(AuditLogFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            sql.Append(" AND actor = @actor");
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, filter.Actor);
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceType))
        {
            sql.Append(" AND resource_type = @resource_type");
            command.Parameters.AddWithValue("resource_type", NpgsqlDbType.Text, filter.ResourceType);
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceId))
        {
            sql.Append(" AND resource_id = @resource_id");
            command.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, filter.ResourceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            sql.Append(" AND action = @action");
            command.Parameters.AddWithValue("action", NpgsqlDbType.Text, filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            sql.Append(" AND correlation_id = @correlation_id");
            command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Text, filter.CorrelationId);
        }
    }

    private static void AppendEnumFilters(AuditLogFilter filter, StringBuilder sql, NpgsqlCommand command)
    {
        if (filter.ActorTypes is { Count: > 0 } actorTypes)
        {
            var values = new string[actorTypes.Count];
            for (var i = 0; i < actorTypes.Count; i++)
            {
                values[i] = actorTypes[i].ToString();
            }

            sql.Append(" AND actor_type = ANY(@actor_types)");
            command.Parameters.AddWithValue("actor_types", NpgsqlDbType.Array | NpgsqlDbType.Text, values);
        }

        if (filter.EventTypes is { Count: > 0 } eventTypes)
        {
            var values = new string[eventTypes.Count];
            for (var i = 0; i < eventTypes.Count; i++)
            {
                values[i] = eventTypes[i].ToString();
            }

            sql.Append(" AND event_type = ANY(@event_types)");
            command.Parameters.AddWithValue("event_types", NpgsqlDbType.Array | NpgsqlDbType.Text, values);
        }

        if (filter.Outcomes is { Count: > 0 } outcomes)
        {
            var values = new string[outcomes.Count];
            for (var i = 0; i < outcomes.Count; i++)
            {
                values[i] = outcomes[i].ToString();
            }

            sql.Append(" AND outcome = ANY(@outcomes)");
            command.Parameters.AddWithValue("outcomes", NpgsqlDbType.Array | NpgsqlDbType.Text, values);
        }
    }

    private static void AppendCursorPredicate(AuditLogCursor? cursor, StringBuilder sql, NpgsqlCommand command)
    {
        if (cursor is null)
        {
            return;
        }

        sql.Append(" AND (timestamp, audit_id) < (@cursor_ts, @cursor_id)");
        command.Parameters.AddWithValue("cursor_ts", NpgsqlDbType.TimestampTz, cursor.Timestamp);
        command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Bigint, cursor.AuditId);
    }

    private static AuditEventType ParseEventType(string value)
        => Enum.TryParse<AuditEventType>(value, ignoreCase: false, out var parsed) ? parsed : AuditEventType.AdminAction;

    private static AuditActorType ParseActorType(string value)
        => Enum.TryParse<AuditActorType>(value, ignoreCase: false, out var parsed) ? parsed : AuditActorType.Anonymous;

    private static AuditOutcome ParseOutcome(string value)
        => Enum.TryParse<AuditOutcome>(value, ignoreCase: false, out var parsed) ? parsed : AuditOutcome.Failure;
}

/// <summary>
/// Cursor encoding for audit-log pagination.
/// </summary>
internal sealed record AuditLogCursor(DateTimeOffset Timestamp, long AuditId)
{
    public static string Encode(DateTimeOffset timestamp, long auditId)
    {
        var raw = $"{timestamp.UtcTicks}:{auditId}";
        return Base64Url.Encode(raw);
    }

    public static AuditLogCursor? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!Base64Url.TryDecode(cursor, out var decoded))
        {
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0 || separator == decoded.Length - 1)
        {
            return null;
        }

        if (!long.TryParse(decoded.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(decoded.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var auditId))
        {
            return null;
        }

        return new AuditLogCursor(new DateTimeOffset(ticks, TimeSpan.Zero), auditId);
    }
}
