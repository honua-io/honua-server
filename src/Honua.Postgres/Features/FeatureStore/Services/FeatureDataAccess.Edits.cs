// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        // When an outbox scope is active and the provider supports transactional outbox,
        // wrap the single-row insert in an explicit transaction so the outbox row commits
        // atomically with the feature row. This closes the post-commit append gap that
        // single-row autocommit paths would otherwise expose to a process crash.
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            return await ExecuteSingleRowMutationWithOutboxAsync(
                "create",
                outboxScope!,
                async (connection, transaction, ct) =>
                {
                    var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                    return (created.Id, (Feature?)created);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CreateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            return await ExecuteSingleRowMutationWithOutboxAsync(
                "update",
                outboxScope!,
                async (connection, transaction, ct) =>
                {
                    var updated = await UpdateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                    return (updated.Id, (Feature?)updated);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        if (TryUseTransactionalOutbox(out var outboxScope))
        {
            // Single-row delete path with outbox: open a transaction so the DELETE ... RETURNING
            // capture and the outbox INSERT commit together. The recorder runs only when the
            // RETURNING row was actually deleted; missing rows skip the outbox write entirely.
            var (Connection, Transaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await using var _ = Connection;
            await using var __ = Transaction;
            var npgsql = Connection.RequireNpgsqlConnection();
            var npgsqlTransaction = (NpgsqlTransaction)Transaction;

            var deleted = await DeleteWithConnectionAsync(layerId, featureId, npgsql, npgsqlTransaction, cancellationToken).ConfigureAwait(false);
            if (deleted.Deleted)
            {
                await TryWriteOutboxRowAsync(
                    outboxScope!,
                    Connection,
                    Transaction,
                    featureId,
                    "delete",
                    deleted.Snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted.Deleted;
        }

        await using var conn = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var simpleDelete = await DeleteWithConnectionAsync(layerId, featureId, conn, transaction: null, cancellationToken).ConfigureAwait(false);
        return simpleDelete.Deleted;
    }

    private bool TryUseTransactionalOutbox(out FeatureMutationOutboxScopeData? scope)
    {
        scope = FeatureMutationOutboxScope.Current;
        return scope is not null && _outboxRepository is not null;
    }

    private async Task<Feature> ExecuteSingleRowMutationWithOutboxAsync(
        string operation,
        FeatureMutationOutboxScopeData scope,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<(long ObjectId, Feature? Snapshot)>> mutate,
        CancellationToken cancellationToken)
    {
        var (Connection, Transaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var _ = Connection;
        await using var __ = Transaction;
        var npgsql = Connection.RequireNpgsqlConnection();
        var npgsqlTransaction = (NpgsqlTransaction)Transaction;

        var (objectId, snapshot) = await mutate(npgsql, npgsqlTransaction, cancellationToken).ConfigureAwait(false);

        await TryWriteOutboxRowAsync(
            scope,
            Connection,
            Transaction,
            objectId,
            operation,
            snapshot,
            cancellationToken).ConfigureAwait(false);

        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot ?? throw new InvalidOperationException("Mutation returned no snapshot.");
    }

    private async Task TryWriteOutboxRowAsync(
        FeatureMutationOutboxScopeData scope,
        DbConnection connection,
        DbTransaction transaction,
        long objectId,
        string operation,
        Feature? snapshot,
        CancellationToken cancellationToken)
    {
        if (_outboxRepository is null)
        {
            return;
        }

        var entry = scope.EntryFactory(objectId, operation, snapshot);
        if (entry is null)
        {
            return;
        }

        await _outboxRepository.WriteOutboxRowAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
    {
        if (editBatch.IsEmpty)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        DbConnection dbConnection;
        NpgsqlTransaction? transaction;

        if (editBatch.RollbackOnFailure)
        {
            // Use RepeatableRead isolation level for feature edits to prevent phantom reads during batch operations
            var (txConnection, dbTransaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            dbConnection = txConnection;
            transaction = (NpgsqlTransaction)dbTransaction;
        }
        else
        {
            dbConnection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = null;
        }

        await using var _ = dbConnection;
        var connection = dbConnection.RequireNpgsqlConnection();
        await using var __ = transaction;

        try
        {
            if (!editBatch.Operations.IsDefaultOrEmpty)
            {
                return await ApplyOrderedEditsAsync(
                    layerId,
                    editBatch,
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            var (createdIds, createResults) = await ProcessCreatesWithResultsAsync(
                layerId,
                editBatch.Creates,
                connection,
                transaction,
                cancellationToken);

            var (updatedCount, updateResults) = await ProcessUpdatesWithResultsAsync(
                layerId,
                editBatch.Updates,
                connection,
                transaction,
                cancellationToken);

            var (deletedCount, deleteResults) = await ProcessDeletesWithResultsAsync(
                layerId,
                editBatch.Deletes,
                connection,
                transaction,
                cancellationToken);

            var hasErrors = System.Linq.Enumerable.Any(createResults, r => !r.IsSuccess) ||
                            System.Linq.Enumerable.Any(updateResults, r => !r.IsSuccess) ||
                            System.Linq.Enumerable.Any(deleteResults, r => !r.IsSuccess);

            if (hasErrors && editBatch.RollbackOnFailure)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                return FeatureEditResult.Rollback(createResults, updateResults, deleteResults);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (hasErrors)
            {
                return FeatureEditResult.Success(
                    System.Linq.Enumerable.Count(createResults, r => r.IsSuccess),
                    System.Linq.Enumerable.Count(updateResults, r => r.IsSuccess),
                    System.Linq.Enumerable.Count(deleteResults, r => r.IsSuccess),
                    createdIds,
                    createResults,
                    updateResults,
                    deleteResults,
                    wasRolledBack: false);
            }

            return FeatureEditResult.Success(
                createdIds.Length,
                updatedCount,
                deletedCount,
                createdIds,
                createResults,
                updateResults,
                deleteResults,
                wasRolledBack: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ApplyEditsFailed(_logger, layerId, editBatch.TotalOperations, ex);

            if (transaction != null)
            {
                await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
                var (createResults, updateResults, deleteResults) = CreateFailedOperationResults(editBatch, "Transaction failed.");

                return FeatureEditResult.Rollback(createResults, updateResults, deleteResults);
            }

            var (failedCreateResults, failedUpdateResults, failedDeleteResults) =
                CreateFailedOperationResults(editBatch, "Edit batch failed.");

            return FeatureEditResult.Success(
                0,
                0,
                0,
                ImmutableArray<long>.Empty,
                failedCreateResults,
                failedUpdateResults,
                failedDeleteResults,
                wasRolledBack: false);
        }
    }

    private async Task<FeatureEditResult> ApplyOrderedEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var createdIds = ImmutableArray.CreateBuilder<long>();
        var createResults = ImmutableArray.CreateBuilder<EditOperationResult>();
        var updateResults = ImmutableArray.CreateBuilder<EditOperationResult>();
        var deleteResults = ImmutableArray.CreateBuilder<EditOperationResult>();

        foreach (var operation in editBatch.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operationSucceeded = await ApplyOrderedEditOperationAsync(
                layerId,
                operation,
                connection,
                transaction,
                createdIds,
                createResults,
                updateResults,
                deleteResults,
                cancellationToken).ConfigureAwait(false);

            if (!operationSucceeded && editBatch.RollbackOnFailure)
            {
                if (transaction != null)
                {
                    await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
                }

                return FeatureEditResult.Rollback(
                    createResults.ToImmutable(),
                    updateResults.ToImmutable(),
                    deleteResults.ToImmutable());
            }
        }

        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var immutableCreatedIds = createdIds.ToImmutable();
        var immutableCreateResults = createResults.ToImmutable();
        var immutableUpdateResults = updateResults.ToImmutable();
        var immutableDeleteResults = deleteResults.ToImmutable();

        return FeatureEditResult.Success(
            immutableCreatedIds.Length,
            immutableUpdateResults.Count(static result => result.IsSuccess),
            immutableDeleteResults.Count(static result => result.IsSuccess),
            immutableCreatedIds,
            immutableCreateResults,
            immutableUpdateResults,
            immutableDeleteResults,
            wasRolledBack: false);
    }

    private async Task<bool> ApplyOrderedEditOperationAsync(
        int layerId,
        FeatureEditOperation operation,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ImmutableArray<long>.Builder createdIds,
        ImmutableArray<EditOperationResult>.Builder createResults,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case FeatureEditOperationKind.Create:
                {
                    var feature = operation.Feature
                        ?? throw new InvalidOperationException("Ordered create operation is missing the feature payload.");

                    // Advance per-operation outbox metadata once per attempt so a failed
                    // mutation consumes its queued row context even if the EntryFactory is
                    // never reached. Without this, the next successful row of the same kind
                    // would inherit the failed row's GeometryChanged / requestId.
                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("create");
                    try
                    {
                        var created = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                var c = await CreateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                                await TryWriteOutboxRowForBatchAsync(conn, tx, c.Id, "create", c, ct).ConfigureAwait(false);
                                return c;
                            },
                            cancellationToken).ConfigureAwait(false);
                        createdIds.Add(created.Id);
                        createResults.Add(EditOperationResult.Success(
                            created.Id,
                            feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                        return true;
                    }
                    catch (Exception ex)
                    {
                        createResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
                        return false;
                    }
                }

            case FeatureEditOperationKind.Update:
                {
                    var feature = operation.Feature
                        ?? throw new InvalidOperationException("Ordered update operation is missing the feature payload.");

                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("update");
                    try
                    {
                        var updated = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                var u = await UpdateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                                await TryWriteOutboxRowForBatchAsync(conn, tx, u.Id, "update", u, ct).ConfigureAwait(false);
                                return u;
                            },
                            cancellationToken).ConfigureAwait(false);
                        updateResults.Add(EditOperationResult.Success(
                            updated.Id,
                            feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                        return true;
                    }
                    catch (Exception ex)
                    {
                        updateResults.Add(EditOperationResult.Failure(
                            GetSafeEditOperationError(ex, "Update"),
                            objectId: feature.Id));
                        return false;
                    }
                }

            case FeatureEditOperationKind.Delete:
                {
                    var objectId = operation.ObjectId
                        ?? throw new InvalidOperationException("Ordered delete operation is missing the target object ID.");

                    FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("delete");
                    try
                    {
                        var deleted = await RunRowMutationAtomicallyAsync(
                            connection,
                            transaction,
                            async (conn, tx, ct) =>
                            {
                                var d = await DeleteWithConnectionAsync(layerId, objectId, conn, tx, ct).ConfigureAwait(false);
                                if (d.Deleted)
                                {
                                    await TryWriteOutboxRowForBatchAsync(conn, tx, objectId, "delete", d.Snapshot, ct).ConfigureAwait(false);
                                }
                                return d;
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (deleted.Deleted)
                        {
                            deleteResults.Add(EditOperationResult.Success(objectId));
                            return true;
                        }

                        deleteResults.Add(EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId));
                        return false;
                    }
                    catch (Exception ex)
                    {
                        deleteResults.Add(EditOperationResult.Failure(
                            GetSafeEditOperationError(ex, "Delete"),
                            objectId: objectId));
                        return false;
                    }
                }

            default:
                throw new InvalidOperationException($"Unsupported ordered edit operation kind '{operation.Kind}'.");
        }
    }

    private static (
        ImmutableArray<EditOperationResult> createResults,
        ImmutableArray<EditOperationResult> updateResults,
        ImmutableArray<EditOperationResult> deleteResults) CreateFailedOperationResults(
        FeatureEditBatch editBatch,
        string errorMessage)
    {
        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            var createResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var updateResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var deleteResults = ImmutableArray.CreateBuilder<EditOperationResult>();

            foreach (var operation in editBatch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Create:
                        createResults.Add(EditOperationResult.Failure(errorMessage));
                        break;
                    case FeatureEditOperationKind.Update:
                        updateResults.Add(EditOperationResult.Failure(
                            errorMessage,
                            objectId: operation.Feature?.Id));
                        break;
                    case FeatureEditOperationKind.Delete:
                        deleteResults.Add(EditOperationResult.Failure(
                            errorMessage,
                            objectId: operation.ObjectId));
                        break;
                }
            }

            return (
                createResults.ToImmutable(),
                updateResults.ToImmutable(),
                deleteResults.ToImmutable());
        }

        return (
            System.Linq.Enumerable.Select(editBatch.Creates, _ => EditOperationResult.Failure(errorMessage)).ToImmutableArray(),
            System.Linq.Enumerable.Select(editBatch.Updates, feature => EditOperationResult.Failure(errorMessage, objectId: feature.Id)).ToImmutableArray(),
            System.Linq.Enumerable.Select(editBatch.Deletes, id => EditOperationResult.Failure(errorMessage, objectId: id)).ToImmutableArray());
    }

    private static async Task RollbackIfNeededAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("has completed", StringComparison.Ordinal))
        {
            // The transaction was already completed by the provider. Treat that as a successful rollback.
        }
    }

    private async Task<Feature> CreateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);
        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$2", layerSrid);

        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var sql = $@"
            INSERT INTO {_tableName} (layer_id, geometry, attributes)
            VALUES ($1, {geometryValueExpression}, $3)
            RETURNING objectid, {geometrySelect}, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        var geometryParam = new NpgsqlParameter
        {
            Value = feature.Geometry ?? (object)DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea
        };
        command.Parameters.Add(geometryParam);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible with source generators)
        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = SerializeToJsonString(attributesDictionary);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Failed to create feature: no result returned");
            }

            return await ReadFeatureAsync(reader, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ResourceConflictException("Feature creation conflicted with existing data.", ex);
        }
    }

    private async Task<Feature> UpdateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        ValidateGeometrySrid(feature.Geometry, layerSrid);
        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$3", layerSrid);

        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var sql = $@"
            UPDATE {_tableName}
            SET geometry = {geometryValueExpression}, attributes = $4
            WHERE layer_id = $1 AND objectid = $2
            RETURNING objectid, {geometrySelect}, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Id);
        var geometryParam = new NpgsqlParameter
        {
            Value = feature.Geometry ?? (object)DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea
        };
        command.Parameters.Add(geometryParam);

        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = SerializeToJsonString(attributesDictionary);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ResourceNotFoundException($"Feature with ID {feature.Id} not found in layer {layerId}");
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    private async Task<DeleteOutcome> DeleteWithConnectionAsync(
        int layerId,
        long featureId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        // When an outbox scope is active we use DELETE ... RETURNING to capture
        // the pre-delete snapshot inside the same statement so the dispatcher can
        // emit a delete event whose payload reflects the row that actually existed.
        // This avoids a second round-trip and removes the TOCTOU window where a
        // concurrent vacuum or a follow-up delete could erase the snapshot.
        var captureSnapshot = FeatureMutationOutboxScope.Current is not null && _outboxRepository is not null;

        if (captureSnapshot)
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
            var sqlReturning = $@"
                DELETE FROM {_tableName}
                WHERE layer_id = $1 AND objectid = $2
                RETURNING objectid, {geometrySelect}, attributes";

            await using var commandReturning = new NpgsqlCommand(sqlReturning, connection)
            {
                Transaction = transaction
            };
            ApplyCommandTimeout(commandReturning, _queryTimeoutSeconds);
            commandReturning.Parameters.AddWithValue(layerId);
            commandReturning.Parameters.AddWithValue(featureId);

            await using var reader = await commandReturning.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DeleteOutcome(false, null);
            }

            var snapshot = await ReadFeatureAsync(reader, cancellationToken).ConfigureAwait(false);
            return new DeleteOutcome(true, snapshot);
        }

        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new DeleteOutcome(rowsAffected > 0, null);
    }

    private readonly record struct DeleteOutcome(bool Deleted, Feature? Snapshot);

    private static void ValidateGeometrySrid(byte[]? geometry, int? layerSrid)
    {
        if (geometry == null || geometry.Length == 0 || !layerSrid.HasValue || layerSrid.Value <= 0)
        {
            return;
        }

        var srid = GetGeometrySrid(geometry);
        if (!srid.HasValue || srid.Value == 0)
        {
            return;
        }

        if (srid.Value != layerSrid.Value)
        {
            throw new ArgumentException(
                $"Geometry SRID {srid.Value} does not match layer SRID {layerSrid.Value}.");
        }
    }

    private static int? GetGeometrySrid(byte[] geometry)
    {
        try
        {
            var reader = new WKBReader();
            var parsed = reader.Read(geometry);
            return parsed.SRID > 0 ? parsed.SRID : 0;
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            throw new ArgumentException("Invalid geometry WKB.", ex);
        }
    }

    private async Task<(ImmutableArray<long> createdIds, ImmutableArray<EditOperationResult> results)> ProcessCreatesWithResultsAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (ImmutableArray<long>.Empty, ImmutableArray<EditOperationResult>.Empty);
        }

        // When an outbox scope is active we serialize creates so the per-row snapshot
        // returned by CreateWithConnectionAsync can drive the outbox factory. The fast
        // bulk INSERT path returns ids only and would force a second projection just to
        // satisfy the outbox payload — single-row inserts give us the snapshot directly
        // and remain inside the same connection/transaction so atomicity is preserved.
        var outboxActive = FeatureMutationOutboxScope.Current is not null && _outboxRepository is not null;

        if (transaction == null && features.Length > 1 && !outboxActive)
        {
            return await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                features,
                (batch, ct) => CreateBatchWithConnectionAsync(layerId, batch, connection, transaction, ct),
                async (feature, ct) =>
                {
                    try
                    {
                        var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, ct).ConfigureAwait(false);
                        return (
                            created.Id,
                            EditOperationResult.Success(
                                created.Id,
                                feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
                    }
                    catch (Exception ex)
                    {
                        return (
                            null,
                            EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
                    }
                },
                (feature, createdId) => EditOperationResult.Success(
                    createdId,
                    feature.Attributes.GetValueOrDefault("globalId")?.ToString()),
                cancellationToken).ConfigureAwait(false);
        }

        var createdIds = new List<long>();
        var results = new List<EditOperationResult>();

        foreach (var feature in features)
        {
            // Advance per-operation outbox metadata once per attempt; a failed mutation
            // must consume its slot so the next successful row of the same kind picks
            // up its own GeometryChanged / requestId, not the failed row's.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("create");
            try
            {
                var created = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        var c = await CreateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                        await TryWriteOutboxRowForBatchAsync(conn, tx, c.Id, "create", c, ct).ConfigureAwait(false);
                        return c;
                    },
                    cancellationToken).ConfigureAwait(false);
                createdIds.Add(created.Id);
                results.Add(EditOperationResult.Success(created.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (Exception ex)
            {
                results.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
            }
        }

        return (createdIds.ToImmutableArray(), results.ToImmutableArray());
    }

    internal static async Task<(ImmutableArray<long> createdIds, ImmutableArray<EditOperationResult> results)> ExecuteAdaptiveNonTransactionalCreateBatchAsync<TFeature>(
        ImmutableArray<TFeature> features,
        Func<ImmutableArray<TFeature>, CancellationToken, Task<ImmutableArray<long>>> batchCreateAsync,
        Func<TFeature, CancellationToken, Task<(long? createdId, EditOperationResult result)>> singleCreateAsync,
        Func<TFeature, long, EditOperationResult> createSuccessResult,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (ImmutableArray<long>.Empty, ImmutableArray<EditOperationResult>.Empty);
        }

        if (features.Length == 1)
        {
            var singleOutcome = await singleCreateAsync(features[0], cancellationToken).ConfigureAwait(false);
            return singleOutcome.createdId.HasValue
                ? ([singleOutcome.createdId.Value], [singleOutcome.result])
                : (ImmutableArray<long>.Empty, [singleOutcome.result]);
        }

        try
        {
            var batchCreatedIds = await batchCreateAsync(features, cancellationToken).ConfigureAwait(false);
            var batchResults = new EditOperationResult[batchCreatedIds.Length];
            for (var i = 0; i < batchCreatedIds.Length; i++)
            {
                batchResults[i] = createSuccessResult(features[i], batchCreatedIds[i]);
            }

            return (batchCreatedIds, batchResults.ToImmutableArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var midpoint = features.Length / 2;
            var left = Slice(features, 0, midpoint);
            var right = Slice(features, midpoint, features.Length - midpoint);

            var leftOutcome = await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                left,
                batchCreateAsync,
                singleCreateAsync,
                createSuccessResult,
                cancellationToken).ConfigureAwait(false);
            var rightOutcome = await ExecuteAdaptiveNonTransactionalCreateBatchAsync(
                right,
                batchCreateAsync,
                singleCreateAsync,
                createSuccessResult,
                cancellationToken).ConfigureAwait(false);

            return (
                leftOutcome.createdIds.AddRange(rightOutcome.createdIds),
                leftOutcome.results.AddRange(rightOutcome.results));
        }
    }

    private static ImmutableArray<T> Slice<T>(ImmutableArray<T> source, int start, int length)
    {
        var builder = ImmutableArray.CreateBuilder<T>(length);
        for (var index = start; index < start + length; index++)
        {
            builder.Add(source[index]);
        }

        return builder.MoveToImmutable();
    }

    private async Task<ImmutableArray<long>> CreateBatchWithConnectionAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
        var geometryPayload = new byte[]?[features.Length];
        var attributesPayload = new string[features.Length];

        for (var i = 0; i < features.Length; i++)
        {
            var feature = features[i];
            ValidateGeometrySrid(feature.Geometry, layerSrid);
            geometryPayload[i] = feature.Geometry;

            var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            attributesPayload[i] = SerializeToJsonString(attributesDictionary);
        }

        var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "payload.geometry", layerSrid);
        var sql = $@"
            WITH payload(geometry, attributes, ordinality) AS (
                SELECT source.geometry, source.attributes, source.ordinality
                FROM unnest($2::bytea[], $3::jsonb[]) WITH ORDINALITY AS source(geometry, attributes, ordinality)
            ),
            inserted AS (
                INSERT INTO {_tableName} (layer_id, geometry, attributes)
                SELECT $1, {geometryValueExpression}, payload.attributes
                FROM payload
                ORDER BY payload.ordinality
                RETURNING objectid
            )
            SELECT objectid
            FROM inserted";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = geometryPayload,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = attributesPayload,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb
        });

        var createdIds = ImmutableArray.CreateBuilder<long>(features.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            createdIds.Add(reader.GetInt64(0));
        }

        if (createdIds.Count != features.Length)
        {
            throw new InvalidOperationException("Failed to create all features in batch.");
        }

        return createdIds.MoveToImmutable();
    }

    private async Task<(int updatedCount, ImmutableArray<EditOperationResult> results)> ProcessUpdatesWithResultsAsync(
        int layerId,
        ImmutableArray<Feature> features,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (features.Length == 0)
        {
            return (0, ImmutableArray<EditOperationResult>.Empty);
        }

        var updatedCount = 0;
        var results = new List<EditOperationResult>();

        foreach (var feature in features)
        {
            // Advance per-operation outbox metadata once per attempt; failed updates must
            // consume their queued slot so the next successful update gets its own
            // GeometryChanged / requestId rather than the failed row's.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("update");
            try
            {
                var updated = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        var u = await UpdateWithConnectionAsync(layerId, feature, conn, tx, ct).ConfigureAwait(false);
                        await TryWriteOutboxRowForBatchAsync(conn, tx, u.Id, "update", u, ct).ConfigureAwait(false);
                        return u;
                    },
                    cancellationToken).ConfigureAwait(false);
                updatedCount++;
                results.Add(EditOperationResult.Success(updated.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            }
            catch (Exception ex)
            {
                results.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Update"), objectId: feature.Id));
            }
        }

        return (updatedCount, results.ToImmutableArray());
    }

    private async Task<(int deletedCount, ImmutableArray<EditOperationResult> results)> ProcessDeletesWithResultsAsync(
        int layerId,
        ImmutableArray<long> featureIds,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (featureIds.Length == 0)
        {
            return (0, ImmutableArray<EditOperationResult>.Empty);
        }

        var deletedCount = 0;
        var results = new List<EditOperationResult>();

        foreach (var featureId in featureIds)
        {
            // Advance per-operation outbox metadata once per attempt; rows that throw or
            // miss the snapshot (no RETURNING) must consume their slot so the next
            // successful delete gets its own queued requestId.
            FeatureMutationOutboxScope.Current?.BeginRowAttempt?.Invoke("delete");
            try
            {
                var deleted = await RunRowMutationAtomicallyAsync(
                    connection,
                    transaction,
                    async (conn, tx, ct) =>
                    {
                        var d = await DeleteWithConnectionAsync(layerId, featureId, conn, tx, ct).ConfigureAwait(false);
                        if (d.Deleted)
                        {
                            await TryWriteOutboxRowForBatchAsync(conn, tx, featureId, "delete", d.Snapshot, ct).ConfigureAwait(false);
                        }
                        return d;
                    },
                    cancellationToken).ConfigureAwait(false);
                if (deleted.Deleted)
                {
                    deletedCount++;
                    results.Add(EditOperationResult.Success(featureId));
                }
                else
                {
                    results.Add(EditOperationResult.Failure($"Feature {featureId} not found", objectId: featureId));
                }
            }
            catch (Exception ex)
            {
                results.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Delete"), objectId: featureId));
            }
        }

        return (deletedCount, results.ToImmutableArray());
    }

    /// <summary>
    /// Writes an outbox row inside the in-flight mutation transaction (or in a new auto-commit
    /// statement on the same connection if no transaction is active). Used by the batch and
    /// ordered-edit paths where the connection/transaction are already managed by ApplyEdits.
    /// </summary>
    private async Task TryWriteOutboxRowForBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long objectId,
        string operation,
        Feature? snapshot,
        CancellationToken cancellationToken)
    {
        if (_outboxRepository is null)
        {
            return;
        }

        var scope = FeatureMutationOutboxScope.Current;
        if (scope is null)
        {
            return;
        }

        var entry = scope.EntryFactory(objectId, operation, snapshot);
        if (entry is null)
        {
            return;
        }

        if (transaction is null)
        {
            // Callers in the active-outbox batch path must wrap the row mutation and this
            // INSERT in a per-row transaction via RunRowMutationAtomicallyAsync so the two
            // commit atomically. Reaching this branch indicates a coding error; throwing
            // here surfaces it loudly rather than silently splitting the mutation and the
            // outbox row across separate connections (which would expose the CDC durability
            // gap the outbox is meant to close).
            throw new InvalidOperationException(
                "Outbox writes require an active transaction when a feature mutation scope is open.");
        }

        await _outboxRepository.WriteOutboxRowAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a row mutation paired with its outbox insert atomically. When the caller already
    /// holds a batch transaction the work runs inside it; when the batch is non-transactional
    /// (RollbackOnFailure=false) but an outbox scope is active, a per-row transaction wraps
    /// the mutation and outbox INSERT so a crash between them cannot leave a committed feature
    /// row without its CDC envelope. When no outbox scope is active the work runs directly on
    /// the connection so the autocommit fast path is preserved. Per-row failures bubble up to
    /// the caller's try/catch and are recorded as edit operation failures while preserving
    /// partial-success semantics.
    /// </summary>
    private async Task<TResult> RunRowMutationAtomicallyAsync<TResult>(
        NpgsqlConnection connection,
        NpgsqlTransaction? batchTransaction,
        Func<NpgsqlConnection, NpgsqlTransaction?, CancellationToken, Task<TResult>> mutateAndAppendOutbox,
        CancellationToken cancellationToken)
    {
        if (batchTransaction is not null || !TryUseTransactionalOutbox(out _))
        {
            return await mutateAndAppendOutbox(connection, batchTransaction, cancellationToken).ConfigureAwait(false);
        }

        await using var rowTransaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var result = await mutateAndAppendOutbox(connection, (NpgsqlTransaction)rowTransaction, cancellationToken).ConfigureAwait(false);
        await rowTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string GetSafeEditOperationError(Exception ex, string operation)
    {
        return ex switch
        {
            ResourceNotFoundException => "Feature not found.",
            ResourceConflictException => "The operation conflicted with existing data.",
            ValidationException => "Invalid feature data.",
            ArgumentException or InvalidOperationException => "Invalid feature data.",
            _ => $"{operation} failed."
        };
    }
}
