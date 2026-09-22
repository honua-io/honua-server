// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.SpatialAnalytics;

/// <summary>
/// PostgreSQL/PostGIS implementation of <see cref="ISpatialAnalyticsReader"/>.
/// Each method is a thin façade over <see cref="IFeatureQueryBuilder"/> (which
/// emits the SQL) and <see cref="IFeatureDataAccess.ExecuteStatisticsQueryAsync"/>
/// (which runs the query and reshapes the rows into the dictionary form shared
/// with statistics, date bins, H3 and other analytics endpoints).
/// </summary>
/// <remarks>
/// The reader keeps the same shape as <c>PostgresFeatureStoreRefactored.QueryH3Async</c>
/// so that telemetry, slow-query logging and result conversion all flow through
/// the existing data-access pipeline. Cross-cutting concerns like edition gating,
/// limit enforcement and overflow detection live in the request handler so the
/// reader stays focused on storage interaction.
/// <para>
/// Read policy (permanent filter, row-level security, field masks) is resolved through
/// <see cref="LayerReadSecurityResolver"/>, the same implementation the feature store
/// uses, so an analytics operation sees exactly the rows and fields a direct query by
/// the same caller would. A spatial join resolves the policy of both layers.
/// </para>
/// </remarks>
internal sealed class PostgresSpatialAnalyticsReader : ISpatialAnalyticsReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly LayerReadSecurityResolver _readSecurity;

    public PostgresSpatialAnalyticsReader(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        IMetadataV2GraphProvider? v2Provider = null,
        IFilterExpressionService? filterExpressionService = null,
        IRowLevelSecurityFilterSource? rlsFilterSource = null,
        IFieldMaskSource? fieldMaskSource = null)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _readSecurity = new LayerReadSecurityResolver(v2Provider, filterExpressionService, rlsFilterSource, fieldMaskSource);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CancellationToken cancellationToken = default)
    {
        query = await _readSecurity.ApplyAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        FeatureQuerySecurity.ValidateClusters(query, clusterQuery);
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildClusterQuery(layerId, query, clusterQuery, geometryStorageType);
        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, query, layerId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QuerySpatialJoinAsync(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        CancellationToken cancellationToken = default)
    {
        targetQuery = await _readSecurity.ApplyAsync(targetLayerId, targetQuery, cancellationToken).ConfigureAwait(false);

        // The join layer carries its own read policy. Its enforced row filter (permanent
        // filter AND row-level security) is applied on the join side of the LEFT JOIN so
        // matchCount, carry fields and join-side statistics only aggregate rows a direct
        // query of the join layer would return; its field masks decide which join-layer
        // fields carryFields / outStatistics may name.
        var joinLayerFilter = await _readSecurity
            .ResolveEnforcedSqlFilterAsync(joinQuery.JoinLayerId, cancellationToken)
            .ConfigureAwait(false);
        var joinLayerMaskedFields = await _readSecurity
            .ResolveMaskedFieldsAsync(joinQuery.JoinLayerId, cancellationToken)
            .ConfigureAwait(false);
        FeatureQuerySecurity.ValidateSpatialJoin(targetQuery, joinQuery, joinLayerMaskedFields);

        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        CoreParameterizedQuery sqlQuery;
        if (joinLayerFilter is null)
        {
            sqlQuery = _queryBuilder.BuildSpatialJoinQuery(
                targetLayerId, targetQuery, joinQuery, geometryStorageType);
        }
        else if (_queryBuilder is FeatureQueryBuilder postgresQueryBuilder)
        {
            sqlQuery = postgresQueryBuilder.BuildSpatialJoinQuery(
                targetLayerId, targetQuery, joinQuery, joinLayerFilter, geometryStorageType);
        }
        else
        {
            // Only the PostgreSQL builder can place the join layer's filter on the join
            // side. Refuse rather than run the join without it.
            throw new InvalidOperationException(
                "The join layer has an enforced row filter that the configured query builder cannot apply.");
        }

        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, targetQuery, targetLayerId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBufferAggregateAsync(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        CancellationToken cancellationToken = default)
    {
        query = await _readSecurity.ApplyAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        FeatureQuerySecurity.ValidateBufferAggregate(query, bufferQuery);
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildBufferAggregateQuery(
            layerId, query, bufferQuery, geometryStorageType);
        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, query, layerId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDensityAsync(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        CancellationToken cancellationToken = default)
    {
        query = await _readSecurity.ApplyAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        FeatureQuerySecurity.ValidateDensity(query, densityQuery);
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildDensityQuery(layerId, query, densityQuery, geometryStorageType);
        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, query, layerId, cancellationToken)
            .ConfigureAwait(false);
    }
}
