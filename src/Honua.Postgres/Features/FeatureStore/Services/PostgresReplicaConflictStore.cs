// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Postgres-backed durable storage for disconnected-sync conflict records (#1167).
/// </summary>
internal sealed class PostgresReplicaConflictStore : IReplicaConflictStore
{
    private const string SelectColumns = """
        conflict_id, replica_id, sync_op_id, service_id, layer_id, object_id,
        conflict_type, base_generation, client_payload_json, server_payload_json, base_payload_json,
        resolution, resolved_by, resolved_at, resolution_payload_json, created_at, updated_at
        """;

    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresReplicaConflictStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task AppendAsync(ReplicaConflict conflict, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO honua.replica_conflicts (
                conflict_id, replica_id, sync_op_id, service_id, layer_id, object_id,
                conflict_type, base_generation, client_payload_json, server_payload_json, base_payload_json,
                resolution, resolved_by, resolved_at, resolution_payload_json, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, conflict.ConflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflict.ReplicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, conflict.SyncOpId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflict.ServiceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, conflict.LayerId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, conflict.ObjectId);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)conflict.ConflictType);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, conflict.BaseGeneration);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflict.ClientPayloadJson);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, conflict.ServerPayloadJson);
        AddNullableText(command, conflict.BasePayloadJson);
        AddNullableSmallint(command, conflict.Resolution is { } resolution ? (short)resolution : null);
        AddNullableText(command, conflict.ResolvedBy);
        AddNullableTimestamp(command, conflict.ResolvedAt);
        AddNullableText(command, conflict.ResolutionPayloadJson);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, conflict.CreatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, conflict.UpdatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReplicaConflict>> ListByReplicaAsync(
        string replicaId,
        bool pendingOnly,
        int limit,
        Guid? afterConflictId,
        CancellationToken cancellationToken = default)
    {
        // Keyset pagination on (created_at DESC, conflict_id DESC). An unknown cursor yields an empty page.
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.replica_conflicts
            WHERE replica_id = $1
              AND ($2 = FALSE OR resolution IS NULL)
              AND ($3::uuid IS NULL OR (created_at, conflict_id) <
                    (SELECT created_at, conflict_id FROM honua.replica_conflicts WHERE conflict_id = $3))
            ORDER BY created_at DESC, conflict_id DESC
            LIMIT $4
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, replicaId);
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, pendingOnly);
        AddNullableUuid(command, afterConflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReplicaConflict>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapConflict(reader));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, int>> CountPendingByReplicaAsync(
        IReadOnlyCollection<string> replicaIds,
        CancellationToken cancellationToken = default)
    {
        if (replicaIds.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        const string sql = """
            SELECT replica_id, COUNT(*)::int
            FROM honua.replica_conflicts
            WHERE replica_id = ANY($1) AND resolution IS NULL
            GROUP BY replica_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = replicaIds as string[] ?? replicaIds.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    public async Task<ReplicaConflict?> GetAsync(Guid conflictId, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.replica_conflicts
            WHERE conflict_id = $1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, conflictId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapConflict(reader);
    }

    public async Task<bool> ResolveAsync(
        Guid conflictId,
        ReplicaConflictResolution resolution,
        string resolvedBy,
        string? resolutionPayloadJson,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: only a still-pending conflict is updated.
        const string sql = """
            UPDATE honua.replica_conflicts
            SET resolution = $2,
                resolved_by = $3,
                resolved_at = now(),
                resolution_payload_json = $4,
                updated_at = now()
            WHERE conflict_id = $1 AND resolution IS NULL
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, conflictId);
        command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (short)resolution);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, resolvedBy);
        AddNullableText(command, resolutionPayloadJson);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    private static ReplicaConflict MapConflict(NpgsqlDataReader reader) => new()
    {
        ConflictId = reader.GetGuid(0),
        ReplicaId = reader.GetString(1),
        SyncOpId = reader.GetGuid(2),
        ServiceId = reader.GetString(3),
        LayerId = reader.GetInt32(4),
        ObjectId = reader.GetInt64(5),
        ConflictType = (ReplicaConflictType)reader.GetInt16(6),
        BaseGeneration = reader.GetInt64(7),
        ClientPayloadJson = reader.GetString(8),
        ServerPayloadJson = reader.GetString(9),
        BasePayloadJson = reader.IsDBNull(10) ? null : reader.GetString(10),
        Resolution = reader.IsDBNull(11) ? null : (ReplicaConflictResolution)reader.GetInt16(11),
        ResolvedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
        ResolvedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        ResolutionPayloadJson = reader.IsDBNull(14) ? null : reader.GetString(14),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(15),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(16),
    };

    private static void AddNullableText(NpgsqlCommand command, string? value)
        => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = (object?)value ?? DBNull.Value,
        });

    private static void AddNullableSmallint(NpgsqlCommand command, short? value)
        => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Smallint,
            Value = (object?)value ?? DBNull.Value,
        });

    private static void AddNullableTimestamp(NpgsqlCommand command, DateTimeOffset? value)
        => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = (object?)value ?? DBNull.Value,
        });

    private static void AddNullableUuid(NpgsqlCommand command, Guid? value)
        => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)value ?? DBNull.Value,
        });
}
