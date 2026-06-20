// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// PostgreSQL implementation of feature storage and retrieval using service composition
/// </summary>
/// <remarks>
/// <para>This refactored implementation uses the composition pattern to separate concerns
/// and improve maintainability. The large monolithic class has been broken down into
/// focused services with single responsibilities.</para>
///
/// <para>Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).</para>
///
/// <para><strong>SECURITY NOTICE</strong>: WHERE clause handling has been secured using
/// parameterized queries. The implementation parses simple WHERE expressions (e.g.,
/// 'field = value', 'age > 18') and properly parameterizes all literal values while
/// validating field names to prevent SQL injection attacks.</para>
/// </remarks>
internal sealed class PostgresFeatureStoreRefactored : IFeatureDataProvider, IFeatureReader, IBindableFeatureDataProvider, IRasterPointReader, IFeatureWriter, ITileProvider, IRelationshipStore, IGeoJsonFeatureStore, IGeobufFeatureStore, IFlatGeobufFeatureStore, IGmlFeatureStore, IKmlFeatureStore, IStreamingFeatureStore, IPagedFeatureReader, IPagedGeoJsonFeatureStore, IPagedRawGeoJsonFeatureStore, IPagedRawGeoServicesFeatureStore
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IAdoNetDatabaseConnectionProvider? _connectionProvider;
    private readonly ObjectPool<Dictionary<string, object?>>? _dictionaryPool;
    private readonly IConnectionEncryptionService? _connectionEncryptionService;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly ILogger<PostgresStorageMappedFeatureReader>? _storageMappedReaderLogger;

    public PostgresFeatureStoreRefactored(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager)
        : this(
            queryBuilder,
            dataAccess,
            cacheManager,
            connectionProvider: null,
            dictionaryPool: null,
            connectionEncryptionService: null,
            filterExpressionService: null)
    {
    }

    public PostgresFeatureStoreRefactored(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        IAdoNetDatabaseConnectionProvider? connectionProvider,
        ObjectPool<Dictionary<string, object?>>? dictionaryPool,
        IConnectionEncryptionService? connectionEncryptionService,
        IFilterExpressionService? filterExpressionService = null,
        IMetadataV2GraphProvider? v2Provider = null,
        ILogger<PostgresStorageMappedFeatureReader>? storageMappedReaderLogger = null)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _v2Provider = v2Provider;
        _connectionProvider = connectionProvider;
        _dictionaryPool = dictionaryPool;
        _connectionEncryptionService = connectionEncryptionService;
        _filterExpressionService = filterExpressionService;
        _storageMappedReaderLogger = storageMappedReaderLogger;
    }

    public string ProviderName => DataProviderNames.Postgis;

    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadWritePostgis;

    public IFeatureReader Reader => this;

    public IFeatureWriter Writer => this;

    public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_connectionProvider == null || _dictionaryPool == null)
        {
            return this;
        }

        return new PostgresStorageMappedFeatureReader(
            _connectionProvider,
            _dictionaryPool,
            binding.Resource,
            binding.StorageMapping,
            binding.Connection,
            _connectionEncryptionService,
            _storageMappedReaderLogger);
    }

    #region Core CRUD Operations

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        // Enforce the layer's permanent (row-visibility) filter: a row hidden by
        // the metadata-v2 PermanentFilter must not be readable item-by-id either.
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        if (query.EnforcedSqlFilter is null)
        {
            return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken);
        }

        query = query with { ObjectIds = ImmutableArray.Create(featureId), Limit = 1 };
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
        return features.IsDefaultOrEmpty ? null : features[0];
    }

    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.CreateFeatureAsync(layerId, feature, cancellationToken);
    }

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.UpdateFeatureAsync(layerId, feature, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.DeleteFeatureAsync(layerId, featureId, cancellationToken);
    }

    #endregion

    #region Query Operations

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectQuery,
            _dataAccess.ExecuteSelectQueryAsync,
            QueryOptimizedAsync,
            cancellationToken);

    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectObjectIdsQueryAsync(
            objectIdsQuery,
            query,
            layerId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ProjectedPoint>> QueryProjectedPointsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildProjectedPointQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectProjectedPointsAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return PagedQueryResult<Feature>.Create(result.Items, result.HasMoreResults, result.TotalCount);
        }

        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var pageQuery = query with { Limit = query.Limit.Value + 1 };
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, pageQuery, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, pageQuery, layerId, cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return PagedQueryResult<Feature>.Empty();
        }

        var hasMoreResults = features.Length > query.Limit.Value;
        var items = hasMoreResults
            ? features.Take(query.Limit.Value).ToImmutableArray()
            : features;

        return PagedQueryResult<Feature>.Create(items, hasMoreResults);
    }

    public async Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var resource = await GetMetadataResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectFlatGeobufQuery(resource, layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectFlatGeobufQueryAsync(selectQuery, query, layerId, cancellationToken);
    }

    public async Task<byte[]?> QueryGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var resource = await GetMetadataResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectGeobufQuery(resource, layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectGeobufQueryAsync(selectQuery, query, layerId, cancellationToken);
    }

    public Task<QueryResult<EncodedGeoJsonFeature>> QueryGeoJsonAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectGeoJsonQuery,
            _dataAccess.ExecuteSelectGeoJsonQueryAsync,
            QueryOptimizedGeoJsonAsync,
            cancellationToken);

    public async Task<PagedQueryResult<EncodedGeoJsonFeature>> QueryGeoJsonPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var result = await QueryGeoJsonAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return PagedQueryResult<EncodedGeoJsonFeature>.Create(result.Items, result.HasMoreResults, result.TotalCount);
        }

        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var pageQuery = query with { Limit = query.Limit.Value + 1 };
        var selectQuery = _queryBuilder.BuildSelectGeoJsonQuery(layerId, pageQuery, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectGeoJsonQueryAsync(selectQuery, pageQuery, layerId, cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return PagedQueryResult<EncodedGeoJsonFeature>.Empty();
        }

        var hasMoreResults = features.Length > query.Limit.Value;
        var items = hasMoreResults
            ? features.Take(query.Limit.Value).ToImmutableArray()
            : features;

        return PagedQueryResult<EncodedGeoJsonFeature>.Create(items, hasMoreResults);
    }

    public async Task<PagedQueryResult<RawGeoJsonFeature>> QueryGeoJsonRawPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (_queryBuilder is not FeatureQueryBuilder postgresQueryBuilder ||
            _dataAccess is not FeatureDataAccess postgresDataAccess)
        {
            var fallback = await QueryGeoJsonPageAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            var fallbackItems = fallback.Items
                .Select(feature => RawGeoJsonFeature.Create(
                    feature.Id,
                    feature.GeometryGeoJson,
                    JsonSerializer.Serialize(
                        feature.Attributes,
                        FeatureAttributesJsonContext.Default.ImmutableDictionaryStringObject)))
                .ToImmutableArray();
            return PagedQueryResult<RawGeoJsonFeature>.Create(fallbackItems, fallback.HasMoreResults, fallback.TotalCount);
        }

        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var unpagedSelectQuery = postgresQueryBuilder.BuildSelectRawGeoJsonQuery(layerId, query, geometryStorageType);
            var rawFeatures = await postgresDataAccess.ExecuteSelectRawGeoJsonQueryAsync(unpagedSelectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
            return rawFeatures.Length == 0
                ? PagedQueryResult<RawGeoJsonFeature>.Empty()
                : PagedQueryResult<RawGeoJsonFeature>.Create(rawFeatures);
        }

        var pageQuery = query with { Limit = query.Limit.Value + 1 };
        var selectQuery = postgresQueryBuilder.BuildSelectRawGeoJsonQuery(layerId, pageQuery, geometryStorageType);
        var features = await postgresDataAccess.ExecuteSelectRawGeoJsonQueryAsync(selectQuery, pageQuery, layerId, cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return PagedQueryResult<RawGeoJsonFeature>.Empty();
        }

        var hasMoreResults = features.Length > query.Limit.Value;
        var items = hasMoreResults
            ? features.Take(query.Limit.Value).ToImmutableArray()
            : features;

        return PagedQueryResult<RawGeoJsonFeature>.Create(items, hasMoreResults);
    }

    public async Task<PagedQueryResult<RawGeoServicesFeature>> QueryGeoServicesRawPointPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (_queryBuilder is not FeatureQueryBuilder postgresQueryBuilder ||
            _dataAccess is not FeatureDataAccess postgresDataAccess)
        {
            var fallback = await QueryPageAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            var fallbackItems = fallback.Items
                .Select(feature => RawGeoServicesFeature.Create(feature.Id, null, null, null))
                .ToImmutableArray();
            return PagedQueryResult<RawGeoServicesFeature>.Create(fallbackItems, fallback.HasMoreResults, fallback.TotalCount);
        }

        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var unpagedSelectQuery = postgresQueryBuilder.BuildSelectGeoServicesPointQuery(layerId, query, geometryStorageType);
            var rawFeatures = await postgresDataAccess.ExecuteSelectRawGeoServicesPointQueryAsync(
                unpagedSelectQuery,
                query,
                layerId,
                cancellationToken).ConfigureAwait(false);
            return rawFeatures.Length == 0
                ? PagedQueryResult<RawGeoServicesFeature>.Empty()
                : PagedQueryResult<RawGeoServicesFeature>.Create(rawFeatures);
        }

        var pageQuery = query with { Limit = query.Limit.Value + 1 };
        var selectQuery = postgresQueryBuilder.BuildSelectGeoServicesPointQuery(layerId, pageQuery, geometryStorageType);
        var features = await postgresDataAccess.ExecuteSelectRawGeoServicesPointQueryAsync(
            selectQuery,
            pageQuery,
            layerId,
            cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return PagedQueryResult<RawGeoServicesFeature>.Empty();
        }

        var hasMoreResults = features.Length > query.Limit.Value;
        var items = hasMoreResults
            ? features.Take(query.Limit.Value).ToImmutableArray()
            : features;

        return PagedQueryResult<RawGeoServicesFeature>.Create(items, hasMoreResults);
    }

    public Task<QueryResult<GmlFeature>> QueryGmlAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectGmlQuery,
            _dataAccess.ExecuteSelectGmlQueryAsync,
            QueryOptimizedGmlAsync,
            cancellationToken);

    public Task<QueryResult<KmlFeature>> QueryKmlAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectKmlQuery,
            _dataAccess.ExecuteSelectKmlQueryAsync,
            QueryOptimizedKmlAsync,
            cancellationToken);

    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var statisticsQuery = _queryBuilder.BuildStatisticsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(statisticsQuery, query, layerId, cancellationToken);
    }

    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var countTask = CountAsync(layerId, new FeatureQuery(), cancellationToken);
        var extentTask = GetExtentAsync(layerId, null, cancellationToken);
        await Task.WhenAll(countTask, extentTask).ConfigureAwait(false);

        return new EstimateResult
        {
            EstimatedCount = await countTask.ConfigureAwait(false),
            Extent = await extentTask.ConfigureAwait(false)
        };
    }

    public async Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var topFeaturesQuery = _queryBuilder.BuildTopFeaturesQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(topFeaturesQuery, query, layerId, cancellationToken);

        return features.Length == 0
            ? QueryResult<Feature>.Empty()
            : QueryResult<Feature>.Create(features.Length, features, false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var dateBinsQuery = _queryBuilder.BuildDateBinsQuery(layerId, query, dateBin, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(dateBinsQuery, query, layerId, cancellationToken);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var binsQuery = _queryBuilder.BuildBinsQuery(layerId, query, binDefinition, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(binsQuery, query, layerId, cancellationToken);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var h3SqlQuery = _queryBuilder.BuildH3AggregationQuery(layerId, query, h3Query, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(h3SqlQuery, query, layerId, cancellationToken);
    }

    #endregion

    #region Spatial Operations

    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var effectiveQuery = query ?? new FeatureQuery();
        effectiveQuery = await ApplyPermanentFilterAsync(layerId, effectiveQuery, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var extentQuery = _queryBuilder.BuildExtentQuery(layerId, effectiveQuery, geometryStorageType);
        return await _dataAccess.GetExtentAsync(layerId, extentQuery, effectiveQuery, cancellationToken);
    }

    public async Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        string fieldName,
        TemporalPropertyType propertyType,
        CancellationToken cancellationToken = default)
    {
        // Enforce the layer's permanent (row-visibility) filter so hidden rows do
        // not leak their min/max temporal values through the extent.
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var temporalQuery = query.EnforcedSqlFilter is not null && _queryBuilder is FeatureQueryBuilder postgresQueryBuilder
            ? postgresQueryBuilder.BuildTemporalExtentQuery(layerId, fieldName, propertyType, query)
            : _queryBuilder.BuildTemporalExtentQuery(layerId, fieldName, propertyType);
        return await _dataAccess.GetTemporalExtentAsync(layerId, temporalQuery, cancellationToken);
    }

    public async Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        Core.Features.Tiles.TileOptions tileOptions,
        Core.Configuration.TileLimits tileLimits,
        Core.Features.Tiles.GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default)
    {
        query = query.HasValue
            ? await ApplyPermanentFilterAsync(layerId, query.Value, cancellationToken).ConfigureAwait(false)
            : await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var tileQuery = _queryBuilder.BuildMvtTileQuery(layerId, x, y, z, query, tileOptions, tileLimits, geometryStorageType, gridGeometry);
        return await _dataAccess.GetMvtTileAsync(layerId, tileQuery, cancellationToken);
    }

    public async Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        Core.Features.Tiles.TileOptions tileOptions,
        Core.Configuration.TileLimits tileLimits,
        CancellationToken cancellationToken = default)
    {
        query = query.HasValue
            ? await ApplyPermanentFilterAsync(layerId, query.Value, cancellationToken).ConfigureAwait(false)
            : await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var tileQuery = _queryBuilder.BuildH3TileQuery(layerId, x, y, z, resolution, query, tileOptions, tileLimits, geometryStorageType);
        return await _dataAccess.GetMvtTileAsync(layerId, tileQuery, cancellationToken);
    }

    #endregion

    #region Batch Operations

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.ApplyEditsAsync(layerId, editBatch, cancellationToken);
    }

    #endregion

    #region Streaming Operations

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var streamQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        await foreach (var feature in _dataAccess.StreamFeaturesAsync(layerId, streamQuery, query, cancellationToken))
        {
            yield return feature;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(int layerId, FeatureQuery query, int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var batch = new List<Feature>(batchSize);

        await foreach (var feature in StreamFeaturesAsync(layerId, query, cancellationToken))
        {
            batch.Add(feature);

            if (batch.Count >= batchSize)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var streamQuery = _queryBuilder.BuildSelectGmlQuery(layerId, query, geometryStorageType);
        await foreach (var feature in _dataAccess.StreamGmlFeaturesAsync(layerId, streamQuery, query, cancellationToken))
        {
            yield return feature;
        }
    }

    #endregion

    #region Related Features

    public async Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
    {
        // Enforce permanent (row-visibility) filters on both sides of the
        // relationship: hidden origin rows must not resolve foreign keys, and
        // hidden related rows must not be readable through queryRelatedRecords.
        if (_dataAccess is FeatureDataAccess postgresDataAccess)
        {
            var originFilter = await ResolveEnforcedSqlFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
            var relatedFilter = query.RelatedLayerId is int relatedLayerId
                ? await ResolveEnforcedSqlFilterAsync(relatedLayerId, cancellationToken).ConfigureAwait(false)
                : null;
            if (originFilter is not null || relatedFilter is not null)
            {
                return await postgresDataAccess.QueryRelatedAsync(layerId, query, originFilter, relatedFilter, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _dataAccess.QueryRelatedAsync(layerId, query, cancellationToken);
    }

    #endregion

    #region Performance Metrics

    public Dictionary<string, DatabaseOperationMetricsSnapshot> GetPerformanceStatistics()
    {
        return _cacheManager.GetPerformanceStatistics();
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Shared query control flow for all format-specific query methods.
    /// Handles KNN, paginated-optimized, and unlimited query paths.
    /// </summary>
    private async Task<QueryResult<T>> ExecuteFormatQueryAsync<T>(
        int layerId,
        FeatureQuery query,
        Func<int, FeatureQuery, CoreGeometryStorageType, ParameterizedQuery> buildSelect,
        Func<ParameterizedQuery, FeatureQuery, int, CancellationToken, Task<ImmutableArray<T>>> executeSelect,
        Func<int, FeatureQuery, CoreGeometryStorageType, CancellationToken, Task<QueryResult<T>>> executeOptimized,
        CancellationToken cancellationToken)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var isKnnQuery = query.SpatialFilter.HasValue &&
                         query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        if (isKnnQuery)
        {
            var knnSelectQuery = buildSelect(layerId, query, geometryStorageType);
            var knnFeatures = await executeSelect(knnSelectQuery, query, layerId, cancellationToken);
            return knnFeatures.Length == 0
                ? QueryResult<T>.Empty()
                : QueryResult<T>.Create(knnFeatures.Length, knnFeatures, false);
        }

        // PERFORMANCE OPTIMIZATION: Use single query with window function instead of separate count + select
        // This reduces database round trips from 2 to 1, improving performance by 30-50%.
        // returnDistance queries opt out: the window-count select builder does not project the
        // runtime distance column, so they fall through to the count + plain-select path (the
        // plain select clause emits the distance column). Distance is only meaningful with a
        // spatial filter, so this affects a narrow slice of queries.
        if ((query.Limit.HasValue || query.Offset.HasValue) &&
            !FeatureQueryBuilder.ShouldComputeDistance(query.SpatialFilter))
        {
            return await executeOptimized(layerId, query, geometryStorageType, cancellationToken);
        }

        // Fallback to original pattern for unlimited queries where count optimization isn't beneficial
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        var totalCount = await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken);

        if (totalCount == 0)
        {
            return QueryResult<T>.Empty();
        }

        var selectQuery = buildSelect(layerId, query, geometryStorageType);
        var features = await executeSelect(selectQuery, query, layerId, cancellationToken);

        return QueryResult<T>.Create(totalCount, features, false);
    }

    /// <summary>
    /// Computes the filtered total count for a windowed page that came back empty.
    /// The optimized single-query path derives TotalCount from a COUNT(*) OVER()
    /// column on returned rows, so an empty page (e.g. OData $skip beyond the last
    /// match) would otherwise report TotalCount = 0 even though the filter matches
    /// rows — breaking $count=true and any client paging on the total count.
    /// </summary>
    private async Task<QueryResult<T>> CountEmptyPageAsync<T>(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        var totalCount = await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);
        return totalCount == 0
            ? QueryResult<T>.Empty()
            : QueryResult<T>.Create(totalCount, ImmutableArray<T>.Empty, totalCount > (query.Offset ?? 0));
    }

    private async Task<QueryResult<Feature>> QueryOptimizedAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectQuery(layerId, query, geometryStorageType);

        var features = new List<Feature>();
        long totalCount = 0;
        var hasCountColumn = false;

        await foreach (var feature in _dataAccess.StreamFeaturesAsync(layerId, optimizedQuery, query, cancellationToken))
        {
            features.Add(feature);

            // Extract total count from first row if available
            if (!hasCountColumn &&
                feature.Attributes.TryGetValue(FeatureQueryEncoding.InternalTotalCountColumn, out var totalCountValue) &&
                totalCountValue is not null)
            {
                totalCount = Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
                hasCountColumn = true;
            }
        }

        if (!hasCountColumn)
        {
            totalCount = features.Count;
        }

        if (features.Count == 0)
        {
            return await CountEmptyPageAsync<Feature>(layerId, query, geometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < features.Count; i++)
        {
            features[i] = RemoveInternalAttributes(features[i]);
        }

        var offset = query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + features.Count;
        return QueryResult<Feature>.Create(totalCount, features.ToImmutableArray(), hasMoreResults);
    }

    private async Task<QueryResult<GmlFeature>> QueryOptimizedGmlAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectGmlQuery(layerId, query, geometryStorageType);

        var features = new List<GmlFeature>();
        long totalCount = 0;
        var hasCountColumn = false;

        await foreach (var feature in _dataAccess.StreamGmlFeaturesAsync(layerId, optimizedQuery, query, cancellationToken))
        {
            features.Add(feature);

            // Extract total count from first row if available
            if (!hasCountColumn &&
                feature.Attributes.TryGetValue(FeatureQueryEncoding.InternalTotalCountColumn, out var totalCountValue) &&
                totalCountValue is not null)
            {
                totalCount = Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
                hasCountColumn = true;
            }
        }

        if (!hasCountColumn)
        {
            totalCount = features.Count;
        }

        if (features.Count == 0)
        {
            return await CountEmptyPageAsync<GmlFeature>(layerId, query, geometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < features.Count; i++)
        {
            features[i] = RemoveInternalAttributes(features[i]);
        }

        var offset = query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + features.Count;
        return QueryResult<GmlFeature>.Create(totalCount, features.ToImmutableArray(), hasMoreResults);
    }

    private async Task<QueryResult<EncodedGeoJsonFeature>> QueryOptimizedGeoJsonAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectGeoJsonQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectGeoJsonQueryAsync(optimizedQuery, query, layerId, cancellationToken);

        if (features.Length == 0)
        {
            return await CountEmptyPageAsync<EncodedGeoJsonFeature>(layerId, query, geometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        var totalCount = ExtractTotalCount(features[0].Attributes, features.Length);
        var cleaned = features.Select(RemoveInternalAttributes).ToImmutableArray();
        var offset = query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + cleaned.Length;
        return QueryResult<EncodedGeoJsonFeature>.Create(totalCount, cleaned, hasMoreResults);
    }

    private async Task<QueryResult<KmlFeature>> QueryOptimizedKmlAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectKmlQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectKmlQueryAsync(optimizedQuery, query, layerId, cancellationToken);

        if (features.Length == 0)
        {
            return await CountEmptyPageAsync<KmlFeature>(layerId, query, geometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        var totalCount = ExtractTotalCount(features[0].Attributes, features.Length);
        var cleaned = features.Select(RemoveInternalAttributes).ToImmutableArray();
        var offset = query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + cleaned.Length;
        return QueryResult<KmlFeature>.Create(totalCount, cleaned, hasMoreResults);
    }

    private static Feature RemoveInternalAttributes(Feature feature)
    {
        if (!feature.Attributes.ContainsKey(FeatureQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        var cleaned = feature.Attributes.Remove(FeatureQueryEncoding.InternalTotalCountColumn);
        return feature with { Attributes = cleaned };
    }

    private static GmlFeature RemoveInternalAttributes(GmlFeature feature)
    {
        if (!feature.Attributes.ContainsKey(FeatureQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        var cleaned = feature.Attributes.Remove(FeatureQueryEncoding.InternalTotalCountColumn);
        return feature with { Attributes = cleaned };
    }

    private static EncodedGeoJsonFeature RemoveInternalAttributes(EncodedGeoJsonFeature feature)
    {
        if (!feature.Attributes.ContainsKey(FeatureQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        var cleaned = feature.Attributes.Remove(FeatureQueryEncoding.InternalTotalCountColumn);
        return feature with { Attributes = cleaned };
    }

    private static KmlFeature RemoveInternalAttributes(KmlFeature feature)
    {
        if (!feature.Attributes.ContainsKey(FeatureQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        var cleaned = feature.Attributes.Remove(FeatureQueryEncoding.InternalTotalCountColumn);
        return feature with { Attributes = cleaned };
    }

    private static long ExtractTotalCount(ImmutableDictionary<string, object?> attributes, int fallbackCount)
    {
        if (attributes.TryGetValue(FeatureQueryEncoding.InternalTotalCountColumn, out var totalCountValue) &&
            totalCountValue is not null)
        {
            return Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
        }

        return fallbackCount;
    }

    private async Task<FeatureQuery> ApplyPermanentFilterAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (query.EnforcedSqlFilter != null)
        {
            return query;
        }

        var enforcedFilter = await ResolveEnforcedSqlFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        return enforcedFilter == null ? query : query with { EnforcedSqlFilter = enforcedFilter };
    }

    /// <summary>
    /// Resolves the layer's metadata-v2 permanent (row-visibility) filter to a
    /// parameterized SQL fragment, or null when the layer has no enforceable filter.
    /// </summary>
    private Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        int layerId,
        CancellationToken cancellationToken)
        => PermanentFilterResolver.ResolveAsync(_v2Provider, _filterExpressionService, layerId, cancellationToken);

    private async Task<MetadataV2Resource> GetMetadataResourceAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_v2Provider == null)
        {
            throw new InvalidOperationException("Metadata v2 resources are required for native binary encoders.");
        }

        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource)
            ? resource
            : throw new ResourceNotFoundException($"Metadata resource for storage layer {layerId} not found.");
    }

    #endregion
}
