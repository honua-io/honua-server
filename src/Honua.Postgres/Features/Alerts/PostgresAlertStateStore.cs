// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertStateStore : IAlertStateStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertStateStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
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

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
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

    public async Task UpsertAsync(AlertStateSnapshot state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        const string sql = """
            INSERT INTO honua.alert_state (
                rule_id, layer_id, objectid, inside, entered_at, last_evaluated_at, last_alert_at, last_generation, threshold_state)
            VALUES (
                @rule_id, @layer_id, @objectid, @inside, @entered_at, now(), @last_alert_at, @last_generation, @threshold_state::jsonb)
            ON CONFLICT (rule_id, layer_id, objectid)
            DO UPDATE SET
                inside = EXCLUDED.inside,
                entered_at = EXCLUDED.entered_at,
                last_evaluated_at = now(),
                last_alert_at = EXCLUDED.last_alert_at,
                last_generation = EXCLUDED.last_generation,
                threshold_state = EXCLUDED.threshold_state
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("rule_id", NpgsqlDbType.Bigint, state.RuleId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, state.LayerId);
        command.Parameters.AddWithValue("objectid", NpgsqlDbType.Bigint, state.ObjectId);
        command.Parameters.AddWithValue("inside", NpgsqlDbType.Boolean, state.Inside);
        command.Parameters.AddWithValue("entered_at", NpgsqlDbType.TimestampTz, (object?)state.EnteredAt ?? DBNull.Value);
        command.Parameters.AddWithValue("last_alert_at", NpgsqlDbType.TimestampTz, (object?)state.LastAlertAt ?? DBNull.Value);
        command.Parameters.AddWithValue("last_generation", NpgsqlDbType.Bigint, state.LastGeneration);
        command.Parameters.AddWithValue("threshold_state", NpgsqlDbType.Text, state.ThresholdStateJson);

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
              AND r.trigger_type = 3
            ORDER BY s.entered_at
            LIMIT @max_count
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("due_before", NpgsqlDbType.TimestampTz, dueBefore);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);

        var rows = new List<AlertStateSnapshot>(Math.Max(1, maxCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapState(reader));
        }

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
