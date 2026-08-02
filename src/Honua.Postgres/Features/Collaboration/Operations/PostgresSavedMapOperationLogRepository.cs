// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Collaboration.Operations;

/// <summary>
/// Restart-durable saved-map operation log whose checkpoint cursor is persisted beside the
/// per-map cursor head.
/// </summary>
internal sealed class PostgresSavedMapOperationLogRepository : ISavedMapOperationLogRepository
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ISavedMapOperationConflictPolicy _conflictPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly int _retainedOperationCount;
    private readonly string _headsTable;
    private readonly string _operationsTable;

    public PostgresSavedMapOperationLogRepository(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ISavedMapOperationConflictPolicy conflictPolicy,
        TimeProvider? timeProvider = null,
        int retainedOperationCount = 512,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(conflictPolicy);
        if (retainedOperationCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedOperationCount),
                retainedOperationCount,
                "Retained operation count must be at least one.");
        }

        _connectionProvider = connectionProvider;
        _conflictPolicy = conflictPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retainedOperationCount = retainedOperationCount;
        _headsTable = SchemaSearchPath.QualifyTable("saved_map_operation_log_heads", schemaName);
        _operationsTable = SchemaSearchPath.QualifyTable("saved_map_operations", schemaName);
    }

    /// <inheritdoc />
    public bool SupportsReplicaSharedReplay => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableReplay => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableCheckpointCursors => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableCheckpointing => true;

    /// <inheritdoc />
    public async Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await EnsureHeadAsync(connection, transaction, request.MapId, cancellationToken)
                .ConfigureAwait(false);
            var headCursor = await LockHeadAsync(connection, transaction, request.MapId, cancellationToken)
                .ConfigureAwait(false);

            var duplicate = await FindDuplicateAsync(connection, transaction, request, cancellationToken)
                .ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return new SavedMapOperationAppendResult
                {
                    Status = SavedMapOperationAppendStatus.Accepted,
                    Operation = duplicate,
                    HeadCursor = new SavedMapOperationCursor(headCursor),
                    IsDuplicate = true,
                };
            }

            var minimumReplayCursor = await GetMinimumReplayCursorAsync(
                    connection,
                    transaction,
                    request.MapId,
                    headCursor,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request.BaseCursor.Value < minimumReplayCursor || request.BaseCursor.Value > headCursor)
            {
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return new SavedMapOperationAppendResult
                {
                    Status = SavedMapOperationAppendStatus.ResyncRequired,
                    HeadCursor = new SavedMapOperationCursor(headCursor),
                    Message = "Base cursor is outside the retained operation-log replay window.",
                };
            }

            var concurrentOperations = await ReadOperationsAsync(
                    connection,
                    transaction,
                    request.MapId,
                    request.BaseCursor.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            var conflict = _conflictPolicy.DetectConflict(
                request,
                concurrentOperations,
                new SavedMapOperationCursor(headCursor));
            if (conflict is not null)
            {
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return new SavedMapOperationAppendResult
                {
                    Status = SavedMapOperationAppendStatus.Conflict,
                    Conflict = conflict,
                    HeadCursor = new SavedMapOperationCursor(headCursor),
                    Message = conflict.Message,
                };
            }

            var operation = new SavedMapOperationEnvelope
            {
                OperationId = request.OperationId,
                MapId = request.MapId,
                ActorId = request.ActorId,
                BaseCursor = request.BaseCursor,
                Kind = request.Kind,
                Payload = ClonePayload(request.Payload),
                IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey),
                ServerCursor = new SavedMapOperationCursor(checked(headCursor + 1)),
                AcceptedAt = _timeProvider.GetUtcNow(),
            };

            await InsertOperationAsync(connection, transaction, operation, cancellationToken)
                .ConfigureAwait(false);
            await AdvanceHeadAsync(connection, transaction, operation, cancellationToken)
                .ConfigureAwait(false);
            await PruneAsync(connection, transaction, operation.MapId, operation.ServerCursor.Value, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

            return new SavedMapOperationAppendResult
            {
                Status = SavedMapOperationAppendStatus.Accepted,
                Operation = operation,
                HeadCursor = operation.ServerCursor,
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) =>
        ReplayCoreAsync(mapId, sinceCursor, useCheckpointCursor: false, cancellationToken);

    /// <inheritdoc />
    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        ReplayCoreAsync(mapId, SavedMapOperationCursor.Empty, useCheckpointCursor: true, cancellationToken);

    /// <inheritdoc />
    public async Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await EnsureHeadAsync(connection, transaction, mapId, cancellationToken).ConfigureAwait(false);
            var headCursor = await LockHeadAsync(connection, transaction, mapId, cancellationToken)
                .ConfigureAwait(false);
            if (checkpointCursor.Value < 0 || checkpointCursor.Value > headCursor)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(checkpointCursor),
                    checkpointCursor.Value,
                    "Checkpoint cursor must be between zero and the operation-log head.");
            }

            var sql = $"""
                UPDATE {_headsTable}
                SET checkpoint_cursor = GREATEST(checkpoint_cursor, @checkpoint_cursor),
                    updated_at = @updated_at
                WHERE map_id = @map_id
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@checkpoint_cursor", checkpointCursor.Value);
            command.Parameters.AddWithValue("@updated_at", _timeProvider.GetUtcNow());
            command.Parameters.AddWithValue("@map_id", mapId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SavedMapOperationReplayResult> ReplayCoreAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        bool useCheckpointCursor,
        CancellationToken cancellationToken)
    {
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var head = await ReadHeadAsync(connection, transaction, mapId, cancellationToken)
                .ConfigureAwait(false);
            if (head is null)
            {
                var result = sinceCursor.Value == 0 || useCheckpointCursor
                    ? BuildReplayOk(SavedMapOperationCursor.Empty, SavedMapOperationCursor.Empty, [])
                    : BuildReplayResyncRequired(
                        sinceCursor,
                        SavedMapOperationCursor.Empty,
                        SavedMapOperationCursor.Empty,
                        "Cursor is not known for this saved map.");
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            var effectiveSinceCursor = useCheckpointCursor
                ? new SavedMapOperationCursor(head.Value.CheckpointCursor)
                : sinceCursor;
            var minimumReplayValue = await GetMinimumReplayCursorAsync(
                    connection,
                    transaction,
                    mapId,
                    head.Value.HeadCursor,
                    cancellationToken)
                .ConfigureAwait(false);
            var minimumReplayCursor = new SavedMapOperationCursor(minimumReplayValue);
            var headCursor = new SavedMapOperationCursor(head.Value.HeadCursor);

            if (effectiveSinceCursor.Value < minimumReplayValue ||
                effectiveSinceCursor.Value > head.Value.HeadCursor)
            {
                var result = BuildReplayResyncRequired(
                    effectiveSinceCursor,
                    headCursor,
                    minimumReplayCursor,
                    "Cursor is outside the retained operation-log replay window.");
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            var operations = await ReadOperationsAsync(
                    connection,
                    transaction,
                    mapId,
                    effectiveSinceCursor.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return new SavedMapOperationReplayResult
            {
                Status = SavedMapOperationReplayStatus.Ok,
                SinceCursor = effectiveSinceCursor,
                HeadCursor = headCursor,
                MinimumReplayCursor = minimumReplayCursor,
                Operations = operations,
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_headsTable} (map_id, head_cursor, checkpoint_cursor, updated_at)
            VALUES (@map_id, 0, 0, @updated_at)
            ON CONFLICT (map_id) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        command.Parameters.AddWithValue("@updated_at", _timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> LockHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT head_cursor FROM {_headsTable} WHERE map_id = @map_id FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(long HeadCursor, long CheckpointCursor)?> ReadHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT head_cursor, checkpoint_cursor FROM {_headsTable} WHERE map_id = @map_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<SavedMapOperationEnvelope?> FindDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var sql = $"""
            SELECT operation_id, map_id, actor_id, base_cursor, operation_kind, payload,
                   idempotency_key, server_cursor, accepted_at
            FROM {_operationsTable}
            WHERE map_id = @map_id
              AND (operation_id = @operation_id
                   OR (@idempotency_key IS NOT NULL AND idempotency_key = @idempotency_key))
            ORDER BY CASE WHEN operation_id = @operation_id THEN 0 ELSE 1 END
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", request.MapId.Value);
        command.Parameters.AddWithValue("@operation_id", request.OperationId.Value);
        command.Parameters.AddWithValue(
            "@idempotency_key",
            NpgsqlDbType.Text,
            (object?)idempotencyKey ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOperation(reader)
            : null;
    }

    private async Task<long> GetMinimumReplayCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        long headCursor,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COALESCE(MIN(server_cursor) - 1, @head_cursor) FROM {_operationsTable} WHERE map_id = @map_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@head_cursor", headCursor);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<SavedMapOperationEnvelope>> ReadOperationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        long sinceCursor,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT operation_id, map_id, actor_id, base_cursor, operation_kind, payload,
                   idempotency_key, server_cursor, accepted_at
            FROM {_operationsTable}
            WHERE map_id = @map_id AND server_cursor > @since_cursor
            ORDER BY server_cursor
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        command.Parameters.AddWithValue("@since_cursor", sinceCursor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var operations = new List<SavedMapOperationEnvelope>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(ReadOperation(reader));
        }

        return operations;
    }

    private async Task InsertOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapOperationEnvelope operation,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_operationsTable}
                (map_id, server_cursor, operation_id, actor_id, base_cursor, operation_kind,
                 payload, idempotency_key, accepted_at)
            VALUES
                (@map_id, @server_cursor, @operation_id, @actor_id, @base_cursor, @operation_kind,
                 @payload, @idempotency_key, @accepted_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", operation.MapId.Value);
        command.Parameters.AddWithValue("@server_cursor", operation.ServerCursor.Value);
        command.Parameters.AddWithValue("@operation_id", operation.OperationId.Value);
        command.Parameters.AddWithValue("@actor_id", operation.ActorId.Value);
        command.Parameters.AddWithValue("@base_cursor", operation.BaseCursor.Value);
        command.Parameters.AddWithValue("@operation_kind", operation.Kind.ToString());
        command.Parameters.AddWithValue("@payload", NpgsqlDbType.Jsonb, SerializePayload(operation.Payload));
        command.Parameters.AddWithValue(
            "@idempotency_key",
            NpgsqlDbType.Text,
            (object?)operation.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@accepted_at", operation.AcceptedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AdvanceHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapOperationEnvelope operation,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {_headsTable}
            SET head_cursor = @head_cursor, updated_at = @updated_at
            WHERE map_id = @map_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@head_cursor", operation.ServerCursor.Value);
        command.Parameters.AddWithValue("@updated_at", operation.AcceptedAt);
        command.Parameters.AddWithValue("@map_id", operation.MapId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SavedMapId mapId,
        long headCursor,
        CancellationToken cancellationToken)
    {
        var pruneThroughCursor = headCursor - _retainedOperationCount;
        if (pruneThroughCursor < 1)
        {
            return;
        }

        var sql = $"DELETE FROM {_operationsTable} WHERE map_id = @map_id AND server_cursor <= @prune_through_cursor";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@map_id", mapId.Value);
        command.Parameters.AddWithValue("@prune_through_cursor", pruneThroughCursor);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SavedMapOperationEnvelope ReadOperation(NpgsqlDataReader reader)
    {
        var operationKindValue = reader.GetString(4);
        if (!Enum.TryParse<SavedMapOperationKind>(operationKindValue, ignoreCase: false, out var operationKind) ||
            !Enum.IsDefined(operationKind))
        {
            throw new InvalidOperationException($"Unknown saved-map operation kind '{operationKindValue}'.");
        }

        using var payloadDocument = JsonDocument.Parse(reader.GetString(5));
        return new SavedMapOperationEnvelope
        {
            OperationId = new SavedMapOperationId(reader.GetString(0)),
            MapId = new SavedMapId(reader.GetString(1)),
            ActorId = new SavedMapActorId(reader.GetString(2)),
            BaseCursor = new SavedMapOperationCursor(reader.GetInt64(3)),
            Kind = operationKind,
            Payload = payloadDocument.RootElement.Clone(),
            IdempotencyKey = reader.IsDBNull(6) ? null : reader.GetString(6),
            ServerCursor = new SavedMapOperationCursor(reader.GetInt64(7)),
            AcceptedAt = reader.GetFieldValue<DateTimeOffset>(8),
        };
    }

    private static SavedMapOperationReplayResult BuildReplayOk(
        SavedMapOperationCursor headCursor,
        SavedMapOperationCursor minimumReplayCursor,
        IReadOnlyList<SavedMapOperationEnvelope> operations) => new()
        {
            Status = SavedMapOperationReplayStatus.Ok,
            SinceCursor = SavedMapOperationCursor.Empty,
            HeadCursor = headCursor,
            MinimumReplayCursor = minimumReplayCursor,
            Operations = operations,
        };

    private static SavedMapOperationReplayResult BuildReplayResyncRequired(
        SavedMapOperationCursor sinceCursor,
        SavedMapOperationCursor headCursor,
        SavedMapOperationCursor minimumReplayCursor,
        string message) => new()
        {
            Status = SavedMapOperationReplayStatus.ResyncRequired,
            SinceCursor = sinceCursor,
            HeadCursor = headCursor,
            MinimumReplayCursor = minimumReplayCursor,
            Operations = [],
            Message = message,
        };

    private static string SerializePayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Undefined ? "null" : payload.GetRawText();

    private static JsonElement ClonePayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Undefined ? default : payload.Clone();

    private static string? NormalizeIdempotencyKey(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
}
