// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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
        var isolationLevel = ResolveVersionedIsolationLevel(editBatch);
        var (txConnection, dbTransaction) = await _connectionProvider
            .OpenTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        await using var _ = txConnection;
        var connection = txConnection.RequireNpgsqlConnection();
        var transaction = (NpgsqlTransaction)dbTransaction;
        await using var __ = transaction;

        try
        {
            await SetVersionGucAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);
            var session = new VersionedBatchSession
            {
                LayerId = layerId,
                VersionId = versionId,
                EditBatch = editBatch,
                Preconditions = BuildPreconditionMap(editBatch),
                Connection = connection,
                Transaction = transaction
            };
            return await ExecuteVersionedBatchBodyAsync(session, cancellationToken).ConfigureAwait(false);
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
            return BuildVersionedFailureResult(editBatch, ex);
        }
    }

    private Task<bool> ApplyVersionedOperationAsync(
        VersionedBatchSession session,
        FeatureEditOperation operation,
        CancellationToken cancellationToken)
    {
        return operation.Kind switch
        {
            FeatureEditOperationKind.Create => TryVersionedCreateAsync(
                session,
                operation.Feature ?? throw new InvalidOperationException("Ordered create is missing the feature payload."),
                cancellationToken),
            FeatureEditOperationKind.Update => TryVersionedUpdateAsync(
                session,
                operation.Feature ?? throw new InvalidOperationException("Ordered update is missing the feature payload."),
                cancellationToken),
            FeatureEditOperationKind.Delete => TryVersionedDeleteAsync(
                session,
                operation.ObjectId ?? throw new InvalidOperationException("Ordered delete is missing the target object id."),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported ordered edit operation kind '{operation.Kind}'.")
        };
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
        VersionedBatchSession session,
        Feature feature,
        CancellationToken cancellationToken)
    {
        try
        {
            var (newId, operationResult) = await InsertVersionedRowAsync(session, feature, cancellationToken)
                .ConfigureAwait(false);
            session.CreatedIds.Add(newId);
            session.CreateResults.Add(operationResult);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            session.CreateResults.Add(CreateFailedOperationResult(ex, "Create"));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            session.CreateResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
            return false;
        }
    }

    private async Task<bool> TryVersionedUpdateAsync(
        VersionedBatchSession session,
        Feature feature,
        CancellationToken cancellationToken)
    {
        var precondition = session.FindPrecondition(feature.Id);
        try
        {
            var (writtenFeature, operationResult) = await ExecuteVersionedUpdateRowAsync(session, feature, cancellationToken)
                .ConfigureAwait(false);
            if (precondition.HasValue)
            {
                session.Preconditions![feature.Id] = precondition.Value with { ExpectedStateToken = FeatureStateToken.Compute(writtenFeature) };
            }

            session.UpdateResults.Add(operationResult);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FeatureEditPreconditionFailedException ex)
        {
            session.UpdateResults.Add(EditOperationResult.PreconditionFailed(feature.Id) with { PreconditionFailureFeature = ex.CurrentFeature });
            return false;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            session.UpdateResults.Add(CreateFailedOperationResult(ex, "Update", feature.Id));
            return false;
        }
        catch (ResourceNotFoundException)
        {
            session.UpdateResults.Add(EditOperationResult.Failure($"Feature {feature.Id} not found", objectId: feature.Id));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            session.UpdateResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Update"), objectId: feature.Id));
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

    private async Task<bool> TryVersionedDeleteAsync(
        VersionedBatchSession session,
        long objectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleteResult = await ExecuteVersionedDeleteRowAsync(session, objectId, cancellationToken)
                .ConfigureAwait(false);
            session.DeleteResults.Add(deleteResult);
            return deleteResult.IsSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FeatureEditPreconditionFailedException ex)
        {
            session.DeleteResults.Add(EditOperationResult.PreconditionFailed(objectId) with { PreconditionFailureFeature = ex.CurrentFeature });
            return false;
        }
        catch (RowSecurityWriteCheckFailedException ex)
        {
            session.DeleteResults.Add(CreateFailedOperationResult(ex, "Delete", objectId));
            return false;
        }
        catch (ResourceNotFoundException)
        {
            session.DeleteResults.Add(EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: a per-operation failure in a versioned batch must be sanitized
            // via GetSafeEditOperationError and let the rest of the batch continue.
            session.DeleteResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Delete"), objectId: objectId));
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
