// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

/// <summary>
/// Spatial predicates supported by the spatial join endpoint.
/// </summary>
public enum SpatialJoinPredicate
{
    /// <summary>
    /// Target geometry intersects the join geometry (<c>ST_Intersects</c>).
    /// </summary>
    Intersects,

    /// <summary>
    /// Target geometry contains the join geometry (<c>ST_Contains</c>).
    /// </summary>
    Contains,

    /// <summary>
    /// Target geometry is within the join geometry (<c>ST_Within</c>).
    /// </summary>
    Within,

    /// <summary>
    /// Target geometry is within <see cref="SpatialJoinQuery.DistanceMeters"/> of
    /// the join geometry (<c>ST_DWithin</c> on geography).
    /// </summary>
    DWithin,
}

/// <summary>
/// Defines a spatial join from a target layer (the layer named in the route)
/// to a join layer, optionally enriching target features with carried join
/// columns or aggregate statistics computed across matching join rows.
/// </summary>
public readonly record struct SpatialJoinQuery
{
    /// <summary>
    /// Identifier of the layer providing join geometry. The caller must have read
    /// access to both the target layer and this join layer.
    /// </summary>
    public required int JoinLayerId { get; init; }

    /// <summary>
    /// Spatial predicate that selects matching rows from the join layer.
    /// </summary>
    public required SpatialJoinPredicate Predicate { get; init; }

    /// <summary>
    /// Distance threshold in meters for <see cref="SpatialJoinPredicate.DWithin"/>.
    /// Ignored for other predicates.
    /// </summary>
    public double? DistanceMeters { get; init; }

    /// <summary>
    /// Join-layer attribute names to attach unchanged to matching target rows.
    /// When omitted (and <see cref="OutStatistics"/> is also empty), only the
    /// match count is added to the target row.
    /// </summary>
    public ImmutableArray<string>? CarryFields { get; init; }

    /// <summary>
    /// Aggregate statistics computed over the join-side rows that satisfy the
    /// predicate, partitioned by the target row.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Maximum number of target features processed by the join. Enforced via
    /// <c>LIMIT n+1</c> overflow detection in the generated SQL.
    /// </summary>
    public int MaxInputFeatures { get; init; }
}
