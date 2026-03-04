// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertRuleRepository : IAlertRuleRepository
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertRuleRepository(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IReadOnlyList<AlertRuleDefinition>> GetActiveRulesAsync(
        string? serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                   conditions, cooldown_seconds, severity, edition_required, channels, is_active
            FROM honua.alert_rules
            WHERE is_active = TRUE
              AND layer_id = @layer_id
              AND (@service_id IS NULL OR service_id = @service_id)
            ORDER BY rule_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, (object?)serviceId ?? DBNull.Value);

        var rules = new List<AlertRuleDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(MapRule(reader));
        }

        return rules;
    }

    public async Task<AlertRuleDefinition?> GetRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                   conditions, cooldown_seconds, severity, edition_required, channels, is_active
            FROM honua.alert_rules
            WHERE rule_id = @rule_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, ruleId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapRule(reader);
    }

    public async Task<IReadOnlyDictionary<long, AlertZoneDefinition>> GetZonesAsync(
        IReadOnlyCollection<long> zoneIds,
        CancellationToken cancellationToken = default)
    {
        if (zoneIds.Count == 0)
        {
            return new Dictionary<long, AlertZoneDefinition>();
        }

        const string sql = """
            SELECT zone_id, service_id, zone_name, ST_AsBinary(geometry), ST_SRID(geometry), metadata, is_active
            FROM honua.alert_zones
            WHERE zone_id = ANY(@zone_ids)
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zone_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, zoneIds.ToArray());

        var zones = new Dictionary<long, AlertZoneDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var zone = new AlertZoneDefinition
            {
                ZoneId = reader.GetInt64(0),
                ServiceId = reader.GetString(1),
                ZoneName = reader.GetString(2),
                Geometry = reader.IsDBNull(3) ? null : (byte[])reader[3],
                GeometrySrid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Metadata = ParseMetadata(reader.IsDBNull(5) ? "{}" : reader.GetString(5)),
                IsActive = reader.GetBoolean(6)
            };

            zones[zone.ZoneId] = zone;
        }

        return zones;
    }

    private static AlertRuleDefinition MapRule(NpgsqlDataReader reader)
    {
        var channels = reader.IsDBNull(10)
            ? ImmutableArray<AlertChannelType>.Empty
            : ParseChannels((string[])reader[10]);

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
            Channels = channels,
            IsActive = reader.GetBoolean(11)
        };
    }

    private static ImmutableDictionary<string, string?> ParseMetadata(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return ImmutableDictionary<string, string?>.Empty;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                builder[property.Name] = property.Value.ValueKind == System.Text.Json.JsonValueKind.Null
                    ? null
                    : property.Value.ToString();
            }

            return builder.ToImmutable();
        }
        catch (System.Text.Json.JsonException)
        {
            return ImmutableDictionary<string, string?>.Empty;
        }
    }

    private static ImmutableArray<AlertChannelType> ParseChannels(string[] values)
    {
        if (values.Length == 0)
        {
            return ImmutableArray<AlertChannelType>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<AlertChannelType>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                builder.Add(AlertStoreConversions.ParseChannel(value));
            }
            catch (InvalidOperationException)
            {
                // Skip unsupported channel values to keep rule loading resilient to bad data.
            }
        }

        return builder.Distinct().ToImmutableArray();
    }
}
