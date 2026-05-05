// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Mobile.FieldCollection;

/// <summary>
/// Postgres-backed FieldCollection sync store. Reads from and writes to the
/// <c>honua.fieldcollection_*</c> tables introduced in migration
/// <c>024_AddFieldCollectionSync.sql</c>.
/// </summary>
internal sealed class PostgresFieldCollectionSyncStore : IFieldCollectionSyncStore
{
    private const string CurrentGenerationSql =
        "SELECT last_value FROM honua.sync_generation";

    private const string GetCursorSql = """
        INSERT INTO honua.fieldcollection_sync_cursors (client_id, last_sync_generation, updated_at)
        VALUES (@client_id, 0, now())
        ON CONFLICT (client_id) DO UPDATE
            SET last_sync_generation = honua.fieldcollection_sync_cursors.last_sync_generation
        RETURNING last_sync_generation
        """;

    private const string SelectChangesSql = """
        SELECT generation, feature_id, layer_id, operation, version, payload, changed_at
        FROM honua.fieldcollection_changes
        WHERE generation > @since_generation
        ORDER BY generation
        LIMIT @limit
        """;

    private const string AdvanceCursorSql = """
        INSERT INTO honua.fieldcollection_sync_cursors (client_id, last_sync_generation, updated_at)
        VALUES (@client_id, @generation, now())
        ON CONFLICT (client_id) DO UPDATE
            SET last_sync_generation = GREATEST(
                    honua.fieldcollection_sync_cursors.last_sync_generation,
                    EXCLUDED.last_sync_generation),
                updated_at = now()
        """;

    private const string LookupIdempotencyResponseSql = """
        SELECT response_payload
        FROM honua.fieldcollection_pushed_changes
        WHERE change_id = @change_id
        """;

    private const string SelectFeatureSql = """
        SELECT version, payload, is_deleted
        FROM honua.fieldcollection_features
        WHERE feature_id = @feature_id AND layer_id = @layer_id
        FOR UPDATE
        """;

    private const string UpsertFeatureSql = """
        INSERT INTO honua.fieldcollection_features (feature_id, layer_id, version, payload, is_deleted, updated_at)
        VALUES (@feature_id, @layer_id, @version, @payload, @is_deleted, now())
        ON CONFLICT (feature_id, layer_id) DO UPDATE
            SET version = EXCLUDED.version,
                payload = EXCLUDED.payload,
                is_deleted = EXCLUDED.is_deleted,
                updated_at = now()
        """;

    private const string AppendChangeSql = """
        INSERT INTO honua.fieldcollection_changes (
            generation, feature_id, layer_id, operation, version, payload, changed_at)
        VALUES (
            @generation, @feature_id, @layer_id, @operation, @version, @payload, @changed_at)
        """;

    private const string AdvanceSyncGenerationSql =
        "SELECT nextval('honua.sync_generation')";

    private const string InsertIdempotencyRecordSql = """
        INSERT INTO honua.fieldcollection_pushed_changes (
            change_id, feature_id, layer_id, operation, outcome, response_payload, pushed_at)
        VALUES (
            @change_id, @feature_id, @layer_id, @operation, @outcome, @response_payload, now())
        """;

    // Two-key advisory lock keyed by (namespace, hashtext(change_id)). pg_advisory_xact_lock
    // serializes concurrent pushes that share a change_id; the lock is released on commit
    // or rollback so we never leak it across requests. The namespace constant keeps this
    // lock space distinct from other features (e.g. raster import) that also use advisory
    // locks. The literal mirrors ticket #894 to make grep-by-ticket easy.
    private const int FieldCollectionPushLockNamespace = 0x0894_FC5C;

