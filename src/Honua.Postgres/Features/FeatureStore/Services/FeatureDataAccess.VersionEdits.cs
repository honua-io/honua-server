// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    // Branch-versioning overlay writes (#1272 Track B, ADR-0051). A non-DEFAULT edit batch routes the
    // INSERT/UPDATE/DELETE to honua.version_edits inside one transaction, sets the transaction-scoped
    // honua.gdb_version GUC, and captures the DEFAULT base image on the version's first touch of an
    // (layer_id, objectid). DEFAULT data (`features`) is never touched by this path; the DEFAULT write
    // path is left completely unchanged (byte-identical) and never reaches this file.
    //
    // The overlay's BEFORE trigger (049_CreateVersionEdits.sql) records each mutation into
    // honua.feature_changes tagged with the producing version so reconcile/post (Slice 2) have a tagged
    // version delta even though DEFAULT was never written.

    private const string VersionEditsTableName = "honua.version_edits";

    /// <summary>
    /// Applies an edit batch against a branch version overlay. Always transactional so the GUC
    /// (<c>SET LOCAL honua.gdb_version</c>) and the overlay upserts commit atomically. DEFAULT batches do
    /// not reach here (see <see cref="ApplyEditsAsync"/>).
    /// </summary>
    private async Task<FeatureEditResult> ApplyVersionedEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        VersionContext version,
        CancellationToken cancellationToken)
    {
        var versionId = version.VersionId
            ?? throw new InvalidOperationException("Versioned edit batch is missing a version id.");

        var (txConnection, dbTransaction) = await _connectionProvider
            .OpenTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using var _ = txConnection;
        var connection = txConnection.RequireNpgsqlConnection();
        var transaction = (NpgsqlTransaction)dbTransaction;
        await using var __ = transaction;

        try
        {
            await SetVersionGucAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);

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
                        layerId, versionId, operation, connection, transaction,
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
                    if (!await TryVersionedCreateAsync(layerId, versionId, feature, connection, transaction, createdIds, createResults, cancellationToken).ConfigureAwait(false))
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
                        if (!await TryVersionedUpdateAsync(layerId, versionId, feature, connection, transaction, updateResults, cancellationToken).ConfigureAwait(false))
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
                        if (!await TryVersionedDeleteAsync(layerId, versionId, objectId, connection, transaction, deleteResults, cancellationToken).ConfigureAwait(false))
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

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var immutableCreateResults = createResults.ToImmutable();
            var immutableUpdateResults = updateResults.ToImmutable();
            var immutableDeleteResults = deleteResults.ToImmutable();

            return FeatureEditResult.Success(
                immutableCreateResults.Count(static r => r.IsSuccess),
                immutableUpdateResults.Count(static r => r.IsSuccess),
                immutableDeleteResults.Count(static r => r.IsSuccess),
                createdIds.ToImmutable(),
                immutableCreateResults,
                immutableUpdateResults,
                immutableDeleteResults,
                wasRolledBack: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ApplyEditsFailed(_logger, layerId, editBatch.TotalOperations, ex);
            await RollbackIfNeededAsync(transaction).ConfigureAwait(false);
            var (c, u, d) = CreateFailedOperationResults(editBatch, "Versioned edit batch failed.");
            return FeatureEditResult.Rollback(c, u, d);
        }
    }

    private async Task<bool> ApplyVersionedOperationAsync(
        int layerId,
        Guid versionId,
        FeatureEditOperation operation,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<long>.Builder createdIds,
        ImmutableArray<EditOperationResult>.Builder createResults,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
        => operation.Kind switch
        {
            FeatureEditOperationKind.Create => await TryVersionedCreateAsync(
                layerId, versionId,
                operation.Feature ?? throw new InvalidOperationException("Ordered create is missing the feature payload."),
                connection, transaction, createdIds, createResults, cancellationToken).ConfigureAwait(false),
            FeatureEditOperationKind.Update => await TryVersionedUpdateAsync(
                layerId, versionId,
                operation.Feature ?? throw new InvalidOperationException("Ordered update is missing the feature payload."),
                connection, transaction, updateResults, cancellationToken).ConfigureAwait(false),
            FeatureEditOperationKind.Delete => await TryVersionedDeleteAsync(
                layerId, versionId,
                operation.ObjectId ?? throw new InvalidOperationException("Ordered delete is missing the target object id."),
                connection, transaction, deleteResults, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported ordered edit operation kind '{operation.Kind}'.")
        };

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

            await using var command = new NpgsqlCommand(sql, connection) { Transaction = transaction };
            ApplyCommandTimeout(command, _queryTimeoutSeconds);
            command.Parameters.AddWithValue(versionId);
            command.Parameters.AddWithValue(layerId);
            command.Parameters.Add(new NpgsqlParameter { Value = feature.Geometry ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
            command.Parameters.Add(new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var newId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            createdIds.Add(newId);
            createResults.Add(EditOperationResult.Success(newId, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            return true;
        }
        catch (Exception ex)
        {
            createResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Create")));
            return false;
        }
    }

    private async Task<bool> TryVersionedUpdateAsync(
        int layerId,
        Guid versionId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<EditOperationResult>.Builder updateResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);
            ValidateGeometrySrid(feature.Geometry, layerSrid);

            var rowsAffected = await UpsertOverlayRowAsync(
                layerId, versionId, feature.Id, operation: 2, feature.Geometry, feature.Attributes,
                geometryStorageType, layerSrid, isDelete: false, connection, transaction, cancellationToken).ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                updateResults.Add(EditOperationResult.Failure($"Feature {feature.Id} not found", objectId: feature.Id));
                return false;
            }

            updateResults.Add(EditOperationResult.Success(feature.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
            return true;
        }
        catch (Exception ex)
        {
            updateResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Update"), objectId: feature.Id));
            return false;
        }
    }

    private async Task<bool> TryVersionedDeleteAsync(
        int layerId,
        Guid versionId,
        long objectId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImmutableArray<EditOperationResult>.Builder deleteResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
            var layerSrid = await _cacheManager.GetLayerSridAsync(layerId, cancellationToken).ConfigureAwait(false);

            var rowsAffected = await UpsertOverlayRowAsync(
                layerId, versionId, objectId, operation: 3, geometry: null, attributes: null,
                geometryStorageType, layerSrid, isDelete: true, connection, transaction, cancellationToken).ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                deleteResults.Add(EditOperationResult.Failure($"Feature {objectId} not found", objectId: objectId));
                return false;
            }

            deleteResults.Add(EditOperationResult.Success(objectId));
            return true;
        }
        catch (Exception ex)
        {
            deleteResults.Add(EditOperationResult.Failure(GetSafeEditOperationError(ex, "Delete"), objectId: objectId));
            return false;
        }
    }

    /// <summary>
    /// Upserts a branch overlay row for an existing (layer_id, objectid). On the version's FIRST touch the
    /// then-current DEFAULT base image is snapshotted into base_geometry/base_attributes; subsequent
    /// touches keep the captured base. Returns the number of affected overlay rows; 0 when neither a
    /// DEFAULT row nor a prior overlay row exists (the target feature does not exist for the version).
    /// </summary>
    private async Task<int> UpsertOverlayRowAsync(
        int layerId,
        Guid versionId,
        long objectId,
        short operation,
        byte[]? geometry,
        ImmutableDictionary<string, object?>? attributes,
        Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType geometryStorageType,
        int? layerSrid,
        bool isDelete,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var geometryValueExpression = isDelete
            ? "NULL"
            : _geometryProcessor.GetGeometryWriteExpression(geometryStorageType, "$4", layerSrid);
        var attributesJson = isDelete
            ? null
            : SerializeToJsonString((attributes ?? ImmutableDictionary<string, object?>.Empty).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

        // The CTE resolves whether the version already shadows the row (overlay) and the current DEFAULT
        // base image, then INSERT ... ON CONFLICT upserts the overlay. base_* is captured from DEFAULT only
        // when no prior overlay row exists (first touch); the ON CONFLICT branch preserves the existing
        // base. The mutation is a no-op (0 rows) when neither an overlay row nor a DEFAULT row exists.
        var sql = string.Create(CultureInfo.InvariantCulture, $@"
            WITH base AS (
                SELECT b.geometry AS base_geom, b.attributes AS base_attrs
                FROM {_tableName} b
                WHERE b.layer_id = $2 AND b.objectid = $3
            ),
            existing AS (
                SELECT 1 FROM {VersionEditsTableName}
                WHERE version_id = $1 AND layer_id = $2 AND objectid = $3
            )
            INSERT INTO {VersionEditsTableName}
                (version_id, layer_id, objectid, operation, geometry, attributes, base_geometry, base_attributes)
            SELECT $1, $2, $3, $5::smallint, {geometryValueExpression}, $6::jsonb,
                   (SELECT base_geom FROM base), (SELECT base_attrs FROM base)
            WHERE EXISTS (SELECT 1 FROM base) OR EXISTS (SELECT 1 FROM existing)
            ON CONFLICT (version_id, layer_id, objectid) DO UPDATE
                SET operation = EXCLUDED.operation,
                    geometry = EXCLUDED.geometry,
                    attributes = EXCLUDED.attributes,
                    modified_at = now()");

        await using var command = new NpgsqlCommand(sql, connection) { Transaction = transaction };
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        command.Parameters.AddWithValue(versionId);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(objectId);
        command.Parameters.Add(new NpgsqlParameter { Value = geometry ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.AddWithValue((short)operation);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)attributesJson ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ResourceConflictException("Versioned edit conflicted with existing overlay data.", ex);
        }
    }
}
