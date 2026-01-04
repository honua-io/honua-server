// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Handles database data access operations for PostgreSQL feature store
/// </summary>
internal sealed partial class FeatureDataAccess : IFeatureDataAccess
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly IGeometryProcessor _geometryProcessor;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly ObjectPool<Dictionary<string, object?>> _dictionaryPool;
    private readonly PreparedStatementCache? _statementCache;
    private readonly ILogger<FeatureDataAccess> _logger;
    private readonly string _tableName;

    public FeatureDataAccess(
        IDatabaseConnectionProvider connectionProvider,
        IGeometryProcessor geometryProcessor,
        IFeatureCacheManager cacheManager,
        ObjectPool<Dictionary<string, object?>> dictionaryPool,
        PreparedStatementCache? statementCache,
        ILogger<FeatureDataAccess> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _geometryProcessor = geometryProcessor ?? throw new ArgumentNullException(nameof(geometryProcessor));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _dictionaryPool = dictionaryPool ?? throw new ArgumentNullException(nameof(dictionaryPool));
        _statementCache = statementCache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var tableNameOnly = "features";
        _tableName = string.IsNullOrEmpty(schemaName) ? tableNameOnly : $"{schemaName}.{tableNameOnly}";
    }

    public async Task<long> ExecuteCountQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var count = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("count", stopwatch.ElapsedMilliseconds, (int)count);

            return count;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("count_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<Feature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select", stopwatch.ElapsedMilliseconds, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<GmlFeature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadGmlFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_gml", stopwatch.ElapsedMilliseconds, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_gml_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

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

    public async Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());

        var sql = $@"
            SELECT objectid, {geometrySelect}, attributes
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    public async Task<FeatureExtent?> GetExtentAsync(int layerId, CoreParameterizedQuery? query, CancellationToken cancellationToken)
    {
        if (query == null)
        {
            return null;
        }

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query.Sql, connection);

        command.Parameters.AddWithValue(layerId);
        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        var minx = reader.GetDouble(0);
        var miny = reader.GetDouble(1);
        var maxx = reader.GetDouble(2);
        var maxy = reader.GetDouble(3);

        return FeatureExtent.Create(minx, miny, maxx, maxy, 4326);
    }

    public async Task<byte[]?> GetMvtTileAsync(int layerId, CoreParameterizedQuery query, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query.Sql, connection);

        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetFieldValue<byte[]>(0);
    }

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(int layerId, CoreParameterizedQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query.Sql, connection);

        command.Parameters.AddWithValue(layerId);
        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return await ReadFeatureAsync(reader, cancellationToken);
        }
    }

    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(int layerId, CoreParameterizedQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(query.Sql, connection);

        command.Parameters.AddWithValue(layerId);
        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return await ReadGmlFeatureAsync(reader, cancellationToken);
        }
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
                    await transaction.RollbackAsync(cancellationToken);
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
                await transaction.RollbackAsync(cancellationToken);

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

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 6121,
            Level = LogLevel.Error,
            Message = "ApplyEdits failed for layer {LayerId} with {OperationCount} operations.")]
        public static partial void ApplyEditsFailed(ILogger logger, int layerId, int operationCount, Exception exception);
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
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    private async Task<Feature> ReadFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        var attributesJson = reader.GetString(2);

        // Use pooled dictionary for performance
        var attributesDictionary = _dictionaryPool.Get();
        try
        {
            // Deserialize JSON using AOT-compatible source generators
            var deserializedDict = DeserializeFromJsonString(attributesJson) ?? new Dictionary<string, object?>();

            // Convert JsonElement values to primitive types for compatibility
            foreach (var (key, value) in deserializedDict)
            {
                attributesDictionary[key] = ConvertJsonElementToObject(value);
            }

            // Inject objectid into attributes for GeoServices FeatureServer compatibility
            attributesDictionary["objectid"] = id;

            var attributes = attributesDictionary.ToImmutableDictionary();
            return Feature.Create(id, geometry, attributes);
        }
        finally
        {
            _dictionaryPool.Return(attributesDictionary);
        }
    }

    private async Task<GmlFeature> ReadGmlFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometryGml = reader.IsDBNull(1) ? null : reader.GetString(1);
        var attributesJson = reader.GetString(2);

        // Use pooled dictionary for performance
        var attributesDictionary = _dictionaryPool.Get();
        try
        {
            // Deserialize JSON using AOT-compatible source generators
            var deserializedDict = DeserializeFromJsonString(attributesJson) ?? new Dictionary<string, object?>();

            // Convert JsonElement values to primitive types for compatibility
            foreach (var (key, value) in deserializedDict)
            {
                attributesDictionary[key] = ConvertJsonElementToObject(value);
            }

            // Inject objectid into attributes for GeoServices FeatureServer compatibility
            attributesDictionary["objectid"] = id;

            var attributes = attributesDictionary.ToImmutableDictionary();
            return GmlFeature.Create(id, geometryGml, attributes);
        }
        finally
        {
            _dictionaryPool.Return(attributesDictionary);
        }
    }

    private async Task<NpgsqlCommand> CreateCommandAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand> configureParameters,
        CancellationToken cancellationToken)
    {
        if (_statementCache == null)
        {
            var command = new NpgsqlCommand(sql, connection);
            configureParameters(command);
            return command;
        }

        var prepared = await _statementCache.GetOrCreatePreparedCommandAsync(
            connection,
            sql,
            configureParameters,
            cancellationToken).ConfigureAwait(false);

        if (prepared != null)
        {
            configureParameters(prepared);
            return prepared;
        }

        var fallback = new NpgsqlCommand(sql, connection);
        configureParameters(fallback);
        return fallback;
    }

    private void AddQueryParameters(NpgsqlCommand command, FeatureQuery query, int layerId, List<object> whereParameters)
    {
        var parameterIndex = 0;
        AddParameterValue(command, ref parameterIndex, layerId);

        if (query.SpatialFilter.HasValue &&
            query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor &&
            query.SpatialFilter.Value.ReturnDistance)
        {
            var filter = query.SpatialFilter.Value;

            // Distance geometry is used in SELECT before WHERE, so add it first.
            AddParameterValue(command, ref parameterIndex, filter.Geometry);

            AddWhereParameters(command, whereParameters, ref parameterIndex);

            AddKnnParameters(command, filter, query, ref parameterIndex, includeDistanceGeometry: false);

            if (query.Offset.HasValue)
            {
                AddParameterValue(command, ref parameterIndex, query.Offset.Value);
            }

            return;
        }

        AddWhereParameters(command, whereParameters, ref parameterIndex);

        if (query.SpatialFilter.HasValue)
        {
            AddSpatialFilterParameters(command, query, ref parameterIndex);
        }
        else
        {
            AddRegularPaginationParameters(command, query, ref parameterIndex);
        }

        if (query.Offset.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Offset.Value);
        }
    }

    private static void AddWhereParameters(NpgsqlCommand command, List<object> whereParameters, ref int parameterIndex)
    {
        foreach (var param in whereParameters)
        {
            AddParameterValue(command, ref parameterIndex, param);
        }
    }

    private void AddSpatialFilterParameters(NpgsqlCommand command, FeatureQuery query, ref int parameterIndex)
    {
        var filter = query.SpatialFilter!.Value;

        if (filter.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            AddKnnParameters(command, filter, query, ref parameterIndex);
        }
        else
        {
            AddRegularSpatialParameters(command, filter, query, ref parameterIndex);
        }
    }

    private static void AddKnnParameters(
        NpgsqlCommand command,
        SpatialFilter filter,
        FeatureQuery query,
        ref int parameterIndex,
        bool includeDistanceGeometry = true)
    {
        if (includeDistanceGeometry && filter.ReturnDistance)
        {
            AddParameterValue(command, ref parameterIndex, filter.Geometry);
        }

        AddParameterValue(command, ref parameterIndex, filter.Geometry);

        var limit = filter.NearestCount ?? query.Limit;
        if (limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, limit.Value);
        }
    }

    private void AddRegularSpatialParameters(NpgsqlCommand command, SpatialFilter filter, FeatureQuery query, ref int parameterIndex)
    {
        AddParameterValue(command, ref parameterIndex, filter.Geometry);

        if (filter.SpatialRelationship == SpatialRelationship.WithinDistance ||
            filter.SpatialRelationship == SpatialRelationship.BeyondDistance)
        {
            var distanceInMeters = _geometryProcessor.ConvertDistanceToMeters(filter.Distance ?? 0, filter.DistanceUnit);
            AddParameterValue(command, ref parameterIndex, distanceInMeters);
        }

        if (query.Limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Limit.Value);
        }
    }

    private static void AddRegularPaginationParameters(NpgsqlCommand command, FeatureQuery query, ref int parameterIndex)
    {
        if (query.Limit.HasValue)
        {
            AddParameterValue(command, ref parameterIndex, query.Limit.Value);
        }
    }

    private static void AddParameterValue(NpgsqlCommand command, ref int parameterIndex, object? value)
    {
        var parameterValue = value ?? DBNull.Value;

        if (command.Parameters.Count > parameterIndex)
        {
            command.Parameters[parameterIndex].Value = parameterValue;
        }
        else
        {
            command.Parameters.AddWithValue(parameterValue);
        }

        parameterIndex++;
    }

    private static string SerializeToJsonString(Dictionary<string, object?> dictionary)
    {
        return JsonSerializer.Serialize(dictionary, FeatureAttributesJsonContext.Default.DictionaryStringObject);
    }

    private static Dictionary<string, object?>? DeserializeFromJsonString(string json)
    {
        return JsonSerializer.Deserialize(json, FeatureAttributesJsonContext.Default.DictionaryStringObject);
    }

    private static object? ConvertJsonElementToObject(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                                        element.TryGetDouble(out var doubleVal) ? doubleVal :
                                        element.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value
            };
        }

        return value;
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
