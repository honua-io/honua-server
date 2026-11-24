// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Npgsql;
using NpgsqlTypes;

using Honua.Db.Postgres.Features.Infrastructure;
namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    // Branch-versioning overlay writes (ADR-0051). A non-DEFAULT edit batch routes the
    // INSERT/UPDATE/DELETE to honua.version_edits inside one transaction, sets the transaction-scoped
    // honua.gdb_version GUC, and captures the DEFAULT base image on the version's first touch of an
    // (layer_id, objectid). DEFAULT data (`features`) is never touched by this path; the DEFAULT write
    // path is left completely unchanged (byte-identical) and never reaches this file.
    //
    // The overlay's BEFORE trigger (049_CreateVersionEdits.sql) records each mutation into
    // honua.feature_changes tagged with the producing version so reconcile/post have a tagged
    // version delta even though DEFAULT was never written.

    private const string VersionEditsTableName = "honua.version_edits";
    private const string VersionedRowSavepoint = "honua_version_edit_row";

    private async Task<FeatureEditResult> ApplyVersionedEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        VersionContext version,
        CancellationToken cancellationToken)
    {
        var versionId = version.VersionId
            ?? throw new InvalidOperationException("Versioned edit batch is missing a version id.");

        var singleUpdate = editBatch.Updates.Length == 1 &&
            editBatch.Creates.IsDefaultOrEmpty && editBatch.Deletes.IsDefaultOrEmpty &&
            editBatch.Operations.IsDefaultOrEmpty;
        var isolationLevel = singleUpdate || (!editBatch.Preconditions.IsDefaultOrEmpty && editBatch.Preconditions.Any(p => p.ExpectedRowAbsent))
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.RepeatableRead;
        var (txConnection, dbTransaction) = await _connectionProvider
            .OpenTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        await using var _ = txConnection;
        var connection = txConnection.RequireNpgsqlConnection();
        var transaction = (NpgsqlTransaction)dbTransaction;
        await using var __ = transaction;

        try
        {
            await SetVersionGucAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);

            var preconditions = BuildPreconditionMap(editBatch);
            var createdIds = ImmutableArray.CreateBuilder<long>();
            var createResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var updateResults = ImmutableArray.CreateBuilder<EditOperationResult>();
            var deleteResults = ImmutableArray.CreateBuilder<EditOperationResult>();

            var failed = false;

            if (!editBatch.Operations.IsDefaultOrEmpty)
            {
                foreach (var operation in editBatch.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ok = await ApplyVersionedOperationAsync(
                        layerId, versionId, operation, editBatch.RowSecurityWriteCheck, editBatch.OwnerGuards, preconditions, connection, transaction,
                        createdIds, createResults, updateResults, deleteResults, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        failed = true;
                        if (editBatch.RollbackOnFailure)
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                foreach (var feature in editBatch.Creates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await TryVersionedCreateAsync(layerId, versionId, feature, editBatch.RowSecurityWriteCheck, connection, transaction, createdIds, createResults, cancellationToken).ConfigureAwait(false))
                    {
                        failed = true;
                        if (editBatch.RollbackOnFailure) break;
                    }
                }

                if (!(failed && editBatch.RollbackOnFailure))
                {
                    foreach (var feature in editBatch.Updates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!await TryVersionedUpdateAsync(layerId, versionId, feature, editBatch.RowSecurityWriteCheck, OwnerEditGuard.Find(editBatch.OwnerGuards, feature.Id), preconditions, connection, transaction, updateResults, cancellationToken).ConfigureAwait(false))
                        {
                            failed = true;
                            if (editBatch.RollbackOnFailure) break;
                        }
                    }
                }

                if (!(failed && editBatch.RollbackOnFailure))
                {
                    foreach (var objectId in editBatch.Deletes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FeatureEditPrecondition precondition = default;
                        var hasPrecondition = preconditions?.TryGetValue(objectId, out precondition) == true;
                        if (!await TryVersionedDeleteAsync(
                            new(new(connection, transaction, new(VersionEditsTableName, layerId, (versionId, objectId))),
                                editBatch.RowSecurityWriteCheck,
                                OwnerEditGuard.Find(editBatch.OwnerGuards, objectId),
                                hasPrecondition ? precondition : null),
                            deleteResults, cancellationToken).ConfigureAwait(false))
                        {
                            failed = true;
                            if (editBatch.RollbackOnFailure) break;
                        }
                    }
                }
            }

            if (failed && editBatch.RollbackOnFailure)
            {
                await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
                return FeatureEditResult.Rollback(
                    createResults.ToImmutable(), updateResults.ToImmutable(), deleteResults.ToImmutable());
            }

            var immutableCreateResults = createResults.ToImmutable();
            var immutableUpdateResults = updateResults.ToImmutable();
            var immutableDeleteResults = deleteResults.ToImmutable();
            var versionedResult = FeatureEditResult.Success(
                immutableCreateResults.Count(static r => r.IsSuccess),
                immutableUpdateResults.Count(static r => r.IsSuccess),
                immutableDeleteResults.Count(static r => r.IsSuccess),
                createdIds.ToImmutable(),
                immutableCreateResults,
                immutableUpdateResults,
                immutableDeleteResults,
                wasRolledBack: false);
            return await CommitStagedResultAsync(connection, transaction, versionedResult, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: unexpected failure outside the per-operation try/catch below
            // (e.g. transaction/GUC setup) must still log, roll back the whole versioned batch, and
            // report every operation as failed.
            Log.ApplyEditsFailed(_logger, layerId, editBatch.TotalOperations, ex);
            await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
            var commitOutcomeUnknown = ex is FeatureEditCommitOutcomeUnknownException;
            var errorMessage = commitOutcomeUnknown
                ? "Versioned transaction commit outcome is unknown."
                : "Versioned edit batch failed.";
            var (c, u, d) = CreateFailedOperationResults(editBatch, errorMessage, commitOutcomeUnknown);
            return commitOutcomeUnknown
                ? FeatureEditResult.FailureWithUnknownCommitOutcome(c, u, d)
                : FeatureEditResult.Rollback(c, u, d);
        }
    }

    private async Task<bool> ApplyVersionedOperationAsync(
        int layerId,
        Guid versionId,
        FeatureEditOperation operation,
        SqlFragment? writeCheck,
        ImmutableArray<OwnerEditGuard> ownerGuards,
        Dictionary<long, FeatureEditPrecondition>? preconditions,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<long>.Builder createdIds,
        ImmutableArray<EditOperationResult>.Builder createResults,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case FeatureEditOperationKind.Create:
                return await TryVersionedCreateAsync(
                    layerId, versionId,
                    operation.Feature ?? throw new InvalidOperationException("Ordered create is missing the feature payload."),
                    writeCheck, connection, transaction, createdIds, createResults, cancellationToken).ConfigureAwait(false);
            case FeatureEditOperationKind.Update:
                var updateFeature = operation.Feature ?? throw new InvalidOperationException("Ordered update is missing the feature payload.");
                return await TryVersionedUpdateAsync(
                    layerId, versionId,
                    updateFeature,
                    writeCheck,
                    OwnerEditGuard.Find(ownerGuards, updateFeature.Id),
                    preconditions,
                    connection, transaction, updateResults, cancellationToken).ConfigureAwait(false);
            case FeatureEditOperationKind.Delete:
                var deleteObjectId = operation.ObjectId ?? throw new InvalidOperationException("Ordered delete is missing the target object id.");
                FeatureEditPrecondition precondition = default;
                var hasPrecondition = preconditions?.TryGetValue(deleteObjectId, out precondition) == true;
                return await TryVersionedDeleteAsync(
                    new(new(connection, transaction, new(VersionEditsTableName, layerId, (versionId, deleteObjectId))),
                        writeCheck,
                        OwnerEditGuard.Find(ownerGuards, deleteObjectId),
                        hasPrecondition ? precondition : null),
                    deleteResults, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unsupported ordered edit operation kind '{operation.Kind}'.");
        }
    }

    private static async Task<TResult> RunVersionedRowWithSavepointAsync<TResult>(
        NpgsqlTransaction transaction,
        Func<Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        await transaction.SaveAsync(VersionedRowSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await action().ConfigureAwait(false);
            await transaction.ReleaseAsync(VersionedRowSavepoint, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(VersionedRowSavepoint, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(VersionedRowSavepoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SetVersionGucAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        // set_config(..., is_local => true) is the parameterized equivalent of SET LOCAL and keeps the
        // version id off the SQL text. The GUC is read by the change-tracking trigger family.
        await using var command = new NpgsqlCommand(
            "SELECT set_config('honua.gdb_version', $1, true)", connection)
        {
            Transaction = transaction
        };
        command.Parameters.AddWithValue(versionId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryVersionedCreateAsync(
        int layerId,
        Guid versionId,
        Feature feature,
        SqlFragment? writeCheck,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<long>.Builder createdIds,
        ImmutableArray<EditOperationResult>.Builder createResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
            ValidateGeometrySrid(feature.Geometry, layerSrid);
            var geometryValueExpression = _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$3", layerSrid);
            var attributesJson = SerializeToJsonString(feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

            // Allocate a fresh objectid from the base features sequence so a later post can insert the
            // branch-created row into DEFAULT without colliding with concurrently created DEFAULT rows.
            var sql = string.Create(CultureInfo.InvariantCulture, $@"
                INSERT INTO {VersionEditsTableName}
                    (version_id, layer_id, objectid, operation, geometry, attributes, base_geometry, base_attributes)
                VALUES (
                    $1, $2,
                    nextval(pg_get_serial_sequence('{_tableName}', 'objectid')),
                    1, {geometryValueExpression}, $4, NULL, NULL)
                RETURNING objectid");

            var (newId, operationResult) = await RunVersionedRowWithSavepointAsync(
                transaction,
                async () =>
                {
                    await using var command = new NpgsqlCommand(sql, connection) { Transaction = transaction };
                    ApplyCommandTimeout(command, _queryTimeoutSeconds);
                    command.Parameters.AddWithValue(versionId);
                    command.Parameters.AddWithValue(layerId);
                    command.Parameters.Add(new NpgsqlParameter { Value = feature.Geometry ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
                    command.Parameters.Add(new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });

                    await BeginOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var createdId = await RunRowSecurityCheckedWriteAsync(
                        writeCheck,
                        connection,
                        transaction,
                        VersionEditsTableName,
                        layerId,
                        versionId,
                        async () =>
                        {
                            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                            var id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                            return (id, id);
                        },
                        cancellationToken).ConfigureAwait(false);
                    var evidence = await ReadOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var opResult = EditOperationResult.Success(createdId, feature.Attributes.GetValueOrDefault("globalId")?.ToString()) with { OperationEvidence = evidence };
                    return (createdId, opResult);
                },
                cancellationToken).ConfigureAwait(false);

            createdIds.Add(newId);
            createResults.Add(operationResult);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            createResults.Add(CreateFailedOperationResult(ex, "Create"));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            createResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
            return false;
        }
    }

    private async Task<bool> TryVersionedUpdateAsync(
        int layerId,
        Guid versionId,
        Feature feature,
        SqlFragment? writeCheck,
        OwnerEditGuard? ownerGuard,
        Dictionary<long, FeatureEditPrecondition>? preconditions,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        CancellationToken cancellationToken)
    {
        FeatureEditPrecondition precondition = default;
        var hasPrecondition = preconditions?.TryGetValue(feature.Id, out precondition) == true;
        try
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
            ValidateGeometrySrid(feature.Geometry, layerSrid);

            var (writtenFeature, operationResult) = await RunVersionedRowWithSavepointAsync(
                transaction,
                async () =>
                {
                    var update = await PrepareVersionedUpdateAsync(feature, new(
                        new(connection, transaction, new(VersionEditsTableName, layerId, (versionId, feature.Id))),
                        writeCheck,
                        hasPrecondition ? precondition : null), cancellationToken).ConfigureAwait(false);

                    await BeginOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var committedFeature = await RunRowSecurityCheckedWriteAsync(
                        writeCheck,
                        connection,
                        transaction,
                        VersionEditsTableName,
                        layerId,
                        versionId,
                        async () =>
                        {
                            var returnedFeature = await UpsertOverlayRowAsync(
                                layerId, versionId, feature.Id, operation: 2, update.Geometry, update.Attributes,
                                geometryStorageType, layerSrid, isDelete: false, ownerGuard, connection, transaction, cancellationToken).ConfigureAwait(false);
                            if (!returnedFeature.HasValue)
                            {
                                throw new ResourceNotFoundException($"Feature {feature.Id} not found");
                            }

                            return (feature.Id, returnedFeature.Value);
                        },
                        cancellationToken).ConfigureAwait(false);

                    var evidence = await ReadOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var opResult = EditOperationResult.Success(feature.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()) with { OperationEvidence = evidence };
                    return (committedFeature, opResult);
                },
                cancellationToken).ConfigureAwait(false);

            if (hasPrecondition)
            {
                preconditions![feature.Id] = precondition with { ExpectedStateToken = FeatureStateToken.Compute(writtenFeature) };
            }

            updateResults.Add(operationResult);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FeatureEditPreconditionFailedException ex)
        {
            updateResults.Add(EditOperationResult.PreconditionFailed(feature.Id) with { PreconditionFailureFeature = ex.CurrentFeature });
            return false;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            updateResults.Add(CreateFailedOperationResult(ex, "Update", feature.Id));
            return false;
        }
        catch (ResourceNotFoundException)
        {
            updateResults.Add(EditOperationResult.Failure($"Feature {feature.Id} not found", objectId: feature.Id));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            updateResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Update"), objectId: feature.Id));
            return false;
        }
    }

    private sealed record VersionUpdatePreparation(
        RowSecurityContext Row,
        SqlFragment? WriteCheck,
        FeatureEditPrecondition? Precondition);

    private async Task<Feature> PrepareVersionedUpdateAsync(
        Feature feature, VersionUpdatePreparation preparation, CancellationToken cancellationToken)
    {
        var (row, writeCheck, precondition) = preparation;
        if (precondition is { ExpectedRowAbsent: true })
        {
            await ReserveExpectedAbsenceKeyAsync(row, cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadEffectiveVersionRowForUpdateAsync(row, cancellationToken).ConfigureAwait(false);
        if (precondition is { ExpectedRowAbsent: true } && !current.Feature.HasValue)
        {
            throw new ResourceNotFoundException($"Feature {feature.Id} not found");
        }

        await EnsureExistingRowVisibleAsync(writeCheck, new(row.Connection, row.Transaction,
            new(current.TableName, row.Target.LayerId, (current.VersionId, feature.Id))), cancellationToken).ConfigureAwait(false);
        if (precondition.HasValue)
        {
            var checkedRow = ValidatePreconditionAgainstRow(row.Target.LayerId, feature.Id, current.Feature, precondition.Value);
            if (!checkedRow.HasValue)
            {
                throw new ResourceNotFoundException($"Feature {feature.Id} not found");
            }

            return PreserveOmittedAttributes(feature, checkedRow.Value);
        }

        if (!current.Feature.HasValue)
        {
            throw new ResourceNotFoundException($"Feature {feature.Id} not found");
        }

        return PreserveOmittedAttributes(feature, current.Feature.Value);
    }

    private sealed record VersionDeleteOperation(
        RowSecurityContext Context,
        SqlFragment? WriteCheck,
        OwnerEditGuard? OwnerGuard,
        FeatureEditPrecondition? Precondition);

    private async Task<bool> TryVersionedDeleteAsync(
        VersionDeleteOperation operation,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
    {
        var (context, writeCheck, ownerGuard, precondition) = operation;
        var connection = context.Connection;
        var transaction = context.Transaction!;
        var layerId = context.Target.LayerId;
        var versionId = context.Target.Row.VersionId!.Value;
        var objectId = context.Target.Row.ObjectId;
        try
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);

            var deleteResult = await RunVersionedRowWithSavepointAsync(
                transaction,
                async () =>
                {
                    if (precondition is { ExpectedRowAbsent: true })
                    {
                        await ReserveExpectedAbsenceKeyAsync(
                            new RowSecurityContext(connection, transaction, new(VersionEditsTableName, layerId, (versionId, objectId))),
                            cancellationToken).ConfigureAwait(false);
                    }

                    var current = await ReadEffectiveVersionRowForUpdateAsync(new RowSecurityContext(connection, transaction,
                        new(VersionEditsTableName, layerId, (versionId, objectId))), cancellationToken).ConfigureAwait(false);
                    if (precondition is { ExpectedRowAbsent: true } && !current.Feature.HasValue)
                    {
                        return EditOperationResult.Success(objectId);
                    }

                    await EnsureExistingRowVisibleAsync(writeCheck, new RowSecurityContext(connection, transaction,
                        new(current.TableName, layerId, (current.VersionId, objectId))), cancellationToken).ConfigureAwait(false);
                    if (precondition.HasValue)
                    {
                        ValidatePreconditionAgainstRow(layerId, objectId, current.Feature, precondition.Value);
                    }
                    else if (!current.Feature.HasValue)
                    {
                        return EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId);
                    }

                    await BeginOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var deletedRow = await UpsertOverlayRowAsync(
                        layerId, versionId, objectId, operation: 3, geometry: null, attributes: null,
                        geometryStorageType, layerSrid, isDelete: true, ownerGuard, connection, transaction, cancellationToken).ConfigureAwait(false);

                    if (!deletedRow.HasValue)
                    {
                        return EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId);
                    }

                    var evidence = await ReadOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    return EditOperationResult.Success(objectId) with { OperationEvidence = evidence };
                },
                cancellationToken).ConfigureAwait(false);

            deleteResults.Add(deleteResult);
            return deleteResult.IsSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FeatureEditPreconditionFailedException ex)
        {
            deleteResults.Add(EditOperationResult.PreconditionFailed(objectId) with { PreconditionFailureFeature = ex.CurrentFeature });
            return false;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            deleteResults.Add(CreateFailedOperationResult(ex, "Delete", objectId));
            return false;
        }
        catch (ResourceNotFoundException)
        {
            deleteResults.Add(EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            deleteResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Delete"), objectId: objectId));
            return false;
        }
    }

    private async Task<(Feature? Feature, string TableName, Guid? VersionId)> ReadEffectiveVersionRowForUpdateAsync(
        RowSecurityContext context, CancellationToken cancellationToken)
    {
        var (connection, transaction, target) = context;
        var layerId = target.LayerId;
        var versionId = target.Row.VersionId!.Value;
        var objectId = target.Row.ObjectId;
        // Lock DEFAULT first to serialize a version's first overlay write with other writers.
        var baseRow = await ReadFeatureForUpdateAsync(layerId, objectId, connection, transaction!, cancellationToken).ConfigureAwait(false);
        var storage = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometry = _geometryProcessor.GetGeometrySelectExpression(storage, new FeatureQuery());
        var sql = $"SELECT objectid, {geometry}, attributes, operation FROM {VersionEditsTableName} " +
            "WHERE version_id = $1 AND layer_id = $2 AND objectid = $3 FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(versionId);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (baseRow, _tableName, null);
        }
        var effective = reader.GetInt16(3) == 3
            ? (Feature?)null
            : ReadFeature(reader);
        return (effective, VersionEditsTableName, versionId);
    }

    private async Task<Feature?> UpsertOverlayRowAsync(
        int layerId,
        Guid versionId,
        long objectId,
        short operation,
        byte[]? geometry,
        ImmutableDictionary<string, object?>? attributes,
        Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType geometryStorageType,
        int? layerSrid,
        bool isDelete,
        OwnerEditGuard? ownerGuard,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var geometryValueExpression = isDelete
            ? "NULL"
            : _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$4", layerSrid);
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());
        var attributesJson = isDelete
            ? null
            : SerializeToJsonString((attributes ?? ImmutableDictionary<string, object?>.Empty).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

        // The CTE resolves whether the version already shadows the row (overlay) and the current DEFAULT
        // base image, then INSERT ... ON CONFLICT upserts the overlay. base_* is captured from DEFAULT only
        // when no prior overlay row exists (first touch); the ON CONFLICT branch preserves the existing
        // base. The mutation is a no-op (0 rows) when neither an overlay row nor a DEFAULT row exists.
        var baseOwner = OwnerMatchExpression(ownerGuard, "b.attributes", 7, 8);
        var existingOwner = OwnerMatchExpression(ownerGuard, "existing.attributes", 7, 8);
        var baseLock = ownerGuard is null ? string.Empty : " FOR UPDATE OF b";
        var existingProjection = ownerGuard is null ? "1" : "attributes, operation";
        var existingLock = ownerGuard is null ? string.Empty : " FOR UPDATE";
        // An overlay shadows DEFAULT even when its owner differs or it is a tombstone.
        // Only an absent overlay permits admission against the base owner. Recheck the
        // conflict row as well so the upsert never replaces a row with another owner.
        var admission = ownerGuard is null
            ? "WHERE EXISTS (SELECT 1 FROM base) OR EXISTS (SELECT 1 FROM existing)"
            : $"WHERE EXISTS (SELECT 1 FROM existing WHERE operation <> 3 AND {existingOwner}) OR (NOT EXISTS (SELECT 1 FROM existing) AND EXISTS (SELECT 1 FROM base))";
        var conflictOwner = OwnerMatchExpression(ownerGuard, "current_edit.attributes", 7, 8);
        var conflictAdmission = ownerGuard is null
            ? string.Empty
            : $"WHERE current_edit.operation <> 3 AND {conflictOwner}";
        var sql = string.Create(CultureInfo.InvariantCulture, $@"
            WITH base AS (
                SELECT b.geometry AS base_geom, b.attributes AS base_attrs
                FROM {_tableName} b
                WHERE b.layer_id = $2 AND b.objectid = $3{(baseOwner is null ? string.Empty : " AND " + baseOwner)}{baseLock}
            ),
            existing AS (
                SELECT {existingProjection} FROM {VersionEditsTableName}
                WHERE version_id = $1 AND layer_id = $2 AND objectid = $3{existingLock}
            )
            INSERT INTO {VersionEditsTableName} AS current_edit
                (version_id, layer_id, objectid, operation, geometry, attributes, base_geometry, base_attributes)
            SELECT $1, $2, $3, $5::smallint, {geometryValueExpression}, $6::jsonb,
                   (SELECT base_geom FROM base), (SELECT base_attrs FROM base)
            {admission}
            ON CONFLICT (version_id, layer_id, objectid) DO UPDATE
                SET operation = EXCLUDED.operation,
                    geometry = EXCLUDED.geometry,
                    attributes = EXCLUDED.attributes,
                    modified_at = now()
                {conflictAdmission}
            RETURNING objectid, {geometrySelect}, attributes");

        await using var command = new NpgsqlCommand(sql, connection) { Transaction = transaction };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(versionId);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(objectId);
        command.Parameters.Add(new NpgsqlParameter { Value = geometry ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.AddWithValue((short)operation);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)attributesJson ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        AddOwnerGuardParameters(command, ownerGuard);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadFeature(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ResourceConflictException("Versioned edit conflicted with existing overlay data.", ex);
        }
    }
}
