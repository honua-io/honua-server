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
    private const string SelectColumns = """
        replica_id, replica_name, service_id, sync_model, layer_ids,
        created_at, last_sync_time, last_sync_generation,
        owner, device_client, sync_direction, status, replica_geometry_json, branch_version_id
        """;

    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresReplicaRepository(
        IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
    {
        // Operator-supplied metadata (owner/device/geometry/branch) is preserved with COALESCE so a
        // write-through from a sync that does not carry it cannot clobber a value set at createReplica.
        const string sql = """
            INSERT INTO honua.replicas (
                replica_id, replica_name, service_id, sync_model, layer_ids,
                created_at, last_sync_time, last_sync_generation,
                owner, device_client, sync_direction, status, replica_geometry_json, branch_version_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (replica_id) DO UPDATE SET
                replica_name = EXCLUDED.replica_name,
                sync_model = EXCLUDED.sync_model,
                layer_ids = EXCLUDED.layer_ids,
                last_sync_time = EXCLUDED.last_sync_time,
                last_sync_generation = EXCLUDED.last_sync_generation,
                owner = COALESCE(EXCLUDED.owner, honua.replicas.owner),
                device_client = COALESCE(EXCLUDED.device_client, honua.replicas.device_client),
                replica_geometry_json = COALESCE(EXCLUDED.replica_geometry_json, honua.replicas.replica_geometry_json),
                branch_version_id = COALESCE(EXCLUDED.branch_version_id, honua.replicas.branch_version_id)
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ReplicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ReplicaName);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.ServiceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.SyncModel);
        command.Parameters.Add(new NpgsqlParameter { Value = record.LayerIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, record.CreatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, record.LastSyncTime);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, record.LastSyncGeneration);
        AddNullableText(command, record.Owner);
        AddNullableText(command, record.DeviceClient);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.SyncDirection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, record.Status);
        AddNullableText(command, record.ReplicaGeometryJson);
        AddNullableText(command, record.BranchVersionId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.replicas
            WHERE replica_id = $1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapReplica(reader);
    }

    public async Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.replicas
            WHERE service_id = $1
            ORDER BY created_at DESC, replica_id ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, serviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReplicaRecord>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapReplica(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ReplicaRecord>> ListAllAsync(
        string? serviceId,
        string? status,
        int limit,
        string? afterReplicaId,
        CancellationToken cancellationToken = default)
    {
        // Keyset pagination on (created_at DESC, replica_id DESC). The cursor row is resolved by id;
        // an unknown cursor yields an empty page rather than an error.
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.replicas
            WHERE ($1::text IS NULL OR service_id = $1)
              AND ($2::text IS NULL OR status = $2)
              AND ($3::text IS NULL OR (created_at, replica_id) <
                    (SELECT created_at, replica_id FROM honua.replicas WHERE replica_id = $3))
            ORDER BY created_at DESC, replica_id DESC
            LIMIT $4
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddNullableText(command, serviceId);
        AddNullableText(command, status);
        AddNullableText(command, afterReplicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReplicaRecord>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapReplica(reader));
        }

        return results;
    }

    public async Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM honua.replicas WHERE replica_id = $1";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    private static ReplicaRecord MapReplica(NpgsqlDataReader reader) => new()
    {
        ReplicaId = reader.GetString(0),
        ReplicaName = reader.GetString(1),
        ServiceId = reader.GetString(2),
        SyncModel = reader.GetString(3),
        LayerIds = (int[])reader.GetValue(4),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
        LastSyncTime = reader.GetFieldValue<DateTimeOffset>(6),
        LastSyncGeneration = reader.GetInt64(7),
        Owner = reader.IsDBNull(8) ? null : reader.GetString(8),
        DeviceClient = reader.IsDBNull(9) ? null : reader.GetString(9),
        SyncDirection = reader.GetString(10),
        Status = reader.GetString(11),
        ReplicaGeometryJson = reader.IsDBNull(12) ? null : reader.GetString(12),
        BranchVersionId = reader.IsDBNull(13) ? null : reader.GetString(13),
    };

    private static void AddNullableText(NpgsqlCommand command, string? value)
        => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)value ?? DBNull.Value,
        });
}
