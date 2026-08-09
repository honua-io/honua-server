// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

/// <summary>
/// Shared SQL text and parameter binding for the two alert-outbox writes — appending an
/// alert event and enqueuing its per-channel dispatch rows. Centralized here so the
/// standalone stores (<see cref="PostgresAlertEventStore"/>, <see cref="PostgresAlertDispatchStore"/>)
/// and the atomic <see cref="PostgresAlertOutboxWriter"/> issue byte-identical statements and can
/// never drift.
/// </summary>
internal static class AlertOutboxCommands
{
    /// <summary>
    /// Appends an alert event, deduplicating on <c>dedupe_key</c>. Returns the new
    /// <c>event_id</c>, or no row when the event was already appended.
    /// </summary>
    public const string AppendEventSql = """
        INSERT INTO honua.alert_events (
            dedupe_key, rule_id, zone_id, service_id, layer_id, objectid, trigger_type,
            generation, severity, occurred_at, payload, incident_status, incident_duration_ms, source)
        VALUES (
            @dedupe_key, @rule_id, @zone_id, @service_id, @layer_id, @objectid, @trigger_type,
            @generation, @severity, @occurred_at, @payload::jsonb, @incident_status, @incident_duration_ms, @source)
        ON CONFLICT (dedupe_key) DO NOTHING
        RETURNING event_id
        """;

    /// <summary>
    /// Enqueues one dispatch row per channel for a persisted event.
    /// </summary>
    public const string EnqueueDispatchSql = """
        INSERT INTO honua.alert_dispatch (event_id, channel_type, status, attempts, max_attempts, next_attempt_at)
        SELECT @event_id, ct, 0, 0, 5, now()
        FROM unnest(@channel_types) AS ct
        """;

    /// <summary>
    /// Binds the append-event parameters. Operations notifications (and any non-positive
    /// rule id) persist <c>rule_id</c> as NULL — only a positive RuleId is a real rule reference.
    /// </summary>
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
    }

    /// <summary>
    /// Projects the deliverable channels to their distinct DB smallint values.
    /// </summary>
    public static short[] ChannelDbValues(ImmutableArray<AlertChannelType> channels)
        => channels.Distinct().Select(static c => c.ToDbValue()).ToArray();

    /// <summary>
    /// Binds the enqueue-dispatch parameters.
    /// </summary>
    public static void BindEnqueue(NpgsqlCommand command, long eventId, short[] channelTypes)
    {
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
        command.Parameters.AddWithValue("channel_types", NpgsqlDbType.Array | NpgsqlDbType.Smallint, channelTypes);
    }
}
