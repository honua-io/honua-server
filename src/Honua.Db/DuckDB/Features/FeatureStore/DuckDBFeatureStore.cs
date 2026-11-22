// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Features.FeatureStore.Services;
using Honua.Db.DuckDB.Features.Infrastructure;
using Honua.Db.DuckDB.Queries.Filters;

namespace Honua.Db.DuckDB.Features.FeatureStore;

internal sealed class DuckDBFeatureStore :
    IFeatureDataProvider,
    IFeatureReader,
    IBindableFeatureDataProvider,
    IGeoJsonFeatureStore,
    IStreamingFeatureStore,
    IPagedFeatureReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly LayerReadSecurityResolver _readSecurity;
    private readonly DuckDBManagedSource? _managedSource;
    private readonly DuckDBLayerRegistry? _registry;
    private readonly ILogger<DuckDBFeatureDataAccess>? _dataAccessLogger;
    private readonly IPerformanceMonitor? _performanceMonitor;
    private readonly FeatureProviderBinding? _binding;
    private readonly bool _enableH3;

    public DuckDBFeatureStore(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        LayerReadSecurityResolver readSecurity,
        IMetadataV2GraphProvider? v2Provider = null,
        IFilterExpressionService? filterExpressionService = null,
        bool enableH3 = false,
        DuckDBManagedSource? managedSource = null,
        DuckDBLayerRegistry? registry = null,
        ILogger<DuckDBFeatureDataAccess>? dataAccessLogger = null,
        IPerformanceMonitor? performanceMonitor = null)
        : this(queryBuilder, dataAccess, cacheManager, v2Provider, filterExpressionService, readSecurity, enableH3,
            managedSource, registry, dataAccessLogger, performanceMonitor, binding: null)
    {
    }

    private DuckDBFeatureStore(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        IMetadataV2GraphProvider? v2Provider,
        IFilterExpressionService? filterExpressionService,
        LayerReadSecurityResolver readSecurity,
        bool enableH3,
        DuckDBManagedSource? managedSource,
        DuckDBLayerRegistry? registry,
        ILogger<DuckDBFeatureDataAccess>? dataAccessLogger,
        IPerformanceMonitor? performanceMonitor,
        FeatureProviderBinding? binding)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _v2Provider = v2Provider;
        _filterExpressionService = filterExpressionService;
        _readSecurity = readSecurity ?? throw new ArgumentNullException(nameof(readSecurity));
        _managedSource = managedSource;
        _registry = registry;
        _dataAccessLogger = dataAccessLogger;
        _performanceMonitor = performanceMonitor;
        _binding = binding;
        _enableH3 = enableH3;
    }

    public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return (binding.StorageMapping.ProviderOptions.ContainsKey(DuckDBProviderOptions.ObjectStorageConnectionId) ||
                binding.StorageMapping.ProviderOptions.ContainsKey(DuckDBProviderOptions.ObjectStorageKey) ||
                binding.StorageMapping.ProviderOptions.ContainsKey(DuckDBProviderOptions.ExternalSourcePath))
            ? CreateManagedReader(binding)
            : CreateBoundReader(binding, _queryBuilder, _dataAccess, _cacheManager);
    }

    private DuckDBFeatureStore CreateManagedReader(FeatureProviderBinding binding)
    {
        if (_managedSource is null || _dataAccessLogger is null)
        {
            throw new InvalidOperationException("Managed DuckDB object storage is unavailable.");
        }

        if (binding.StorageMapping.LayerDiscriminatorColumn is not null)
        {
            throw new InvalidOperationException("Managed DuckDB GeoParquet requires one object per publication without a layer discriminator.");
        }

        var attributes = binding.Resource.SchemaFields
            .Select(field => field.Name)
            .Where(name => FeatureFieldNameSyntax.IsValid(name) &&
                !name.Equals(binding.StorageMapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(binding.StorageMapping.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mapping = DuckDBLayerMapping.FromStorage(binding.StorageLayerId, binding.StorageMapping, attributes);
        if (!mapping.ObjectStorageConnectionId.HasValue || mapping.ObjectStorageKey is null ||
            !mapping.ExternalSourcePaths.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("DuckDB publication sources require a managed object storage connection and key.");
        }

        var registry = new DuckDBLayerRegistry([mapping]);
        return CreateBoundReader(
            binding,
            new DuckDBFeatureQueryBuilder(registry),
            new DuckDBFeatureDataAccess(_managedSource.CreateConnectionProvider(mapping), registry, _performanceMonitor, _dataAccessLogger),
            new DuckDBFeatureCacheManager(registry));
    }

    private DuckDBFeatureStore CreateBoundReader(
        FeatureProviderBinding binding,
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager)
    {
        return new DuckDBFeatureStore(
            queryBuilder,
            dataAccess,
            cacheManager,
            _v2Provider,
            _filterExpressionService,
            _readSecurity,
            _enableH3,
            _managedSource,
            _registry,
            _dataAccessLogger,
            _performanceMonitor,
            binding);
    }

    public string ProviderName => DataProviderNames.DuckDb;

    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyAnalytical;

    public IFeatureReader Reader => this;

    public IFeatureWriter? Writer => null;

    #region IFeatureReader

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        if (query.EnforcedSqlFilter is null)
        {
            return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
        }
        query = query with { ObjectIds = ImmutableArray.Create(featureId), Limit = 1 };
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
        return features.IsDefaultOrEmpty ? null : features[0];
    }

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => ExecuteFormatQueryAsync(
            layerId, query,
            _queryBuilder.BuildSelectQuery,
            _dataAccess.ExecuteSelectQueryAsync,
            QueryOptimizedAsync,
            cancellationToken);

    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteSelectObjectIdsQueryAsync(objectIdsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        // DuckDB does not support native FlatGeobuf; return null so server falls back
        return Task.FromResult<byte[]?>(null);
    }

    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var effectiveQuery = await ApplyPermanentFilterAsync(layerId, query ?? new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var extentQuery = _queryBuilder.BuildExtentQuery(layerId, effectiveQuery, geometryStorageType);
        return await _dataAccess.GetExtentAsync(layerId, extentQuery, effectiveQuery, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var statisticsQuery = _queryBuilder.BuildStatisticsQuery(layerId, query, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(statisticsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
    {
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var temporalQuery = query.EnforcedSqlFilter is not null
            ? _queryBuilder is DuckDBFeatureQueryBuilder duckDbQueryBuilder
                ? duckDbQueryBuilder.BuildTemporalExtentQuery(layerId, fieldName, propertyType, query)
                : throw new NotSupportedException("The query builder cannot enforce the temporal extent's permanent filter.")
            : _queryBuilder.BuildTemporalExtentQuery(layerId, fieldName, propertyType);
        return await _dataAccess.GetTemporalExtentAsync(layerId, temporalQuery, cancellationToken).ConfigureAwait(false);
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
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var topFeaturesQuery = _queryBuilder.BuildTopFeaturesQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(topFeaturesQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        return features.Length == 0
            ? QueryResult<Feature>.Empty()
            : QueryResult<Feature>.Create(features.Length, features, false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var dateBinsQuery = _queryBuilder.BuildDateBinsQuery(layerId, query, dateBin, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(dateBinsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var binsQuery = _queryBuilder.BuildBinsQuery(layerId, query, binDefinition, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(binsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
    {
        if (!_enableH3)
        {
            throw new NotSupportedException("DuckDB H3 queries require DuckDB:EnableH3.");
        }

        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var h3SqlQuery = _queryBuilder.BuildH3AggregationQuery(layerId, query, h3Query, geometryStorageType);
        return await _dataAccess.ExecuteStatisticsQueryAsync(h3SqlQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region IGeoJsonFeatureStore

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

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var streamQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        await foreach (var feature in _dataAccess.StreamFeaturesAsync(layerId, streamQuery, query, cancellationToken))
        {
            yield return feature;
        }
    }

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

    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId, FeatureQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    #region IPagedFeatureReader

    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
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

    #endregion

    #region Private helpers

    private Task EnsureRowAndFieldPolicyEnforceableAsync(int layerId, CancellationToken cancellationToken)
        => _readSecurity.EnsureNoUnenforcedPolicyAsync(
                "DuckDB", layerId, _binding?.Resource, rejectPermanentFilter: false, cancellationToken);

    private async Task<FeatureQuery> ApplyPermanentFilterAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        await EnsureRowAndFieldPolicyEnforceableAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (_binding is not null && _binding.StorageLayerId != layerId)
        {
            throw new InvalidOperationException("DuckDB bound reader received a different storage layer.");
        }

        if (_registry?.GetMapping(layerId)?.ObjectStorageConnectionId is not null &&
            (_binding is null || !_binding.StorageMapping.ProviderOptions.ContainsKey(DuckDBProviderOptions.ObjectStorageConnectionId)))
        {
            throw new InvalidOperationException("DuckDB managed object storage requires a publication binding.");
        }

        if (_binding?.StorageMapping.ProviderOptions.ContainsKey(DuckDBProviderOptions.ObjectStorageConnectionId) == true)
        {
            query = query with
            {
                SqlFilter = TranslateManagedFilter(query.SqlFilter, _binding.Resource),
                EnforcedSqlFilter = TranslateManagedFilter(query.EnforcedSqlFilter, _binding.Resource)
            };
        }

        if (query.EnforcedSqlFilter != null)
        {
            return query;
        }

        var enforcedFilter = _binding is not null
            ? LayerReadSecurityResolver.ResolvePermanentFilter(_binding.Resource, _filterExpressionService)
            : await PermanentFilterResolver
                .ResolveAsync(_v2Provider, _filterExpressionService, layerId, cancellationToken)
                .ConfigureAwait(false);

        if (enforcedFilter != null)
        {
            query = query with { EnforcedSqlFilter = enforcedFilter };
        }

        return query;
    }

    private SqlFragment? TranslateManagedFilter(SqlFragment? fragment, MetadataV2Object resource)
    {
        if (fragment is null || fragment.Dialect == DuckDbSqlDialect.Instance.Name)
        {
            return fragment;
        }

        if (fragment.SourceExpression is not { } expression || _filterExpressionService is null)
        {
            throw new NotSupportedException("Managed DuckDB can only query filters with a provider-neutral expression.");
        }

        var translated = _filterExpressionService.Translate(expression, resource);
        return translated.IsSuccess && translated.SqlFilter is { } sql
            ? sql
            : throw new NotSupportedException("The filter cannot be represented in DuckDB.");
    }

    private async Task<QueryResult<T>> ExecuteFormatQueryAsync<T>(
        int layerId,
        FeatureQuery query,
        Func<int, FeatureQuery, GeometryStorageType, ParameterizedQuery> buildSelect,
        Func<ParameterizedQuery, FeatureQuery, int, CancellationToken, Task<ImmutableArray<T>>> executeSelect,
        Func<int, FeatureQuery, GeometryStorageType, CancellationToken, Task<QueryResult<T>>> executeOptimized,
        CancellationToken cancellationToken)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
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
