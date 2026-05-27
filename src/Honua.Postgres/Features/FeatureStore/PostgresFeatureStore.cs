// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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
    private readonly ILayerCatalog? _layerCatalog;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IDatabaseConnectionProvider? _connectionProvider;
    private readonly ObjectPool<Dictionary<string, object?>>? _dictionaryPool;
    private readonly IConnectionEncryptionService? _connectionEncryptionService;
    private readonly IFilterExpressionService? _filterExpressionService;

    public PostgresFeatureStoreRefactored(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager)
        : this(queryBuilder, dataAccess, cacheManager, layerCatalog: null)
    {
    }

    public PostgresFeatureStoreRefactored(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        ILayerCatalog? layerCatalog)
        : this(
            queryBuilder,
            dataAccess,
            cacheManager,
            layerCatalog,
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
        ILayerCatalog? layerCatalog,
        IDatabaseConnectionProvider? connectionProvider,
        ObjectPool<Dictionary<string, object?>>? dictionaryPool,
        IConnectionEncryptionService? connectionEncryptionService,
        IFilterExpressionService? filterExpressionService = null,
        IMetadataV2GraphProvider? v2Provider = null)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _layerCatalog = layerCatalog;
        _v2Provider = v2Provider;
        _connectionProvider = connectionProvider;
        _dictionaryPool = dictionaryPool;
        _connectionEncryptionService = connectionEncryptionService;
        _filterExpressionService = filterExpressionService;
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
            binding.Layer,
            binding.Connection,
            _connectionEncryptionService);
    }

    #region Core CRUD Operations

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken);
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
        var layer = await GetLayerDefinitionAsync(layerId, cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectFlatGeobufQuery(layer, layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectFlatGeobufQueryAsync(selectQuery, query, layerId, cancellationToken);
    }

    public async Task<byte[]?> QueryGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var layer = await GetLayerDefinitionAsync(layerId, cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectGeobufQuery(layer, layerId, query, geometryStorageType);
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
        FieldType fieldType,
        CancellationToken cancellationToken = default)
    {
        var temporalQuery = _queryBuilder.BuildTemporalExtentQuery(layerId, fieldName, fieldType);
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
        CancellationToken cancellationToken = default)
    {
        query = query.HasValue
            ? await ApplyPermanentFilterAsync(layerId, query.Value, cancellationToken).ConfigureAwait(false)
            : await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var tileQuery = _queryBuilder.BuildMvtTileQuery(layerId, x, y, z, query, tileOptions, tileLimits, geometryStorageType);
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
        // This reduces database round trips from 2 to 1, improving performance by 30-50%
        if (query.Limit.HasValue || query.Offset.HasValue)
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
            return QueryResult<Feature>.Empty();
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
            return QueryResult<GmlFeature>.Empty();
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
            return QueryResult<EncodedGeoJsonFeature>.Empty();
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
            return QueryResult<KmlFeature>.Empty();
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
        if (query.EnforcedSqlFilter != null || _filterExpressionService == null)
        {
            return query;
        }

        // V2 path: resolve the resource via the storageLayerId→resource index, read
        // the typed PermanentFilter from the canonical resource. Falls through to the
        // v1 catalog path when the V2 provider isn't wired in (legacy tests) or the
        // graph has no entry for this storage layer id (resource hasn't been
        // migrated yet). The two paths produce identical SqlFragments.
        if (_v2Provider != null)
        {
            var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
            {
                var v2Filter = resource.PermanentFilter;
                if (v2Filter == null || string.IsNullOrWhiteSpace(v2Filter.Expression))
                {
                    return query;
                }
                if (!TryResolveFilterLanguage(v2Filter.Language, out var v2Language))
                {
                    throw new InvalidOperationException(
                        $"Saved permanent filter for resource '{resource.Metadata.Id}' uses unsupported language '{v2Filter.Language}'.");
                }
                var v2ParseAndNormalize = _filterExpressionService.ParseAndNormalize(v2Language, v2Filter.Expression, resource);
                if (!v2ParseAndNormalize.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Saved permanent filter for resource '{resource.Metadata.Id}' is invalid: {v2ParseAndNormalize.ErrorMessage ?? "Invalid filter."}");
                }
                // SQL last-mile bridge (#1035): the normalized V2 filter still passes
                // through the v1 SQL translator until ISqlFilterTranslator V2 lands.
                if (_layerCatalog == null || v2ParseAndNormalize.Expression == null)
                {
                    return query;
                }
                var v1Layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
                if (v1Layer == null)
                {
                    return query;
                }
                var v2Translation = _filterExpressionService.Translate(v2ParseAndNormalize.Expression, v1Layer);
                if (!v2Translation.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Saved permanent filter for resource '{resource.Metadata.Id}' failed SQL translation: {v2Translation.ErrorMessage ?? "Invalid filter."}");
                }
                return v2Translation.SqlFilter == null ? query : query with { EnforcedSqlFilter = v2Translation.SqlFilter };
            }
        }

        if (_layerCatalog == null)
        {
            return query;
        }

        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var permanentFilter = layer?.Metadata?.PermanentFilter;
        if (permanentFilter == null || string.IsNullOrWhiteSpace(permanentFilter.Expression))
        {
            return query;
        }

        if (!TryResolveFilterLanguage(permanentFilter.Language, out var filterLanguage))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for layer {layerId} uses unsupported language '{permanentFilter.Language}'.");
        }

        var translationResult = _filterExpressionService.Translate(filterLanguage, permanentFilter.Expression, layer!);
        if (!translationResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for layer {layerId} is invalid: {translationResult.ErrorMessage ?? "Invalid filter."}");
        }

        return translationResult.SqlFilter == null
            ? query
            : query with { EnforcedSqlFilter = translationResult.SqlFilter };
    }

    private static bool TryResolveFilterLanguage(string? language, out FilterLanguage filterLanguage)
    {
        filterLanguage = FilterLanguage.ArcGisSql;
        var normalized = (language ?? LayerPermanentFilterLanguages.ArcGisSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case LayerPermanentFilterLanguages.ArcGisSql:
            case "arcgis":
            case "geoservices-sql":
                filterLanguage = FilterLanguage.ArcGisSql;
                return true;
            case LayerPermanentFilterLanguages.Cql2Text:
            case "cql2":
                filterLanguage = FilterLanguage.Cql2Text;
                return true;
            case LayerPermanentFilterLanguages.Cql2Json:
                filterLanguage = FilterLanguage.Cql2Json;
                return true;
            default:
                return false;
        }
    }

    private async Task<LayerDefinition> GetLayerDefinitionAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_layerCatalog == null)
        {
            throw new InvalidOperationException("Layer metadata is required for native binary encoders.");
        }

        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        return layer ?? throw new ResourceNotFoundException($"Layer {layerId} not found.");
    }

    #endregion
}
