// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.DuckDB.Features.FeatureStore.Services;

namespace Honua.DuckDB.Features.FeatureStore;

/// <summary>
/// DuckDB implementation of feature storage using service composition.
/// Implements read-only interfaces; write operations are handled by
/// <see cref="Core.Features.FeatureStore.ReadOnlyProviders.ReadOnlyFeatureWriter"/>.
/// </summary>
/// <remarks>
/// Follows the same delegation pattern as the Postgres provider:
/// query builder produces SQL, data access executes it, cache manager provides metadata.
/// </remarks>
internal sealed class DuckDBFeatureStore :
    IFeatureDataProvider,
    IFeatureReader,
    IGeoJsonFeatureStore,
    IStreamingFeatureStore,
    IPagedFeatureReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;

    public DuckDBFeatureStore(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.DuckDb;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyAnalytical;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    #region IFeatureReader

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectQuery,
            _dataAccess.ExecuteSelectQueryAsync,
            QueryOptimizedAsync,
            cancellationToken);

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectObjectIdsQueryAsync(objectIdsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        // DuckDB does not support native FlatGeobuf; return null so server falls back
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var effectiveQuery = query ?? new FeatureQuery();
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var extentQuery = _queryBuilder.BuildExtentQuery(layerId, effectiveQuery, geometryStorageType);
        return await _dataAccess.GetExtentAsync(layerId, extentQuery, effectiveQuery, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var statisticsQuery = _queryBuilder.BuildStatisticsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(statisticsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, FieldType fieldType, CancellationToken cancellationToken = default)
    {
        var temporalQuery = _queryBuilder.BuildTemporalExtentQuery(layerId, fieldName, fieldType);
        return await _dataAccess.GetTemporalExtentAsync(layerId, temporalQuery, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var topFeaturesQuery = _queryBuilder.BuildTopFeaturesQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(topFeaturesQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        return features.Length == 0
            ? QueryResult<Feature>.Empty()
            : QueryResult<Feature>.Create(features.Length, features, false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var dateBinsQuery = _queryBuilder.BuildDateBinsQuery(layerId, query, dateBin, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(dateBinsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var binsQuery = _queryBuilder.BuildBinsQuery(layerId, query, binDefinition, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(binsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("DuckDB provider does not support H3 hexagonal aggregation.");
    }

    #endregion

    #region IGeoJsonFeatureStore

    /// <inheritdoc />
    public Task<QueryResult<EncodedGeoJsonFeature>> QueryGeoJsonAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectGeoJsonQuery,
            _dataAccess.ExecuteSelectGeoJsonQueryAsync,
            QueryOptimizedGeoJsonAsync,
            cancellationToken);

    #endregion

    #region IStreamingFeatureStore

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var streamQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        await foreach (var feature in _dataAccess.StreamFeaturesAsync(layerId, streamQuery, query, cancellationToken))
        {
            yield return feature;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId, FeatureQuery query, int batchSize = 1000, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    /// <inheritdoc />
    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var streamQuery = _queryBuilder.BuildSelectGmlQuery(layerId, query, geometryStorageType);
        await foreach (var feature in _dataAccess.StreamGmlFeaturesAsync(layerId, streamQuery, query, cancellationToken))
        {
            yield return feature;
        }
    }

    #endregion

    #region IPagedFeatureReader

    /// <inheritdoc />
    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
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

    #endregion

    #region Private helpers

    private async Task<QueryResult<T>> ExecuteFormatQueryAsync<T>(
        int layerId,
        FeatureQuery query,
        Func<int, FeatureQuery, GeometryStorageType, ParameterizedQuery> buildSelect,
        Func<ParameterizedQuery, FeatureQuery, int, CancellationToken, Task<ImmutableArray<T>>> executeSelect,
        Func<int, FeatureQuery, GeometryStorageType, CancellationToken, Task<QueryResult<T>>> executeOptimized,
        CancellationToken cancellationToken)
    {
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        // Use optimized single-query path when pagination is active
        if (query.Limit.HasValue || query.Offset.HasValue)
        {
            return await executeOptimized(layerId, query, geometryStorageType, cancellationToken).ConfigureAwait(false);
        }

        // Unlimited query: count + select
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        var totalCount = await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        if (totalCount == 0)
        {
            return QueryResult<T>.Empty();
        }

        var selectQuery = buildSelect(layerId, query, geometryStorageType);
        var features = await executeSelect(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        return QueryResult<T>.Create(totalCount, features, false);
    }

    private async Task<QueryResult<Feature>> QueryOptimizedAsync(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectQuery(layerId, query, geometryStorageType);
        var features = new List<Feature>();
        long totalCount = 0;
        var hasCountColumn = false;

        await foreach (var feature in _dataAccess.StreamFeaturesAsync(layerId, optimizedQuery, query, cancellationToken))
        {
            features.Add(feature);
            if (!hasCountColumn &&
                feature.Attributes.TryGetValue(DuckDBQueryEncoding.InternalTotalCountColumn, out var totalCountValue) &&
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

    private async Task<QueryResult<EncodedGeoJsonFeature>> QueryOptimizedGeoJsonAsync(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectGeoJsonQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectGeoJsonQueryAsync(optimizedQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return QueryResult<EncodedGeoJsonFeature>.Empty();
        }

        var totalCount = ExtractTotalCount(features.Length > 0 ? features[0].Attributes : ImmutableDictionary<string, object?>.Empty, features.Length);
        var cleaned = features.Select(RemoveInternalAttributes).ToImmutableArray();
        var offset = query.Offset ?? 0;
        var hasMoreResults = totalCount > offset + cleaned.Length;
        return QueryResult<EncodedGeoJsonFeature>.Create(totalCount, cleaned, hasMoreResults);
    }

    private static Feature RemoveInternalAttributes(Feature feature)
    {
        if (!feature.Attributes.ContainsKey(DuckDBQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        return feature with { Attributes = feature.Attributes.Remove(DuckDBQueryEncoding.InternalTotalCountColumn) };
    }

    private static EncodedGeoJsonFeature RemoveInternalAttributes(EncodedGeoJsonFeature feature)
    {
        if (!feature.Attributes.ContainsKey(DuckDBQueryEncoding.InternalTotalCountColumn))
        {
            return feature;
        }

        return feature with { Attributes = feature.Attributes.Remove(DuckDBQueryEncoding.InternalTotalCountColumn) };
    }

    private static long ExtractTotalCount(ImmutableDictionary<string, object?> attributes, int fallbackCount)
    {
        if (attributes.TryGetValue(DuckDBQueryEncoding.InternalTotalCountColumn, out var value) && value is not null)
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return fallbackCount;
    }

    #endregion
}
