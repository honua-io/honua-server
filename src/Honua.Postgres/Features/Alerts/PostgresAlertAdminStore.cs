// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, (object?)serviceId ?? DBNull.Value);

        var results = new List<AlertZoneDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapZone(reader));
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, zoneId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapZone(reader);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = BuildZoneWriteCommand(sql, connection, zone);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return MapZone(reader);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = BuildZoneWriteCommand(sql, connection, zone);
        command.Parameters.AddWithValue("zone_id", NpgsqlDbType.Bigint, zone.ZoneId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapZone(reader);
    }

    public async Task<bool> DeleteZoneAsync(long zoneId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM honua.alert_zones
            WHERE zone_id = @zone_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, (object?)serviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, (object?)layerId ?? DBNull.Value);

        var rules = new List<AlertRuleDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(MapRule(reader));
        }

        return rules;
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = BuildRuleWriteCommand(sql, connection, rule);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return MapRule(reader);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = BuildRuleWriteCommand(sql, connection, rule);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, rule.RuleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapRule(reader);
    }

    public async Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM honua.alert_rules
            WHERE rule_id = @rule_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

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

    private static AlertZoneDefinition MapZone(NpgsqlDataReader reader)
    {
        return new AlertZoneDefinition
        {
            ZoneId = reader.GetInt64(0),
            ServiceId = reader.GetString(1),
            ZoneName = reader.GetString(2),
            Geometry = reader.IsDBNull(3) ? null : (byte[])reader[3],
            GeometrySrid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            Metadata = ParseMetadata(reader.IsDBNull(5) ? "{}" : reader.GetString(5)),
            IsActive = reader.GetBoolean(6)
        };
    }

    private static AlertRuleDefinition MapRule(NpgsqlDataReader reader)
    {
        return new AlertRuleDefinition
        {
            RuleId = reader.GetInt64(0),
            ServiceId = reader.GetString(1),
            LayerId = reader.GetInt32(2),
            ZoneId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            RuleName = reader.GetString(4),
            TriggerType = AlertStoreConversions.ToTriggerType(reader.GetInt16(5)),
            ConditionsJson = reader.IsDBNull(6) ? "{}" : reader.GetString(6),
            CooldownSeconds = reader.GetInt32(7),
            Severity = AlertStoreConversions.ToSeverity(reader.GetString(8)),
            EditionRequired = AlertStoreConversions.ToEdition(reader.GetInt16(9)),
            Channels = ParseChannels(reader.IsDBNull(10) ? Array.Empty<string>() : (string[])reader[10]),
            IsActive = reader.GetBoolean(11)
        };
    }

    private static ImmutableDictionary<string, string?> ParseMetadata(string json)
    {
        try
        {
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            if (dictionary is null)
            {
                return ImmutableDictionary<string, string?>.Empty;
            }

            return dictionary.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return ImmutableDictionary<string, string?>.Empty;
        }
    }

    private static string SerializeMetadata(ImmutableDictionary<string, string?> metadata)
    {
        if (metadata.IsEmpty)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(metadata);
    }

    private static ImmutableArray<AlertChannelType> ParseChannels(string[] channels)
    {
        if (channels.Length == 0)
        {
            return ImmutableArray<AlertChannelType>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<AlertChannelType>(channels.Length);
        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                continue;
            }

            try
            {
                builder.Add(AlertStoreConversions.ParseChannel(channel));
            }
            catch (InvalidOperationException)
            {
                // Ignore unsupported channel values persisted by previous versions.
            }
        }

        return builder.Distinct().ToImmutableArray();
    }
}
