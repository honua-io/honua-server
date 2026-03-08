// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads the change-tracking log from Postgres for incremental replication sync
/// </summary>
internal sealed class PostgresChangeTracker : IChangeTracker
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresChangeTracker(
        IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT last_value FROM honua.sync_generation";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long gen ? gen : 0;
    }

    public async Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layerIds);

        if (layerIds.Length == 0)
        {
            return [];
        }

        // Collapse multiple changes per (layer_id, objectid) into a single net operation:
        //   - First op Insert + last op anything → if last is Delete, omit (no-op); else Insert
        //   - First op Update/Delete + last op Delete → Delete
        //   - First op Update + last op Update → Update
        const string sql = """
            WITH ranked AS (
                SELECT change_id, generation, layer_id, objectid, operation, changed_at,
                       FIRST_VALUE(operation) OVER (PARTITION BY layer_id, objectid ORDER BY generation) AS first_op,
                       LAST_VALUE(operation)  OVER (PARTITION BY layer_id, objectid ORDER BY generation
                           ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS last_op,
                       ROW_NUMBER() OVER (PARTITION BY layer_id, objectid ORDER BY generation DESC) AS rn,
                       MAX(generation) OVER (PARTITION BY layer_id, objectid) AS max_gen
                FROM honua.feature_changes
                WHERE generation > $1
                  AND layer_id = ANY($2)
            )
            SELECT change_id, max_gen AS generation, layer_id, objectid,
                   CASE
                       WHEN first_op = 1 AND last_op = 3 THEN 0
                       WHEN first_op = 1 THEN 1
                       WHEN last_op = 3 THEN 3
                       ELSE 2
                   END AS net_operation,
                   changed_at
            FROM ranked
            WHERE rn = 1
              AND CASE
                      WHEN first_op = 1 AND last_op = 3 THEN 0
                      WHEN first_op = 1 THEN 1
                      WHEN last_op = 3 THEN 3
                      ELSE 2
                  END > 0
            ORDER BY max_gen
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, sinceGeneration);
        command.Parameters.Add(new NpgsqlParameter { Value = layerIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });

        var changes = new List<FeatureChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var netOp = reader.GetInt16(4);
            if (netOp == 0)
            {
                continue; // insert+delete = no-op (should be filtered by HAVING but just in case)
            }

            changes.Add(new FeatureChange
            {
                ChangeId = reader.GetInt64(0),
                Generation = reader.GetInt64(1),
                LayerId = reader.GetInt32(2),
                ObjectId = reader.GetInt64(3),
                Operation = (FeatureChangeOperation)netOp,
                ChangedAt = reader.GetFieldValue<DateTimeOffset>(5)
            });
        }

        return changes;
    }
}
