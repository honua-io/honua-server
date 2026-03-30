// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CreateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    public async Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteWithConnectionAsync(layerId, featureId, connection, transaction: null, cancellationToken);
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
    {
        if (editBatch.IsEmpty)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        NpgsqlConnection connection;
        NpgsqlTransaction? transaction;

        if (editBatch.RollbackOnFailure)
        {
            // Use RepeatableRead isolation level for feature edits to prevent phantom reads during batch operations
            var (dbConnection, dbTransaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            connection = (NpgsqlConnection)dbConnection;
            transaction = (NpgsqlTransaction)dbTransaction;
        }
        else
        {
            connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = null;
        }

        await using var _ = connection;
        await using var __ = transaction;

        try
        {
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
                await transaction.RollbackAsync(CancellationToken.None);

                var createResults = System.Linq.Enumerable.Select(editBatch.Creates, _ =>
                    EditOperationResult.Failure("Transaction failed.")).ToImmutableArray();
                var updateResults = System.Linq.Enumerable.Select(editBatch.Updates, feature =>
                    EditOperationResult.Failure("Transaction failed.", objectId: feature.Id)).ToImmutableArray();
                var deleteResults = System.Linq.Enumerable.Select(editBatch.Deletes, id =>
                    EditOperationResult.Failure("Transaction failed.", objectId: id)).ToImmutableArray();

                return FeatureEditResult.Rollback(createResults, updateResults, deleteResults);
            }

            var failedCreateResults = System.Linq.Enumerable.Select(editBatch.Creates, _ =>
                EditOperationResult.Failure("Edit batch failed.")).ToImmutableArray();
            var failedUpdateResults = System.Linq.Enumerable.Select(editBatch.Updates, feature =>
                EditOperationResult.Failure("Edit batch failed.", objectId: feature.Id)).ToImmutableArray();
            var failedDeleteResults = System.Linq.Enumerable.Select(editBatch.Deletes, id =>
                EditOperationResult.Failure("Edit batch failed.", objectId: id)).ToImmutableArray();

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

    private async Task<bool> DeleteWithConnectionAsync(
        int layerId,
        long featureId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
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
        return rowsAffected > 0;
    }

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

    // Simplified batch operations - would need full implementation from original
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

        if (transaction == null && features.Length > 1)
        {
            try
            {
                var batchCreatedIds = await CreateBatchWithConnectionAsync(
                    layerId,
                    features,
                    connection,
                    transaction,
                    cancellationToken);
                var batchResults = new EditOperationResult[batchCreatedIds.Length];
                for (var i = 0; i < batchCreatedIds.Length; i++)
                {
                    batchResults[i] = EditOperationResult.Success(
                        batchCreatedIds[i],
                        features[i].Attributes.GetValueOrDefault("globalId")?.ToString());
                }

                return (batchCreatedIds, batchResults.ToImmutableArray());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Fall back to per-feature inserts so non-transactional edit batches
                // preserve detailed partial-success results when any create fails.
            }
        }

        var createdIds = new List<long>();
        var results = new List<EditOperationResult>();

        foreach (var feature in features)
        {
            try
            {
                var created = await CreateWithConnectionAsync(layerId, feature, connection, transaction, cancellationToken);
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
            try
            {
                await UpdateWithConnectionAsync(layerId, feature, connection, transaction, cancellationToken);
                updatedCount++;
                results.Add(EditOperationResult.Success(feature.Id, feature.Attributes.GetValueOrDefault("globalId")?.ToString()));
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
            try
            {
                var deleted = await DeleteWithConnectionAsync(layerId, featureId, connection, transaction, cancellationToken);
                if (deleted)
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
