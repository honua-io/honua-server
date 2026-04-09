// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Abstractions;

/// <summary>
/// Provides read-only spatial analytics operations (clustering, spatial join,
/// buffer aggregate, density binning) layered on top of the existing feature
/// store. Each method honors the shared <see cref="FeatureQuery"/> filter
/// (where, objectIds, spatial, temporal) so analytics behavior matches the
/// rest of the FeatureServer / OGC Features surface (ADR-0009).
/// </summary>
public interface ISpatialAnalyticsReader
{
    /// <summary>
    /// Runs a spatial clustering pass over the filtered subset of <paramref name="layerId"/>.
    /// Returns one row per cluster (when <see cref="ClusterQuery.ReturnHullPerCluster"/>
    /// is true) or one row per source feature carrying its assigned <c>clusterId</c>.
    /// </summary>
    /// <param name="layerId">Target layer identifier.</param>
    /// <param name="query">Shared feature filter.</param>
    /// <param name="clusterQuery">Clustering parameters and safety caps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryClustersAsync(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins the filtered target-layer subset to a join layer using the requested
    /// spatial predicate, optionally enriching each target row with carry fields
    /// or aggregate statistics computed across matching join rows.
    /// </summary>
    /// <param name="targetLayerId">Layer providing target geometry/attributes.</param>
    /// <param name="targetQuery">Filter applied to the target layer.</param>
    /// <param name="joinQuery">Join layer id, predicate and column selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QuerySpatialJoinAsync(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Buffers each input feature by a fixed distance and either dissolves the
    /// result with <c>ST_Union</c> per group or returns the per-row buffers.
    /// </summary>
    /// <param name="layerId">Target layer identifier.</param>
    /// <param name="query">Shared feature filter.</param>
    /// <param name="bufferQuery">Buffer distance, dissolve mode and grouping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBufferAggregateAsync(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bins the filtered input set into a hex or square grid in Web Mercator
    /// and returns one row per occupied cell with the cell boundary geometry
    /// and a feature count or weighted sum.
    /// </summary>
    /// <param name="layerId">Target layer identifier.</param>
    /// <param name="query">Shared feature filter.</param>
    /// <param name="densityQuery">Binning mode, cell size and weight field.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDensityAsync(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        CancellationToken cancellationToken = default);
}
