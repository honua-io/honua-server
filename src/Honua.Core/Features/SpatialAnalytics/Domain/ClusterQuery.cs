// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

/// <summary>
/// Algorithms supported by the spatial clustering endpoint.
/// </summary>
public enum ClusterAlgorithm
{
    /// <summary>
    /// Density-Based Spatial Clustering of Applications with Noise (PostGIS <c>ST_ClusterDBSCAN</c>).
    /// Driven by <see cref="ClusterQuery.Eps"/> and <see cref="ClusterQuery.MinPoints"/>.
    /// </summary>
    DbScan,

    /// <summary>
    /// K-Means partitional clustering (PostGIS <c>ST_ClusterKMeans</c>).
    /// Driven by <see cref="ClusterQuery.K"/>.
    /// </summary>
    KMeans,
}

/// <summary>
/// Defines a spatial clustering operation that assigns each input feature
/// (filtered by the shared <see cref="FeatureQuery"/>) a stable cluster id and
/// optionally returns aggregate statistics or per-cluster hull geometry.
/// </summary>
public readonly record struct ClusterQuery
{
    /// <summary>
    /// Clustering algorithm to apply.
    /// </summary>
    public required ClusterAlgorithm Algorithm { get; init; }

    /// <summary>
    /// DBSCAN <c>eps</c> distance — features within this distance of each other
    /// are placed in the same cluster. Interpreted in <see cref="DistanceUnit"/>;
    /// defaults to meters when <see cref="DistanceUnit"/> is unset.
    /// Required when <see cref="Algorithm"/> is <see cref="ClusterAlgorithm.DbScan"/>.
    /// </summary>
    public double? Eps { get; init; }

    /// <summary>
    /// DBSCAN <c>minpoints</c> — minimum number of features required to form a cluster.
    /// Required when <see cref="Algorithm"/> is <see cref="ClusterAlgorithm.DbScan"/>.
    /// </summary>
    public int? MinPoints { get; init; }

    /// <summary>
    /// K-Means partition count.
    /// Required when <see cref="Algorithm"/> is <see cref="ClusterAlgorithm.KMeans"/>.
    /// </summary>
    public int? K { get; init; }

    /// <summary>
    /// Unit for the <see cref="Eps"/> distance value. Defaults to meters.
    /// </summary>
    public DistanceUnit DistanceUnit { get; init; }

    /// <summary>
    /// When true, the response is one row per cluster containing the convex hull
    /// (<c>ST_ConvexHull</c>) of its members plus aggregate statistics. When false,
    /// the response is one row per source feature carrying the assigned <c>clusterId</c>.
    /// </summary>
    public bool ReturnHullPerCluster { get; init; }

    /// <summary>
    /// Optional aggregate statistics computed per cluster. Only honored when
    /// <see cref="ReturnHullPerCluster"/> is true.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Maximum number of input features the operation will process before
    /// short-circuiting with an "input too large" error. The SQL applies
    /// <c>LIMIT n+1</c> so the handler can detect overflow without scanning further.
    /// </summary>
    public int MaxInputFeatures { get; init; }

    /// <summary>
    /// Maximum number of aggregated cluster rows returned in hull-per-cluster mode.
    /// The SQL applies <c>LIMIT n+1</c> after the <c>GROUP BY</c> so the handler
    /// can detect result truncation without materializing the full cluster set.
    /// Ignored when <see cref="ReturnHullPerCluster"/> is false.
    /// </summary>
    public int MaxClusters { get; init; }

    /// <summary>
    /// Minimum allowed value for DBSCAN <see cref="Eps"/> (meters or selected unit).
    /// Prevents pathological queries that would force every feature into its own cluster.
    /// </summary>
    public const double MinEps = 0d;

    /// <summary>
    /// Minimum allowed value for DBSCAN <see cref="MinPoints"/>.
    /// </summary>
    public const int MinMinPoints = 1;

    /// <summary>
    /// Minimum allowed K-Means partition count.
    /// </summary>
    public const int MinK = 1;

    /// <summary>
    /// Maximum allowed K-Means partition count to keep query plans bounded.
    /// </summary>
    public const int MaxK = 10_000;
}
