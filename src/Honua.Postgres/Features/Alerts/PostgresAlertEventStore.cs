// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertEventStore : IAlertEventStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertEventStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<long?> TryAppendAsync(
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        const string sql = """
            INSERT INTO honua.alert_events (
                dedupe_key, rule_id, zone_id, service_id, layer_id, objectid, trigger_type,
                generation, severity, occurred_at, payload)
            VALUES (
                @dedupe_key, @rule_id, @zone_id, @service_id, @layer_id, @objectid, @trigger_type,
                @generation, @severity, @occurred_at, @payload::jsonb)
            ON CONFLICT (dedupe_key) DO NOTHING
            RETURNING event_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("dedupe_key", NpgsqlDbType.Text, alertEvent.DedupeKey);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, alertEvent.RuleId);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, (object?)alertEvent.ZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, alertEvent.ServiceId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, alertEvent.LayerId);
        command.Parameters.AddWithValue("objectid", NpgsqlDbType.Bigint, alertEvent.ObjectId);
        command.Parameters.AddWithValue("trigger_type", NpgsqlDbType.Smallint, alertEvent.TriggerType.ToDbValue());
        command.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, alertEvent.Generation);
        command.Parameters.AddWithValue("severity", NpgsqlDbType.Text, alertEvent.Severity.ToDbValue());
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, alertEvent.OccurredAt);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, alertEvent.PayloadJson);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long eventId ? eventId : null;
    }

    public async Task<AlertEventEnvelope?> GetAsync(long eventId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT dedupe_key, rule_id, zone_id, service_id, layer_id, objectid, trigger_type,
                   generation, severity, occurred_at, payload
            FROM honua.alert_events
            WHERE event_id = @event_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
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
            RuleId = reader.GetInt64(1),
            ZoneId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            ServiceId = reader.GetString(3),
            LayerId = reader.GetInt32(4),
            ObjectId = reader.GetInt64(5),
            TriggerType = AlertStoreConversions.ToTriggerType(reader.GetInt16(6)),
            Generation = reader.GetInt64(7),
            Severity = AlertStoreConversions.ToSeverity(reader.GetString(8)),
            OccurredAt = reader.GetFieldValue<DateTimeOffset>(9),
            PayloadJson = reader.IsDBNull(10) ? "{}" : reader.GetString(10)
        };
    }
}
