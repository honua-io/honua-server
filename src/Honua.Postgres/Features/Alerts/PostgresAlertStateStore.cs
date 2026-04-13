// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertStateStore : IAlertStateStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAlertStateStore> _logger;

    public PostgresAlertStateStore(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAlertStateStore> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AlertStateSnapshot?> GetAsync(
        long ruleId,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        var results = await GetManyAsync(
            [new AlertStateLookupKey(ruleId, layerId, objectId)],
            cancellationToken).ConfigureAwait(false);

        return results.TryGetValue(new AlertStateLookupKey(ruleId, layerId, objectId), out var state)
            ? state
            : null;
    }

    public async Task<IReadOnlyDictionary<AlertStateLookupKey, AlertStateSnapshot>> GetManyAsync(
        IReadOnlyCollection<AlertStateLookupKey> lookupKeys,
        CancellationToken cancellationToken = default)
    {
        if (lookupKeys.Count == 0)
        {
            return new Dictionary<AlertStateLookupKey, AlertStateSnapshot>();
        }

        const string sql = """
            WITH requested(rule_id, layer_id, objectid) AS (
                SELECT *
                FROM unnest(@rule_ids, @layer_ids, @object_ids)
            )
            SELECT s.rule_id, s.layer_id, s.objectid, s.inside, s.entered_at, s.last_alert_at, s.last_generation, s.threshold_state
            FROM requested r
            INNER JOIN honua.alert_state s
                ON s.rule_id = r.rule_id
               AND s.layer_id = r.layer_id
               AND s.objectid = r.objectid
            """;

        var normalizedKeys = lookupKeys
            .Distinct()
            .ToArray();
        var ruleIds = normalizedKeys.Select(static key => key.RuleId).ToArray();
        var layerIds = normalizedKeys.Select(static key => key.LayerId).ToArray();
        var objectIds = normalizedKeys.Select(static key => key.ObjectId).ToArray();

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("rule_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ruleIds);
        command.Parameters.AddWithValue("layer_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, layerIds);
        command.Parameters.AddWithValue("object_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, objectIds);

        var results = new Dictionary<AlertStateLookupKey, AlertStateSnapshot>(normalizedKeys.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = MapState(reader);
            results[new AlertStateLookupKey(state.RuleId, state.LayerId, state.ObjectId)] = state;
        }

        return results;
    }

    public Task UpsertAsync(AlertStateSnapshot state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return UpsertManyAsync([state], cancellationToken);
    }

    public async Task UpsertManyAsync(
        IReadOnlyCollection<AlertStateSnapshot> states,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (states.Count == 0)
        {
            return;
        }

        const string sql = """
            WITH payload(rule_id, layer_id, objectid, inside, entered_at, last_alert_at, last_generation, threshold_state) AS (
                SELECT *
                FROM unnest(@rule_ids, @layer_ids, @object_ids, @inside, @entered_at, @last_alert_at, @last_generation, @threshold_state)
            )
            INSERT INTO honua.alert_state (
                rule_id, layer_id, objectid, inside, entered_at, last_evaluated_at, last_alert_at, last_generation, threshold_state)
            SELECT
                payload.rule_id,
                payload.layer_id,
                payload.objectid,
                payload.inside,
                payload.entered_at,
                now(),
                payload.last_alert_at,
                payload.last_generation,
                payload.threshold_state::jsonb
            FROM payload
            ON CONFLICT (rule_id, layer_id, objectid)
            DO UPDATE SET
                inside = EXCLUDED.inside,
                entered_at = EXCLUDED.entered_at,
                last_evaluated_at = now(),
                last_alert_at = EXCLUDED.last_alert_at,
                last_generation = EXCLUDED.last_generation,
                threshold_state = EXCLUDED.threshold_state
            """;

        var normalizedStates = states
            .GroupBy(static state => new AlertStateLookupKey(state.RuleId, state.LayerId, state.ObjectId))
            .Select(static group => group.Last())
            .ToArray();

        var ruleIds = normalizedStates.Select(static state => state.RuleId).ToArray();
        var layerIds = normalizedStates.Select(static state => state.LayerId).ToArray();
        var objectIds = normalizedStates.Select(static state => state.ObjectId).ToArray();
        var inside = normalizedStates.Select(static state => state.Inside).ToArray();
        var enteredAt = normalizedStates.Select(static state => state.EnteredAt).ToArray();
        var lastAlertAt = normalizedStates.Select(static state => state.LastAlertAt).ToArray();
        var lastGeneration = normalizedStates.Select(static state => state.LastGeneration).ToArray();
        var thresholdState = normalizedStates.Select(static state => state.ThresholdStateJson).ToArray();

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("rule_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ruleIds);
        command.Parameters.AddWithValue("layer_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, layerIds);
        command.Parameters.AddWithValue("object_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, objectIds);
        command.Parameters.AddWithValue("inside", NpgsqlDbType.Array | NpgsqlDbType.Boolean, inside);
        command.Parameters.AddWithValue("entered_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, enteredAt);
        command.Parameters.AddWithValue("last_alert_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, lastAlertAt);
        command.Parameters.AddWithValue("last_generation", NpgsqlDbType.Array | NpgsqlDbType.Bigint, lastGeneration);
        command.Parameters.AddWithValue("threshold_state", NpgsqlDbType.Array | NpgsqlDbType.Text, thresholdState);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlertStateSnapshot>> GetDwellCandidatesAsync(
        DateTimeOffset dueBefore,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.rule_id, s.layer_id, s.objectid, s.inside, s.entered_at, s.last_alert_at, s.last_generation, s.threshold_state
            FROM honua.alert_state s
            INNER JOIN honua.alert_rules r ON r.rule_id = s.rule_id
            WHERE s.inside = TRUE
              AND s.entered_at IS NOT NULL
              AND s.entered_at <= @due_before
              AND r.is_active = TRUE
              AND r.trigger_type = @dwell_trigger_type
            ORDER BY s.entered_at
            LIMIT @max_count
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("due_before", NpgsqlDbType.TimestampTz, dueBefore);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);
        command.Parameters.AddWithValue("dwell_trigger_type", NpgsqlDbType.Smallint, AlertTriggerType.Dwell.ToDbValue());

        var rows = new List<AlertStateSnapshot>(Math.Max(1, maxCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapState(reader));
        }

        AlertLog.DwellCandidatesFound(_logger, rows.Count, dueBefore);
        return rows;
    }

    private static AlertStateSnapshot MapState(NpgsqlDataReader reader)
    {
        return new AlertStateSnapshot
        {
            RuleId = reader.GetInt64(0),
            LayerId = reader.GetInt32(1),
            ObjectId = reader.GetInt64(2),
            Inside = reader.GetBoolean(3),
            EnteredAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            LastAlertAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            LastGeneration = reader.GetInt64(6),
            ThresholdStateJson = reader.IsDBNull(7) ? "{}" : reader.GetString(7)
        };
    }
}