    private const string AcquireChangeIdLockSql =
        "SELECT pg_advisory_xact_lock(@namespace, hashtext(@change_id))";

    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresFieldCollectionSyncStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(CurrentGenerationSql, connection);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long gen ? gen : 0;
    }

    public async Task<FieldCollectionSyncCursor> GetSyncCursorAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(GetCursorSql, connection);
        command.Parameters.AddWithValue("client_id", NpgsqlDbType.Text, clientId);

        var lastSyncGeneration = (long?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L;
        return new FieldCollectionSyncCursor
        {
            ClientId = clientId,
            LastSyncGeneration = lastSyncGeneration,
        };
    }

    public async Task<FieldCollectionChangesPage> GetChangesAsync(
        string clientId,
        long sinceGeneration,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfNegative(sinceGeneration);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var fetchLimit = limit + 1;
        var changes = new List<FieldCollectionChange>(Math.Min(limit, 256));
        long currentGeneration;

        await using (var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var selectCmd = new NpgsqlCommand(SelectChangesSql, connection))
            {
                selectCmd.Parameters.AddWithValue("since_generation", NpgsqlDbType.Bigint, sinceGeneration);
                selectCmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, fetchLimit);

                await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var generation = reader.GetInt64(0);
                    var featureId = reader.GetString(1);
                    var layerId = reader.GetInt32(2);
                    var operation = (FieldCollectionChangeOperation)reader.GetInt16(3);
                    var version = reader.GetInt64(4);
                    var payloadJson = reader.IsDBNull(5) ? null : reader.GetFieldValue<string>(5);
                    var changedAt = reader.GetFieldValue<DateTimeOffset>(6);

                    changes.Add(new FieldCollectionChange
                    {
                        Generation = generation,
                        FeatureId = featureId,
                        LayerId = layerId,
                        Operation = operation,
                        Version = version,
                        Timestamp = changedAt,
                        FeaturePayloadJson = payloadJson,
                    });
                }
            }

            await using (var generationCmd = new NpgsqlCommand(CurrentGenerationSql, connection))
            {
                var result = await generationCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                currentGeneration = result is long gen ? gen : 0L;
            }

            var hasMore = changes.Count > limit;
            if (hasMore)
            {
                changes.RemoveRange(limit, changes.Count - limit);
            }

            var nextCursor = changes.Count > 0
                ? changes[^1].Generation
                : Math.Max(sinceGeneration, currentGeneration);

            // Advance the per-client cursor on every successful pull, including empty
            // pages. Without this, a client that has caught up to the current server
            // generation but observes a non-FieldCollection write that bumped the
            // shared sequence keeps re-pulling the same window forever. The
            // ON CONFLICT clause already clamps the stored value with GREATEST so
            // late-arriving smaller cursors cannot regress the watermark.
            await using (var advanceCmd = new NpgsqlCommand(AdvanceCursorSql, connection))
            {
                advanceCmd.Parameters.AddWithValue("client_id", NpgsqlDbType.Text, clientId);
                advanceCmd.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, nextCursor);
                _ = await advanceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new FieldCollectionChangesPage
            {
                Changes = changes,
                ServerGeneration = currentGeneration,
                NextCursor = nextCursor,
                HasMore = hasMore,
            };
        }
    }

    public async Task<FieldCollectionPushResult> PushChangeAsync(
        FieldCollectionPushRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Fast path: a previous request with this change_id committed an idempotency
        // record outside any transaction, so we can return it without taking the
        // advisory lock or starting a transaction.
        var existing = await TryReadIdempotencyResponseAsync(connection, request.ChangeId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        FieldCollectionPushResult result;
        try
        {
            // Serialize concurrent duplicate-changeId requests. The transaction-scoped
            // advisory lock guarantees the loser waits until the winner commits its
            // idempotency row, then the post-lock read returns the stored response —
            // never a unique-violation surfaced as a 5xx.
            await AcquireChangeIdLockAsync(connection, transaction, request.ChangeId, cancellationToken).ConfigureAwait(false);

            existing = await TryReadIdempotencyResponseAsync(connection, request.ChangeId, transaction, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var current = await ReadCurrentFeatureAsync(connection, transaction, request.FeatureId, request.LayerId, cancellationToken).ConfigureAwait(false);
            result = ResolveOutcome(request, current);

            if (result.Outcome == FieldCollectionPushOutcome.Applied)
            {
                var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                var payloadJson = request.Operation == FieldCollectionChangeOperation.Delete ? null : request.FeaturePayloadJson;

                await UpsertFeatureAsync(
                    connection,
                    transaction,
                    request.FeatureId,
                    request.LayerId,
                    result.Version!.Value,
                    payloadJson,
                    isDeleted: request.Operation == FieldCollectionChangeOperation.Delete,
                    cancellationToken).ConfigureAwait(false);

                await AppendChangeAsync(
                    connection,
                    transaction,
                    generation,
                    request.FeatureId,
                    request.LayerId,
                    request.Operation,
                    result.Version.Value,
                    payloadJson,
                    request.Timestamp ?? DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);

                result = result with { ServerGeneration = generation };
            }
            else
            {
                var currentGeneration = await ReadCurrentGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                result = result with { ServerGeneration = currentGeneration };
            }

            await WriteIdempotencyRecordAsync(connection, transaction, request, result, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return result;
    }

    private static FieldCollectionPushResult ResolveOutcome(
        FieldCollectionPushRequest request,
        ServerFeatureSnapshot? current)
    {
        switch (request.Operation)
        {
            case FieldCollectionChangeOperation.Insert:
                return ResolveInsert(request, current);
            case FieldCollectionChangeOperation.Update:
                return ResolveUpdate(request, current);
            case FieldCollectionChangeOperation.Delete:
                return ResolveDelete(request, current);
            default:
                return new FieldCollectionPushResult
                {
                    ChangeId = request.ChangeId,
                    Outcome = FieldCollectionPushOutcome.Rejected,
                    ServerGeneration = 0,
                    RejectionReason = "Unsupported operation.",
                };
        }
    }

    private static FieldCollectionPushResult ResolveInsert(
        FieldCollectionPushRequest request,
        ServerFeatureSnapshot? current)
    {
        if (string.IsNullOrWhiteSpace(request.FeaturePayloadJson))
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Rejected,
                ServerGeneration = 0,
                RejectionReason = "Insert payload is required.",
            };
        }

        if (current is { IsDeleted: false })
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Conflict,
                ServerGeneration = 0,
                ConflictType = FieldCollectionConflictType.UpdateUpdate,
                ServerVersion = current.Version,
                ServerFeaturePayloadJson = current.PayloadJson,
            };
        }

        var version = current is null ? 1L : current.Version + 1;
        return new FieldCollectionPushResult
        {
            ChangeId = request.ChangeId,
            Outcome = FieldCollectionPushOutcome.Applied,
            ServerGeneration = 0,
            Version = version,
        };
    }

    private static FieldCollectionPushResult ResolveUpdate(
        FieldCollectionPushRequest request,
        ServerFeatureSnapshot? current)
    {
        if (string.IsNullOrWhiteSpace(request.FeaturePayloadJson))
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Rejected,
                ServerGeneration = 0,
                RejectionReason = "Update payload is required.",
            };
        }

        if (request.BaseVersion is null)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Rejected,
                ServerGeneration = 0,
                RejectionReason = "Update requires baseVersion.",
            };
        }

        if (current is null)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Conflict,
                ServerGeneration = 0,
                ConflictType = FieldCollectionConflictType.UpdateDelete,
            };
        }

        if (current.IsDeleted)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Conflict,
                ServerGeneration = 0,
                ConflictType = FieldCollectionConflictType.UpdateDelete,
                ServerVersion = current.Version,
                ServerFeaturePayloadJson = current.PayloadJson,
            };
        }

        if (current.Version != request.BaseVersion.Value)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Conflict,
                ServerGeneration = 0,
                ConflictType = FieldCollectionConflictType.UpdateUpdate,
                ServerVersion = current.Version,
                ServerFeaturePayloadJson = current.PayloadJson,
            };
        }

        return new FieldCollectionPushResult
        {
            ChangeId = request.ChangeId,
            Outcome = FieldCollectionPushOutcome.Applied,
            ServerGeneration = 0,
            Version = current.Version + 1,
        };
    }

    private static FieldCollectionPushResult ResolveDelete(
        FieldCollectionPushRequest request,
        ServerFeatureSnapshot? current)
    {
        if (request.BaseVersion is null)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Rejected,
                ServerGeneration = 0,
                RejectionReason = "Delete requires baseVersion.",
            };
        }

        if (current is null || current.IsDeleted)
        {
            // Delete of an already-absent feature is idempotently applied.
            var version = current?.Version + 1 ?? 1L;
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Applied,
                ServerGeneration = 0,
                Version = version,
            };
        }

        if (current.Version != request.BaseVersion.Value)
        {
            return new FieldCollectionPushResult
            {
                ChangeId = request.ChangeId,
                Outcome = FieldCollectionPushOutcome.Conflict,
                ServerGeneration = 0,
                ConflictType = FieldCollectionConflictType.DeleteUpdate,
                ServerVersion = current.Version,
                ServerFeaturePayloadJson = current.PayloadJson,
            };
        }

        return new FieldCollectionPushResult
        {
            ChangeId = request.ChangeId,
            Outcome = FieldCollectionPushOutcome.Applied,
            ServerGeneration = 0,
            Version = current.Version + 1,
        };
    }

    private static async Task<ServerFeatureSnapshot?> ReadCurrentFeatureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string featureId,
        int layerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectFeatureSql, connection, transaction);
        command.Parameters.AddWithValue("feature_id", NpgsqlDbType.Text, featureId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = reader.GetInt64(0);
        var payloadJson = reader.IsDBNull(1) ? null : reader.GetFieldValue<string>(1);
        var isDeleted = reader.GetBoolean(2);
        return new ServerFeatureSnapshot(version, payloadJson, isDeleted);
    }

    private static async Task UpsertFeatureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string featureId,
        int layerId,
        long version,
        string? payloadJson,
        bool isDeleted,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpsertFeatureSql, connection, transaction);
        command.Parameters.AddWithValue("feature_id", NpgsqlDbType.Text, featureId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, version);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("is_deleted", NpgsqlDbType.Boolean, isDeleted);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long generation,
        string featureId,
        int layerId,
        FieldCollectionChangeOperation operation,
        long version,
        string? payloadJson,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AppendChangeSql, connection, transaction);
        command.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, generation);
        command.Parameters.AddWithValue("feature_id", NpgsqlDbType.Text, featureId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Smallint, (short)operation);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, version);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("changed_at", NpgsqlDbType.TimestampTz, changedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> NextGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AdvanceSyncGenerationSql, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long gen ? gen : 0;
    }

    private static async Task<long> ReadCurrentGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CurrentGenerationSql, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long gen ? gen : 0;
    }

    private static async Task WriteIdempotencyRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FieldCollectionPushRequest request,
        FieldCollectionPushResult result,
        CancellationToken cancellationToken)
    {
        var serializedResponse = JsonSerializer.Serialize(result, FieldCollectionSyncStoreJsonContext.Default.FieldCollectionPushResult);

        await using var command = new NpgsqlCommand(InsertIdempotencyRecordSql, connection, transaction);
        command.Parameters.AddWithValue("change_id", NpgsqlDbType.Text, request.ChangeId);
        command.Parameters.AddWithValue("feature_id", NpgsqlDbType.Text, request.FeatureId);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, request.LayerId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Smallint, (short)request.Operation);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Smallint, (short)result.Outcome);
        command.Parameters.AddWithValue("response_payload", NpgsqlDbType.Jsonb, serializedResponse);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcquireChangeIdLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string changeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AcquireChangeIdLockSql, connection, transaction);
        command.Parameters.AddWithValue("namespace", NpgsqlDbType.Integer, FieldCollectionPushLockNamespace);
        command.Parameters.AddWithValue("change_id", NpgsqlDbType.Text, changeId);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<FieldCollectionPushResult?> TryReadIdempotencyResponseAsync(
        NpgsqlConnection connection,
        string changeId,
        CancellationToken cancellationToken)
        => TryReadIdempotencyResponseAsync(connection, changeId, transaction: null, cancellationToken);

    private static async Task<FieldCollectionPushResult?> TryReadIdempotencyResponseAsync(
        NpgsqlConnection connection,
        string changeId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LookupIdempotencyResponseSql, connection, transaction);
        command.Parameters.AddWithValue("change_id", NpgsqlDbType.Text, changeId);

        var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null || raw == DBNull.Value)
        {
            return null;
        }

        var json = (string)raw;
        return JsonSerializer.Deserialize(json, FieldCollectionSyncStoreJsonContext.Default.FieldCollectionPushResult);
    }

    private sealed record ServerFeatureSnapshot(long Version, string? PayloadJson, bool IsDeleted);
}
