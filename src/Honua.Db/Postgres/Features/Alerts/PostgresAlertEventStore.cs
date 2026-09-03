// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

internal sealed class PostgresAlertEventStore : IAlertEventStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertEventStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<long?> TryAppendAsync(
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(AlertOutboxCommands.AppendEventSql, connection);
        AlertOutboxCommands.BindAppendEvent(command, alertEvent);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long eventId ? eventId : null;
    }

    public async Task<AlertEventEnvelope?> GetAsync(long eventId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT dedupe_key, rule_id, zone_id, service_id, layer_id, objectid, trigger_type,
                   generation, severity, occurred_at, payload, incident_status, incident_duration_ms, source,
                   source_event_id, job_id, operation_instance_id, correlation_id, audit_id, proposal_id
            FROM honua.alert_events
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

        return new AlertEventEnvelope
        {
            DedupeKey = reader.GetString(0),
            RuleId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            ZoneId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            ServiceId = reader.GetString(3),
            LayerId = reader.GetInt32(4),
            ObjectId = reader.GetInt64(5),
            TriggerType = AlertStoreConversions.ToTriggerType(reader.GetInt16(6)),
            Generation = reader.GetInt64(7),
            Severity = AlertStoreConversions.ToSeverity(reader.GetString(8)),
            OccurredAt = reader.GetFieldValue<DateTimeOffset>(9),
            PayloadJson = reader.IsDBNull(10) ? "{}" : reader.GetString(10),
            IncidentStatus = AlertStoreConversions.ToIncidentStatus(reader.GetInt16(11)),
            IncidentDurationMs = reader.GetInt64(12),
            Source = reader.IsDBNull(13) ? null : reader.GetString(13),
            SourceEventId = reader.IsDBNull(14) ? null : reader.GetString(14),
            JobId = reader.IsDBNull(15) ? null : reader.GetString(15),
            OperationInstanceId = reader.IsDBNull(16) ? null : reader.GetString(16),
            CorrelationId = reader.IsDBNull(17) ? null : reader.GetString(17),
            AuditId = reader.IsDBNull(18) ? null : reader.GetString(18),
            ProposalId = reader.IsDBNull(19) ? null : reader.GetString(19)
        };
    }
}
