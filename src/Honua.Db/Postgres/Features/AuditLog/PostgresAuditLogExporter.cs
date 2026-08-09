// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditLogExporter"/> backing the SIEM
/// export surface (#509). Streams rows oldest-first so the consumer can ingest
/// without buffering and checkpoint on the last <c>audit_id</c>.
/// </summary>
internal sealed class PostgresAuditLogExporter : IAuditLogExporter
{
    /// <summary>Server-side cap so an unbounded export cannot scan the whole table.</summary>
    internal const int MaxExportRows = 1_000_000;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresAuditLogExporter(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
    }

    public async IAsyncEnumerable<AuditEventRecord> ExportAsync(
        AuditExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var limit = ClampLimit(filter.Limit);

        var sql = new StringBuilder();
        sql.Append("SELECT audit_id, timestamp, event_type, actor, actor_type, resource_type, resource_id, ");
        sql.Append("action, outcome, correlation_id, remote_ip, user_agent, details ");
        sql.Append("FROM ").Append(_table).Append(" WHERE 1=1");

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };

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

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            sql.Append(" AND action = @action");
            command.Parameters.AddWithValue("action", NpgsqlDbType.Text, filter.Action);
        }

        sql.Append(" ORDER BY audit_id ASC LIMIT @limit");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        command.CommandText = sql.ToString();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new AuditEventRecord
            {
                AuditId = reader.GetInt64(0),
                Timestamp = reader.GetFieldValue<DateTimeOffset>(1),
                EventType = ParseEnum(reader.GetString(2), AuditEventType.AdminAction),
                Actor = reader.GetString(3),
                ActorType = ParseEnum(reader.GetString(4), AuditActorType.Anonymous),
                ResourceType = reader.GetString(5),
                ResourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Action = reader.GetString(7),
                Outcome = ParseEnum(reader.GetString(8), AuditOutcome.Failure),
                CorrelationId = reader.GetString(9),
                RemoteIp = reader.IsDBNull(10) ? null : reader.GetString(10),
                UserAgent = reader.IsDBNull(11) ? null : reader.GetString(11),
                Details = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            };
        }
    }

    internal static int ClampLimit(int? requested)
    {
        if (requested is not { } value || value <= 0)
        {
            return MaxExportRows;
        }

        return Math.Min(MaxExportRows, value);
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ? parsed : fallback;
}
