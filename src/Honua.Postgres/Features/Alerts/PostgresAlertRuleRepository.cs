// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertRuleRepository : IAlertRuleRepository
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAlertRuleRepository> _logger;

    public PostgresAlertRuleRepository(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAlertRuleRepository> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<AlertRuleDefinition>> GetActiveRulesAsync(
        string? serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var rulesByLookup = await GetActiveRulesAsync(
            [new AlertRuleLookupKey(serviceId, layerId)],
            cancellationToken).ConfigureAwait(false);

        return rulesByLookup.TryGetValue(new AlertRuleLookupKey(serviceId, layerId), out var rules)
            ? rules
            : Array.Empty<AlertRuleDefinition>();
    }

    public async Task<IReadOnlyDictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>> GetActiveRulesAsync(
        IReadOnlyCollection<AlertRuleLookupKey> lookupKeys,
        CancellationToken cancellationToken = default)
    {
        if (lookupKeys.Count == 0)
        {
            return new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>();
        }

        var normalizedKeys = lookupKeys
            .Distinct()
            .ToArray();
        var layerIds = normalizedKeys
            .Select(static key => key.LayerId)
            .Distinct()
            .ToArray();

        // When all lookup keys carry a non-null service_id, push the filter into SQL
        // to avoid fetching rows for unrelated services.
        var allKeysHaveServiceId = normalizedKeys.All(static k => k.ServiceId is not null);
        var serviceIds = allKeysHaveServiceId
            ? normalizedKeys.Select(static k => k.ServiceId!).Distinct().ToArray()
            : null;

        var sql = allKeysHaveServiceId
            ? """
              SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                     conditions, cooldown_seconds, severity, edition_required, channels, is_active
              FROM honua.alert_rules
              WHERE is_active = TRUE
                AND layer_id = ANY(@layer_ids)
                AND service_id = ANY(@service_ids)
              ORDER BY layer_id, rule_id
              """
            : """
              SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                     conditions, cooldown_seconds, severity, edition_required, channels, is_active
              FROM honua.alert_rules
              WHERE is_active = TRUE
                AND layer_id = ANY(@layer_ids)
              ORDER BY layer_id, rule_id
              """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("layer_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, layerIds);
        if (serviceIds is not null)
        {
            command.Parameters.AddWithValue("service_ids", NpgsqlDbType.Array | NpgsqlDbType.Text, serviceIds);
        }

        var rulesByLayer = new Dictionary<int, List<AlertRuleDefinition>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rule = AlertStoreConversions.MapRule(reader);
            if (!rulesByLayer.TryGetValue(rule.LayerId, out var layerRules))
            {
                layerRules = new List<AlertRuleDefinition>();
                rulesByLayer[rule.LayerId] = layerRules;
            }

            layerRules.Add(rule);
        }

        var results = new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>(normalizedKeys.Length);
        foreach (var lookupKey in normalizedKeys)
        {
            if (!rulesByLayer.TryGetValue(lookupKey.LayerId, out var layerRules))
            {
                results[lookupKey] = Array.Empty<AlertRuleDefinition>();
                continue;
            }

            results[lookupKey] = lookupKey.ServiceId == null
                ? layerRules
                : layerRules
                    .Where(rule => string.Equals(rule.ServiceId, lookupKey.ServiceId, StringComparison.Ordinal))
                    .ToArray();
        }

        var totalRules = rulesByLayer.Values.Sum(static r => r.Count);
        AlertLog.ActiveRulesLoaded(_logger, totalRules, normalizedKeys.Length);
        return results;
    }

    public async Task<AlertRuleDefinition?> GetRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        var rules = await GetRulesAsync([ruleId], cancellationToken).ConfigureAwait(false);
        return rules.TryGetValue(ruleId, out var rule) ? rule : null;
    }

    public async Task<IReadOnlyDictionary<long, AlertRuleDefinition>> GetRulesAsync(
        IReadOnlyCollection<long> ruleIds,
        CancellationToken cancellationToken = default)
    {
        if (ruleIds.Count == 0)
        {
            return new Dictionary<long, AlertRuleDefinition>();
        }

        const string sql = """
            SELECT rule_id, service_id, layer_id, zone_id, rule_name, trigger_type,
                   conditions, cooldown_seconds, severity, edition_required, channels, is_active
            FROM honua.alert_rules
            WHERE rule_id = ANY(@rule_ids)
            """;

        var normalizedRuleIds = ruleIds.Distinct().ToArray();

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, normalizedRuleIds);

        var results = new Dictionary<long, AlertRuleDefinition>(normalizedRuleIds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rule = AlertStoreConversions.MapRule(reader);
            results[rule.RuleId] = rule;
        }

        return results;
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
            var zone = AlertStoreConversions.MapZone(reader);
            zones[zone.ZoneId] = zone;
        }

        return zones;
    }

}
