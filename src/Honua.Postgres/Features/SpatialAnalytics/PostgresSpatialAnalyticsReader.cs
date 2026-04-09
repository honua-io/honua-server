// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;

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

    public PostgresSpatialAnalyticsReader(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IFeatureCacheManager cacheManager)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CancellationToken cancellationToken = default)
    {
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
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildSpatialJoinQuery(
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
        var geometryStorageType = await _cacheManager
            .GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var sqlQuery = _queryBuilder.BuildDensityQuery(layerId, query, densityQuery, geometryStorageType);
        return await _dataAccess
            .ExecuteStatisticsQueryAsync(sqlQuery, query, layerId, cancellationToken)
            .ConfigureAwait(false);
    }
}
