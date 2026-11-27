// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureExport;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal sealed partial class PostgresFeatureStoreRefactored : IFeatureDataProvider, IFeatureReader, IDistinctFeatureReader, IBindableFeatureDataProvider, IBindableTileProvider, IRasterPointReader, IFeatureWriter, ITileProvider, IPooledTileProvider, IRelationshipStore, IGeoJsonFeatureStore, IGeobufFeatureStore, IFlatGeobufFeatureStore, IGmlFeatureStore, IKmlFeatureStore, IStreamingFeatureStore, IObjectIdStreamingReader, IPagedFeatureReader, IPagedGeoJsonFeatureStore, IPagedRawGeoJsonFeatureStore, IPagedRawGeoServicesFeatureStore, IPreChangeImageReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IAdoNetDatabaseConnectionProvider? _connectionProvider;
    private readonly ObjectPool<Dictionary<string, object?>>? _dictionaryPool;
    private readonly IConnectionEncryptionService? _connectionEncryptionService;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly IRowLevelSecurityFilterSource? _rlsFilterSource;
    private readonly IFieldMaskSource? _fieldMaskSource;
    private readonly ILogger<PostgresStorageMappedFeatureReader>? _storageMappedReaderLogger;
    private readonly string? _managedFeatureSchema;
    private readonly bool? _preferSerialBoundedSpatialReads;
    private readonly bool _disableJitForSourceSpatialCounts;
    private readonly bool? _preferSerialSourceSpatialCounts;
    private readonly LayerReadSecurityResolver _readSecurity;
    private readonly PostgresBoundConnectionProvider? _boundConnectionProvider;
    private readonly IRequestSecretReferenceResolver? _secretReferenceResolver;
    private readonly IResolvedConnectionStringValidator? _resolvedConnectionStringValidator;

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
        ILogger<PostgresStorageMappedFeatureReader>? storageMappedReaderLogger = null,
        IRowLevelSecurityFilterSource? rlsFilterSource = null,
        IFieldMaskSource? fieldMaskSource = null,
        string? managedFeatureSchema = null,
        PostgresBoundConnectionProvider? boundConnectionProvider = null,
        bool? preferSerialBoundedSpatialReads = null,
        bool disableJitForSourceSpatialCounts = false,
        bool? preferSerialSourceSpatialCounts = null,
        IRequestSecretReferenceResolver? secretReferenceResolver = null,
        IResolvedConnectionStringValidator? resolvedConnectionStringValidator = null)
    {
        _secretReferenceResolver = secretReferenceResolver;
        _resolvedConnectionStringValidator = resolvedConnectionStringValidator;
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _v2Provider = v2Provider;
        _connectionProvider = connectionProvider;
        _boundConnectionProvider = boundConnectionProvider;
        _dictionaryPool = dictionaryPool;
        _connectionEncryptionService = connectionEncryptionService;
        _filterExpressionService = filterExpressionService;
        _storageMappedReaderLogger = storageMappedReaderLogger;
        _managedFeatureSchema = string.IsNullOrWhiteSpace(managedFeatureSchema) ? null : managedFeatureSchema.Trim();
        _preferSerialBoundedSpatialReads = preferSerialBoundedSpatialReads;
        _disableJitForSourceSpatialCounts = disableJitForSourceSpatialCounts;
        _preferSerialSourceSpatialCounts = preferSerialSourceSpatialCounts;
        _rlsFilterSource = rlsFilterSource;
        _fieldMaskSource = fieldMaskSource;
        _readSecurity = new LayerReadSecurityResolver(v2Provider, filterExpressionService, rlsFilterSource, fieldMaskSource);
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
            _storageMappedReaderLogger,
            _filterExpressionService,
            _rlsFilterSource,
            _fieldMaskSource,
            _managedFeatureSchema,
            _boundConnectionProvider,
            _preferSerialBoundedSpatialReads,
            _disableJitForSourceSpatialCounts,
            preferSerialSourceSpatialCounts: _preferSerialSourceSpatialCounts,
            secretReferenceResolver: _secretReferenceResolver,
            resolvedConnectionStringValidator: _resolvedConnectionStringValidator);
    }

    public ITileProvider CreateTileProviderForBinding(FeatureProviderBinding binding)
    {
        var reader = CreateReaderForBinding(binding);
        return reader as ITileProvider
            ?? throw new InvalidOperationException(
                $"PostGIS storage binding '{binding.StorageBinding.Metadata.Id}' does not expose a connection-bound tile provider.");
    }

    #region Core CRUD Operations

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        // Enforce the layer's permanent (row-visibility) filter: a row hidden by
        // the metadata-v2 PermanentFilter must not be readable item-by-id either.
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        if (query.EnforcedSqlFilter is null && query.EnforcedMaskedFields is null)
        {
            // No enforced row-visibility filter and no field-level masking apply — take the
            // optimized by-id read path. When either applies we fall through to the select
            // builder so the enforced filter and column masking are honored.
            return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken);
        }

        // Capture the full row token and apply its field mask from the same database
        // snapshot. A second unmasked read could race the projected read. The row
        // visibility filter remains enforced by this query; raw attributes never
        // leave this provider method.
        var maskedFields = query.EnforcedMaskedFields ?? ImmutableArray<string>.Empty;
        query = query with
        {
            ObjectIds = ImmutableArray.Create(featureId),
            Limit = 1,
            EnforcedMaskedFields = ImmutableArray<string>.Empty
        };
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
        if (features.IsDefaultOrEmpty)
        {
            return null;
        }

        var feature = features[0];
        if (maskedFields.IsDefaultOrEmpty)
        {
            return feature;
        }

        var masked = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return feature with
        {
            ReadStateToken = FeatureStateToken.Compute(feature),
            EditValidationAttributes = feature.Attributes,
            Attributes = feature.Attributes.RemoveRange(feature.Attributes.Keys.Where(masked.Contains))
        };
    }

    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        AgentFeatureWriteGuard.EnsureAllowed();
        var filter = await _readSecurity.ResolveRowLevelSecurityFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        return filter is null
            ? await _dataAccess.CreateFeatureAsync(layerId, feature, cancellationToken).ConfigureAwait(false)
            : await _dataAccess.CreateFeatureAsync(new FeatureWritePolicy(layerId, filter), feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        AgentFeatureWriteGuard.EnsureAllowed();
        var maskedFields = await ResolveMaskedFieldsAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (!maskedFields.IsDefaultOrEmpty)
        {
            feature = MergePreservedAttributeNames(feature, maskedFields);
        }

        var filter = await _readSecurity.ResolveRowLevelSecurityFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        return filter is null
            ? await _dataAccess.UpdateFeatureAsync(layerId, feature, cancellationToken).ConfigureAwait(false)
            : await _dataAccess.UpdateFeatureAsync(new FeatureWritePolicy(layerId, filter), feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        AgentFeatureWriteGuard.EnsureAllowed();
        var filter = await _readSecurity.ResolveRowLevelSecurityFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        return filter is null
            ? await _dataAccess.DeleteFeatureAsync(layerId, featureId, cancellationToken).ConfigureAwait(false)
            : await _dataAccess.DeleteFeatureAsync(new FeatureWritePolicy(layerId, filter), featureId, cancellationToken).ConfigureAwait(false);
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
        var objectIds = await StreamObjectIdsAsync(layerId, query, cancellationToken).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. objectIds];
    }

    public async IAsyncEnumerable<long> StreamObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query, geometryStorageType);
        await foreach (var objectId in _dataAccess.StreamSelectObjectIdsQueryAsync(new ObjectIdQueryRequest(objectIdsQuery, query, layerId), cancellationToken).ConfigureAwait(false))
        {
            yield return objectId;
        }
    }

    public async Task<IReadOnlySet<long>> QueryPreChangeObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        IReadOnlyCollection<long> changeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeIds);
        if (changeIds.Count == 0)
        {
            return new HashSet<long>();
        }

        // The enforced read policy (permanent filter, row-level security) is stamped exactly as for a live
        // read, so a pre-change image the caller could not read never matches.
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        query = query with { Limit = null, Offset = null, OrderBy = null };
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildPreChangeObjectIdsQuery(layerId, query, changeIds, geometryStorageType);
        var objectIds = await _dataAccess.ExecuteSelectObjectIdsQueryAsync(
            objectIdsQuery,
            query,
            layerId,
            cancellationToken).ConfigureAwait(false);
        return objectIds.ToHashSet();
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
        // The native encoder shifts properties after null values and does not encode all
        // temporal types. Reuse the canonical feature SELECT and one shared format writer.
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, query, geometryStorageType);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);
        return FlatGeobufFeatureWriter.Write(features, new FlatGeobufFeatureWriter.ExportOptions
        {
            Resource = resource,
            Query = query,
            ProjectedFields = FeatureQueryBuilder.GetEncodedBinaryAttributeFields(resource, query),
            OutputSrid = query.OutputSrid ?? resource.Spatial?.StorageCrs?.ResolveSrid() ?? resource.ReadSrid()
        }, cancellationToken);
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
        FeatureQuerySecurity.ValidateDateBins(query, dateBin);
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
        FeatureQuerySecurity.ValidateBins(query, binDefinition);
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
        FeatureQuerySecurity.ValidateH3(query, h3Query);
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
        if (query.EnforcedMaskedFields is { IsDefaultOrEmpty: false } maskedFields &&
            maskedFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

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
        if (_queryBuilder is FeatureQueryBuilder postgresBuilder)
        {
            return await MvtTileBudgetFitter.ReadAsync(
                tileLimits.MaxTileSize,
                step => postgresBuilder.BuildMvtTileQuery(
                    layerId, x, y, z, query, tileOptions, tileLimits, geometryStorageType, gridGeometry, step),
                (tileQuery, maxTileSize, token) => _dataAccess.GetMvtTileAsync(layerId, tileQuery, maxTileSize, token),
                cancellationToken).ConfigureAwait(false);
        }

        var tileQuery = _queryBuilder.BuildMvtTileQuery(layerId, x, y, z, query, tileOptions, tileLimits, geometryStorageType, gridGeometry);
        return await _dataAccess.GetMvtTileAsync(layerId, tileQuery, tileLimits.MaxTileSize, cancellationToken);
    }

    public async Task<Core.Features.Tiles.PooledTileData?> GetMvtTilePooledAsync(
        Core.Features.Tiles.MvtTileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_dataAccess is not FeatureDataAccess postgresDataAccess)
        {
            var bytes = await GetMvtTileAsync(
                request.LayerId, request.X, request.Y, request.Z, request.Query,
                request.TileOptions, request.TileLimits, request.GridGeometry, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : Core.Features.Tiles.PooledTileData.FromOwnedArray(bytes);
        }

        var effectiveQuery = request.Query.HasValue
            ? await ApplyPermanentFilterAsync(request.LayerId, request.Query.Value, cancellationToken).ConfigureAwait(false)
            : await ApplyPermanentFilterAsync(request.LayerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        if (_queryBuilder is FeatureQueryBuilder postgresBuilder)
        {
            return await MvtTileBudgetFitter.ReadPooledAsync(
                new MvtTileBudgetRead<Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery>(
                    request.TileLimits.MaxTileSize,
                    step => postgresBuilder.BuildMvtTileQuery(
                        request.LayerId, request.X, request.Y, request.Z, effectiveQuery,
                        request.TileOptions, request.TileLimits, geometryStorageType, request.GridGeometry, step),
                    (tileQuery, maxTileSize, token) => postgresDataAccess.GetMvtTilePooledAsync(tileQuery, maxTileSize, token)),
                cancellationToken).ConfigureAwait(false);
        }

        var tileQuery = _queryBuilder.BuildMvtTileQuery(
            request.LayerId, request.X, request.Y, request.Z, effectiveQuery,
            request.TileOptions, request.TileLimits, geometryStorageType, request.GridGeometry);
        return await postgresDataAccess.GetMvtTilePooledAsync(tileQuery, request.TileLimits.MaxTileSize, cancellationToken).ConfigureAwait(false);
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
        return await _dataAccess.GetMvtTileAsync(layerId, tileQuery, tileLimits.MaxTileSize, cancellationToken);
    }

    #endregion

    #region Batch Operations

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
    {
        editBatch = await PrepareEditBatchAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        return await _dataAccess.ApplyEditsAsync(layerId, editBatch, cancellationToken);
    }

    public async Task<FeatureEditApplication> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditIdempotencyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        editBatch = await PrepareEditBatchAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        return await _dataAccess.ApplyEditsAsync(layerId, editBatch, receipt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        AgentFeatureWriteGuard.EnsureAllowed();
        var inner = await _dataAccess.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new SecuredWriterTransaction(this, inner);
    }

    private async Task<FeatureEditBatch> PrepareEditBatchAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken)
    {
        AgentFeatureWriteGuard.EnsureAllowed();
        var hasUpdates = !editBatch.Updates.IsDefaultOrEmpty
            || (!editBatch.Operations.IsDefaultOrEmpty
                && editBatch.Operations.Any(static op => op.Kind == FeatureEditOperationKind.Update && op.Feature.HasValue));
        if (!editBatch.Preconditions.IsDefaultOrEmpty || hasUpdates)
        {
            // Preserve omitted masked fields inside the provider transaction on every
            // update/replace path so field-masked callers cannot wipe hidden columns to NULL,
            // while keeping the read token over the full row.
            var maskedFields = await ResolveMaskedFieldsAsync(layerId, cancellationToken).ConfigureAwait(false);
            editBatch = ApplyMaskedFieldsToEditBatch(editBatch, maskedFields);
        }

        var writeCheck = await _readSecurity
            .ResolveRowLevelSecurityFilterAsync(layerId, cancellationToken)
            .ConfigureAwait(false);
        return editBatch with { RowSecurityWriteCheck = writeCheck };
    }

    private sealed class SecuredWriterTransaction(
        PostgresFeatureStoreRefactored owner,
        IFeatureWriterTransaction inner) : IFeatureWriterTransaction
    {
        public async Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            editBatch = await owner.PrepareEditBatchAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            return await inner.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        }

        public bool SupportsDurableReceipts => inner.SupportsDurableReceipts;

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(
            FeatureWriterTransactionReceipt receipt,
            CancellationToken cancellationToken = default)
            => inner.CommitAsync(receipt, cancellationToken);

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
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
        // Enforce permanent (row-visibility) filters and field masks on both sides of the
        // relationship: hidden origin rows and masked key fields must not resolve foreign keys,
        // and hidden related rows or masked attributes must not be readable through queryRelatedRecords.
        var security = await ResolveRelatedReadSecurityAsync(
            new RelatedReadSecurityRequest
            {
                OriginLayerId = layerId,
                RelatedLayerId = query.RelatedLayerId,
                OriginForeignKeyField = query.OriginForeignKeyField,
                DestinationForeignKeyField = query.DestinationForeignKeyField
            },
            cancellationToken).ConfigureAwait(false);
        if (_dataAccess is FeatureDataAccess postgresDataAccess &&
            (security.OriginFilter is not null || security.RelatedFilter is not null || !security.RelatedMasks.IsDefaultOrEmpty))
        {
            return await postgresDataAccess.QueryRelatedAsync(
                layerId,
                query,
                security.OriginFilter,
                security.RelatedFilter,
                security.RelatedMasks,
                cancellationToken).ConfigureAwait(false);
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
            // A page cut short of the nearest count reads one probe row so the more-results flag is exact.
            var probe = query.NearestNeighborCountExceedsLimit();
            var knnQuery = probe ? query with { Limit = query.Limit + 1 } : query;
            var knnSelectQuery = buildSelect(layerId, knnQuery, geometryStorageType);
            var knnFeatures = await executeSelect(knnSelectQuery, knnQuery, layerId, cancellationToken);
            var hasMoreNearest = probe && knnFeatures.Length > query.Limit;
            if (hasMoreNearest)
            {
                knnFeatures = knnFeatures.RemoveAt(knnFeatures.Length - 1);
            }

            return knnFeatures.Length == 0 && !hasMoreNearest
                ? QueryResult<T>.Empty()
                : QueryResult<T>.Create(knnFeatures.Length, knnFeatures, hasMoreNearest);
        }

        // PERFORMANCE OPTIMIZATION: Use single query with window function instead of separate count + select
        // This reduces database round trips from 2 to 1, improving performance by 30-50%.
        // returnDistance queries opt out: the window-count select builder does not project the
        // runtime distance column, so they fall through to the count + plain-select path (the
        // plain select clause emits the distance column). Distance is only meaningful with a
        // spatial filter, so this affects a narrow slice of queries.
        if ((query.Limit.HasValue || query.Offset.HasValue) &&
            !query.Distinct &&
            !FeatureQueryBuilder.ShouldComputeDistance(query.SpatialFilter))
        {
            return await executeOptimized(layerId, query, geometryStorageType, cancellationToken);
        }

        // Distinct and distance projections also use this fallback, even with a page limit.
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query, geometryStorageType);
        var totalCount = await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken);

        if (totalCount == 0)
        {
            return QueryResult<T>.Empty();
        }

        var requestedLimit = query.Limit.GetValueOrDefault();
        var probePage = query.Limit.HasValue && requestedLimit >= 0 && requestedLimit < int.MaxValue;
        var pageQuery = probePage ? query with { Limit = requestedLimit + 1 } : query;
        var selectQuery = buildSelect(layerId, pageQuery, geometryStorageType);
        var features = await executeSelect(selectQuery, pageQuery, layerId, cancellationToken);

        // A source-row count cannot establish whether another DISTINCT projection exists.
        // Probe one projected row, as QueryPageAsync does, and keep the existing total-count
        // and output contracts. The extra row must never leak into the returned page.
        var hasMoreResults = probePage && features.Length > requestedLimit;
        var items = hasMoreResults ? features.Take(requestedLimit).ToImmutableArray() : features;
        return QueryResult<T>.Create(totalCount, items, hasMoreResults);
    }

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
        if (_dataAccess is FeatureDataAccess postgresDataAccess)
        {
            var request = new SelectQueryRequest { Query = optimizedQuery, FeatureQuery = query, LayerId = layerId };
            return await ExecutePostgresOptimizedQueryAsync(
                postgresDataAccess,
                new OptimizedQueryContext<Feature>
                {
                    LayerId = layerId,
                    Query = query,
                    GeometryStorageType = geometryStorageType,
                    ExecuteAsync = access => access.ExecuteOptimizedSelectQueryAsync(request, cancellationToken)
                },
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteStreamOptimizedFallbackAsync(
            new StreamOptimizedFallbackContext<Feature>
            {
                LayerId = layerId,
                Query = query,
                GeometryStorageType = geometryStorageType,
                Stream = _dataAccess.StreamFeaturesAsync(layerId, optimizedQuery, query, cancellationToken),
                GetAttributes = static feature => feature.Attributes,
                RemoveInternal = RemoveInternalAttributes
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult<GmlFeature>> QueryOptimizedGmlAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectGmlQuery(layerId, query, geometryStorageType);
        if (_dataAccess is FeatureDataAccess postgresDataAccess)
        {
            var request = new SelectQueryRequest { Query = optimizedQuery, FeatureQuery = query, LayerId = layerId };
            return await ExecutePostgresOptimizedQueryAsync(
                postgresDataAccess,
                new OptimizedQueryContext<GmlFeature>
                {
                    LayerId = layerId,
                    Query = query,
                    GeometryStorageType = geometryStorageType,
                    ExecuteAsync = access => access.ExecuteOptimizedSelectGmlQueryAsync(request, cancellationToken)
                },
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteStreamOptimizedFallbackAsync(
            new StreamOptimizedFallbackContext<GmlFeature>
            {
                LayerId = layerId,
                Query = query,
                GeometryStorageType = geometryStorageType,
                Stream = _dataAccess.StreamGmlFeaturesAsync(layerId, optimizedQuery, query, cancellationToken),
                GetAttributes = static feature => feature.Attributes,
                RemoveInternal = RemoveInternalAttributes
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult<EncodedGeoJsonFeature>> QueryOptimizedGeoJsonAsync(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        CancellationToken cancellationToken)
    {
        var optimizedQuery = _queryBuilder.BuildOptimizedSelectGeoJsonQuery(layerId, query, geometryStorageType);
        if (_dataAccess is FeatureDataAccess postgresDataAccess)
        {
            var request = new SelectQueryRequest { Query = optimizedQuery, FeatureQuery = query, LayerId = layerId };
            return await ExecutePostgresOptimizedQueryAsync(
                postgresDataAccess,
                new OptimizedQueryContext<EncodedGeoJsonFeature>
                {
                    LayerId = layerId,
                    Query = query,
                    GeometryStorageType = geometryStorageType,
                    ExecuteAsync = access => access.ExecuteOptimizedSelectGeoJsonQueryAsync(request, cancellationToken)
                },
                cancellationToken).ConfigureAwait(false);
        }

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
        if (_dataAccess is FeatureDataAccess postgresDataAccess)
        {
            var request = new SelectQueryRequest { Query = optimizedQuery, FeatureQuery = query, LayerId = layerId };
            return await ExecutePostgresOptimizedQueryAsync(
                postgresDataAccess,
                new OptimizedQueryContext<KmlFeature>
                {
                    LayerId = layerId,
                    Query = query,
                    GeometryStorageType = geometryStorageType,
                    ExecuteAsync = access => access.ExecuteOptimizedSelectKmlQueryAsync(request, cancellationToken)
                },
                cancellationToken).ConfigureAwait(false);
        }

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

    // The enforcement itself lives in LayerReadSecurityResolver so the spatial analytics
    // reader applies exactly the same permanent filter, RLS predicate and field masks.
    private Task<FeatureQuery> ApplyPermanentFilterAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
        => _readSecurity.ApplyAsync(layerId, query, cancellationToken);

    private Task<ImmutableArray<string>> ResolveMaskedFieldsAsync(int layerId, CancellationToken cancellationToken)
        => _readSecurity.ResolveMaskedFieldsAsync(layerId, cancellationToken);

    private Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(int layerId, CancellationToken cancellationToken)
        => _readSecurity.ResolveEnforcedSqlFilterAsync(layerId, cancellationToken);

    private async Task<MetadataV2Object> GetMetadataResourceAsync(int layerId, CancellationToken cancellationToken)
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
