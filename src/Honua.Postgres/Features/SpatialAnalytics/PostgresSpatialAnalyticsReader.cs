// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore.Services;

namespace Honua.Postgres.Features.SpatialAnalytics;

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
/// </remarks>
internal sealed class PostgresSpatialAnalyticsReader : ISpatialAnalyticsReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IFeatureCacheManager _cacheManager;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IFilterExpressionService? _filterExpressionService;

    public PostgresSpatialAnalyticsReader(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager,
        IMetadataV2GraphProvider? v2Provider = null,
        IFilterExpressionService? filterExpressionService = null)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _v2Provider = v2Provider;
        _filterExpressionService = filterExpressionService;
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
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
        targetQuery = await ApplyPermanentFilterAsync(targetLayerId, targetQuery, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);

        // The join layer needs its own permanent filter on the join side of the
        // LEFT JOIN: matchCount, carry fields and join-side statistics would
        // otherwise aggregate (and leak) rows the join layer hides from direct
        // queries. Threaded through the Postgres-specific builder overload.
        var joinLayerFilter = await PermanentFilterResolver
            .ResolveAsync(_v2Provider, _filterExpressionService, joinQuery.JoinLayerId, cancellationToken)
            .ConfigureAwait(false);
        var sqlQuery = joinLayerFilter is not null && _queryBuilder is FeatureQueryBuilder postgresQueryBuilder
            ? postgresQueryBuilder.BuildSpatialJoinQuery(
                targetLayerId, targetQuery, joinQuery, joinLayerFilter, geometryStorageType)
            : _queryBuilder.BuildSpatialJoinQuery(
                targetLayerId, targetQuery, joinQuery, geometryStorageType);
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
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
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
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildDensityQuery(layerId, query, densityQuery, geometryStorageType);
        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, query, layerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the layer's metadata-v2 permanent (row-visibility) filter to the
    /// query, mirroring <c>PostgresFeatureStoreRefactored</c> so the analytics
    /// surface enforces the same row-level visibility as direct queries.
    /// </summary>
    private async Task<FeatureQuery> ApplyPermanentFilterAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (query.EnforcedSqlFilter != null)
        {
            return query;
        }

        var enforcedFilter = await PermanentFilterResolver
            .ResolveAsync(_v2Provider, _filterExpressionService, layerId, cancellationToken)
            .ConfigureAwait(false);
        return enforcedFilter == null ? query : query with { EnforcedSqlFilter = enforcedFilter };
    }
}
