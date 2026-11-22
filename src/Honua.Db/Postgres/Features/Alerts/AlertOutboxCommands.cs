// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

internal static class AlertOutboxCommands
{
    public const string AppendEventSql = """
        INSERT INTO honua.alert_events (
            dedupe_key, rule_id, zone_id, service_id, layer_id, objectid, trigger_type,
            generation, severity, occurred_at, payload, incident_status, incident_duration_ms, source,
            source_event_id, job_id, operation_instance_id, correlation_id, audit_id, proposal_id)
        VALUES (
            @dedupe_key, @rule_id, @zone_id, @service_id, @layer_id, @objectid, @trigger_type,
            @generation, @severity, @occurred_at, @payload::jsonb, @incident_status, @incident_duration_ms, @source,
            @source_event_id, @job_id, @operation_instance_id, @correlation_id, @audit_id, @proposal_id)
        ON CONFLICT (dedupe_key) DO NOTHING
        RETURNING event_id
        """;

    // Operations notices get about two hours of retries at the default backoff (1 s doubling to 5 min), so a reminder
    // ordinal that falls due during a short channel outage still reaches the recipient; other alerts keep five attempts.
    public const int DefaultDispatchAttempts = 5;
    public const int OpsDispatchAttempts = 30;

    public static readonly string EnqueueDispatchSql = $"""
        INSERT INTO honua.alert_dispatch (event_id, channel_type, status, attempts, max_attempts, next_attempt_at)
        SELECT @event_id, ct, 0, 0,
            CASE WHEN EXISTS (SELECT 1 FROM honua.alert_events e WHERE e.event_id = @event_id AND e.source = '{AlertEventSources.Ops}')
                THEN {OpsDispatchAttempts} ELSE {DefaultDispatchAttempts} END,
            now()
        FROM unnest(@channel_types) AS ct
        """;

    public static void BindAppendEvent(NpgsqlCommand command, AlertEventEnvelope alertEvent)
    {
        var isOps = string.Equals(alertEvent.Source, AlertEventSources.Ops, StringComparison.Ordinal);
        var ruleIdValue = isOps || alertEvent.RuleId <= 0
            ? (object)DBNull.Value
            : alertEvent.RuleId;

        command.Parameters.AddWithValue("dedupe_key", NpgsqlDbType.Text, alertEvent.DedupeKey);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleIdValue);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, (object?)alertEvent.ZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, alertEvent.ServiceId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, alertEvent.LayerId);
        command.Parameters.AddWithValue("objectid", NpgsqlDbType.Bigint, alertEvent.ObjectId);
        command.Parameters.AddWithValue("trigger_type", NpgsqlDbType.Smallint, alertEvent.TriggerType.ToDbValue());
        command.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, alertEvent.Generation);
        command.Parameters.AddWithValue("severity", NpgsqlDbType.Text, alertEvent.Severity.ToDbValue());
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, alertEvent.OccurredAt);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, alertEvent.PayloadJson);
        command.Parameters.AddWithValue("incident_status", NpgsqlDbType.Smallint, alertEvent.IncidentStatus.ToDbValue());
        command.Parameters.AddWithValue("incident_duration_ms", NpgsqlDbType.Bigint, alertEvent.IncidentDurationMs);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Text, (object?)alertEvent.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("source_event_id", NpgsqlDbType.Text, (object?)alertEvent.SourceEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("job_id", NpgsqlDbType.Text, (object?)alertEvent.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("operation_instance_id", NpgsqlDbType.Text, (object?)alertEvent.OperationInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Text, (object?)alertEvent.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("audit_id", NpgsqlDbType.Text, (object?)alertEvent.AuditId ?? DBNull.Value);
        command.Parameters.AddWithValue("proposal_id", NpgsqlDbType.Text, (object?)alertEvent.ProposalId ?? DBNull.Value);
    }

    public static short[] ChannelDbValues(ImmutableArray<AlertChannelType> channels)
        => channels.Distinct().Select(static c => c.ToDbValue()).ToArray();

    public static void BindEnqueue(NpgsqlCommand command, long eventId, short[] channelTypes)
    {
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
        command.Parameters.AddWithValue("channel_types", NpgsqlDbType.Array | NpgsqlDbType.Smallint, channelTypes);
    }
}
