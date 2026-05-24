// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertAdminStore : IAlertAdminStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertAdminStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IReadOnlyList<AlertZoneDefinition>> ListZonesAsync(
        string? serviceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active
            FROM honua.alert_zones
            WHERE (@service_id IS NULL OR service_id = @service_id)
            ORDER BY zone_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, (object?)serviceId ?? DBNull.Value);

        var results = new List<AlertZoneDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(AlertStoreConversions.MapZone(reader));
        }

        return results;
    }

    public async Task<AlertZoneDefinition?> GetZoneAsync(
        long zoneId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active
            FROM honua.alert_zones
            WHERE zone_id = @zone_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, zoneId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return AlertStoreConversions.MapZone(reader);
    }

    public async Task<AlertZoneDefinition> CreateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zone);

        const string sql = """
            INSERT INTO honua.alert_zones (service_id, zone_name, geometry, metadata, is_active)
            VALUES (
                @service_id,
                @zone_name,
                CASE
                    WHEN @geometry IS NULL THEN NULL
                    ELSE ST_SetSRID(ST_GeomFromWKB(@geometry), @geometry_srid)
                END,
                @metadata::jsonb,
                @is_active)
            RETURNING zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = BuildZoneWriteCommand(sql, connection, zone);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return AlertStoreConversions.MapZone(reader);
    }

    public async Task<AlertZoneDefinition?> UpdateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zone);

        const string sql = """
            UPDATE honua.alert_zones
            SET service_id = @service_id,
                zone_name = @zone_name,
                geometry = CASE
                    WHEN @geometry IS NULL THEN NULL
                    ELSE ST_SetSRID(ST_GeomFromWKB(@geometry), @geometry_srid)
                END,
                metadata = @metadata::jsonb,
                is_active = @is_active,
                updated_at = now()
            WHERE zone_id = @zone_id
            RETURNING zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = BuildZoneWriteCommand(sql, connection, zone);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, zone.ZoneId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return AlertStoreConversions.MapZone(reader);
    }

    public async Task<bool> DeleteZoneAsync(long zoneId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM honua.alert_zones
            WHERE zone_id = @zone_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, zoneId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(
        string? serviceId,
        int? layerId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                   conditions, cooldown_seconds, severity, edition_required, channels, is_active
            FROM honua.alert_rules
            WHERE (@service_id IS NULL OR service_id = @service_id)
              AND (@layer_id IS NULL OR layer_id = @layer_id)
            ORDER BY rule_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, (object?)serviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, (object?)layerId ?? DBNull.Value);

        var rules = new List<AlertRuleDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(AlertStoreConversions.MapRule(reader));
        }

        return rules;
    }

    public async Task<AlertRuleDefinition?> GetRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                   conditions, cooldown_seconds, severity, edition_required, channels, is_active
            FROM honua.alert_rules
            WHERE rule_id = @rule_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return AlertStoreConversions.MapRule(reader);
    }

    public async Task<AlertRuleDefinition> CreateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        const string sql = """
            INSERT INTO honua.alert_rules (
                service_id, layer_id, zone_id, rule_name, trigger_type,
                conditions, cooldown_seconds, severity, edition_required, channels, is_active)
            VALUES (
                @service_id, @layer_id, @zone_id, @rule_name, @trigger_type,
                @conditions::jsonb, @cooldown_seconds, @severity, @edition_required, @channels, @is_active)
            RETURNING rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                      conditions, cooldown_seconds, severity, edition_required, channels, is_active
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = BuildRuleWriteCommand(sql, connection, rule);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return AlertStoreConversions.MapRule(reader);
    }

    public async Task<AlertRuleDefinition?> UpdateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        const string sql = """
            UPDATE honua.alert_rules
            SET service_id = @service_id,
                layer_id = @layer_id,
                zone_id = @zone_id,
                rule_name = @rule_name,
                trigger_type = @trigger_type,
                conditions = @conditions::jsonb,
                cooldown_seconds = @cooldown_seconds,
                severity = @severity,
                edition_required = @edition_required,
                channels = @channels,
                is_active = @is_active,
                updated_at = now()
            WHERE rule_id = @rule_id
            RETURNING rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                      conditions, cooldown_seconds, severity, edition_required, channels, is_active
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = BuildRuleWriteCommand(sql, connection, rule);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, rule.RuleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return AlertStoreConversions.MapRule(reader);
    }

    public async Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM honua.alert_rules
            WHERE rule_id = @rule_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<AlertRuleHealthSnapshot?> GetRuleHealthAsync(
        long ruleId,
        int recentTriggerLimit,
        CancellationToken cancellationToken = default)
    {
        var rule = await GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var clampedRecentTriggerLimit = Math.Clamp(recentTriggerLimit, 1, 50);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        var state = await GetRuleStateHealthAsync(connection, rule, now, cancellationToken).ConfigureAwait(false);
        var events = await GetRuleEventHealthAsync(connection, ruleId, clampedRecentTriggerLimit, cancellationToken).ConfigureAwait(false);
        var delivery = await GetRuleDeliveryHealthAsync(connection, ruleId, cancellationToken).ConfigureAwait(false);

        return new AlertRuleHealthSnapshot
        {
            RuleId = ruleId,
            LastEvaluatedAt = state.LastEvaluatedAt,
            LastTriggeredAt = events.LastTriggeredAt,
            ActiveIncidentCount = state.ActiveIncidentCount,
            RecentTriggerCount = events.RecentTriggerCount,
            CoolingDownFeatureCount = state.CoolingDownFeatureCount,
            NextCooldownExpiresAt = state.NextCooldownExpiresAt,
            DeliveryFailureCount = delivery.Sum(static channel => channel.FailedCount),
            DeadLetterCount = delivery.Sum(static channel => channel.DeadLetterCount),
            LinkedEventIds = events.LinkedEventIds,
            DeliveryChannels = delivery
        };
    }

    private static async Task<RuleStateHealth> GetRuleStateHealthAsync(
        NpgsqlConnection connection,
        AlertRuleDefinition rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT MAX(last_evaluated_at),
                   COUNT(*) FILTER (
                       WHERE @cooldown_seconds > 0
                         AND last_alert_at IS NOT NULL
                         AND @now < last_alert_at + make_interval(secs => @cooldown_seconds)
                   )::int,
                   MAX(last_alert_at + make_interval(secs => @cooldown_seconds)) FILTER (
                       WHERE @cooldown_seconds > 0
                         AND last_alert_at IS NOT NULL
                         AND @now < last_alert_at + make_interval(secs => @cooldown_seconds)
                   ) AS next_cooldown_expires_at,
                   COUNT(*) FILTER (
                       WHERE (@count_inside_state AND inside = TRUE)
                          OR (@count_threshold_state AND threshold_state ->> 'breached' = 'true')
                   )::int AS active_incident_count
            FROM honua.alert_state
            WHERE rule_id = @rule_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, rule.RuleId);
        command.Parameters.AddWithValue("cooldown_seconds", NpgsqlDbType.Integer, rule.CooldownSeconds);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("count_inside_state", NpgsqlDbType.Boolean, UsesInsideActiveState(rule.TriggerType));
        command.Parameters.AddWithValue("count_threshold_state", NpgsqlDbType.Boolean, rule.TriggerType == AlertTriggerType.Threshold);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new RuleStateHealth(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3));
    }

    private static async Task<RuleEventHealth> GetRuleEventHealthAsync(
        NpgsqlConnection connection,
        long ruleId,
        int recentTriggerLimit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (SELECT MAX(occurred_at)
                 FROM honua.alert_events
                 WHERE rule_id = @rule_id) AS last_triggered_at,
                (SELECT COUNT(*)::int
                 FROM honua.alert_events
                 WHERE rule_id = @rule_id
                   AND occurred_at >= now() - interval '24 hours') AS recent_trigger_count,
                ARRAY(
                    SELECT event_id
                    FROM honua.alert_events
                    WHERE rule_id = @rule_id
                    ORDER BY occurred_at DESC, event_id DESC
                    LIMIT @recent_trigger_limit
                ) AS linked_event_ids
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);
        command.Parameters.AddWithValue("recent_trigger_limit", NpgsqlDbType.Integer, recentTriggerLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var eventIds = reader.GetFieldValue<long[]>(2);
        return new RuleEventHealth(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetInt32(1),
            ImmutableArray.CreateRange(eventIds));
    }

    private static async Task<ImmutableArray<AlertRuleDeliveryHealth>> GetRuleDeliveryHealthAsync(
        NpgsqlConnection connection,
        long ruleId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.channel_type,
                   COUNT(*) FILTER (WHERE d.status = 0)::int AS pending_count,
                   COUNT(*) FILTER (WHERE d.status = 1)::int AS processing_count,
                   COUNT(*) FILTER (WHERE d.status = 2)::int AS delivered_count,
                   COUNT(*) FILTER (WHERE d.status = 3)::int AS failed_count,
                   COUNT(*) FILTER (WHERE d.status = 4)::int AS dead_letter_count,
                   MAX(d.last_attempt_at) AS last_attempt_at,
                   MAX(d.delivered_at) AS last_delivered_at,
                   (ARRAY_AGG(d.last_error ORDER BY d.updated_at DESC) FILTER (WHERE d.last_error IS NOT NULL))[1] AS last_error
            FROM honua.alert_dispatch d
            INNER JOIN honua.alert_events e ON e.event_id = d.event_id
            WHERE e.rule_id = @rule_id
            GROUP BY d.channel_type
            ORDER BY d.channel_type
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);

        var results = ImmutableArray.CreateBuilder<AlertRuleDeliveryHealth>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AlertRuleDeliveryHealth
            {
                ChannelType = AlertStoreConversions.ToChannelType(reader.GetInt16(0)),
                PendingCount = reader.GetInt32(1),
                ProcessingCount = reader.GetInt32(2),
                DeliveredCount = reader.GetInt32(3),
                FailedCount = reader.GetInt32(4),
                DeadLetterCount = reader.GetInt32(5),
                LastAttemptAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                LastDeliveredAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                LastError = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return results.ToImmutable();
    }

    private sealed record RuleStateHealth(
        DateTimeOffset? LastEvaluatedAt,
        int CoolingDownFeatureCount,
        DateTimeOffset? NextCooldownExpiresAt,
        int ActiveIncidentCount);

    private sealed record RuleEventHealth(
        DateTimeOffset? LastTriggeredAt,
        int RecentTriggerCount,
        ImmutableArray<long> LinkedEventIds);

    private static bool UsesInsideActiveState(AlertTriggerType triggerType)
        => triggerType is AlertTriggerType.Enter or AlertTriggerType.Dwell;

    private static NpgsqlCommand BuildZoneWriteCommand(string sql, NpgsqlConnection connection, AlertZoneDefinition zone)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, zone.ServiceId);
        command.Parameters.AddWithValue("zone_name", NpgsqlDbType.Text, zone.ZoneName);
        command.Parameters.AddWithValue("geometry", NpgsqlDbType.Bytea, (object?)zone.Geometry ?? DBNull.Value);
        command.Parameters.AddWithValue("geometry_srid", NpgsqlDbType.Integer, zone.GeometrySrid ?? 4326);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Text, SerializeMetadata(zone.Metadata));
        command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, zone.IsActive);
        return command;
    }

    private static NpgsqlCommand BuildRuleWriteCommand(string sql, NpgsqlConnection connection, AlertRuleDefinition rule)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, rule.ServiceId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, rule.LayerId);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, (object?)rule.ZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("rule_name", NpgsqlDbType.Text, rule.RuleName);
        command.Parameters.AddWithValue("trigger_type", NpgsqlDbType.Smallint, rule.TriggerType.ToDbValue());
        command.Parameters.AddWithValue("conditions", NpgsqlDbType.Text, rule.ConditionsJson);
        command.Parameters.AddWithValue("cooldown_seconds", NpgsqlDbType.Integer, rule.CooldownSeconds);
        command.Parameters.AddWithValue("severity", NpgsqlDbType.Text, rule.Severity.ToDbValue());
        command.Parameters.AddWithValue("edition_required", NpgsqlDbType.Smallint, (short)rule.EditionRequired);
        command.Parameters.AddWithValue("channels", NpgsqlDbType.Array | NpgsqlDbType.Text, rule.Channels.Select(static c => c.ToChannelName()).ToArray());
        command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, rule.IsActive);
        return command;
    }

    private static string SerializeMetadata(ImmutableDictionary<string, string?> metadata)
    {
        return AlertMetadataSerializer.Serialize(metadata);
    }
}
