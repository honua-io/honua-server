// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Postgres-backed persistent storage for replica state
/// </summary>
internal sealed class PostgresReplicaRepository : IReplicaRepository
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresReplicaRepository(
        IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO honua.replicas (replica_id, replica_name, service_id, sync_model, layer_ids, created_at, last_sync_time, last_sync_generation)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (replica_id) DO UPDATE SET
                replica_name = EXCLUDED.replica_name,
                sync_model = EXCLUDED.sync_model,
                layer_ids = EXCLUDED.layer_ids,
                last_sync_time = EXCLUDED.last_sync_time,
                last_sync_generation = EXCLUDED.last_sync_generation
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ReplicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ReplicaName);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ServiceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.SyncModel);
        command.Parameters.Add(new NpgsqlParameter { Value = record.LayerIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, record.CreatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, record.LastSyncTime);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, record.LastSyncGeneration);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT replica_id, replica_name, service_id, sync_model, layer_ids,
                   created_at, last_sync_time, last_sync_generation
            FROM honua.replicas
            WHERE replica_id = $1
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReplicaRecord
        {
            ReplicaId = reader.GetString(0),
            ReplicaName = reader.GetString(1),
            ServiceId = reader.GetString(2),
            SyncModel = reader.GetString(3),
            LayerIds = (int[])reader.GetValue(4),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
            LastSyncTime = reader.GetFieldValue<DateTimeOffset>(6),
            LastSyncGeneration = reader.GetInt64(7)
        };
    }

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM honua.replicas WHERE replica_id = $1";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsAffected > 0;
    }
}
