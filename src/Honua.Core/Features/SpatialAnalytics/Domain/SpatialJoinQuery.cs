// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

/// <summary>
/// Canonical spatial predicates for every spatial join in the platform — the SQL
/// pushdown (PostGIS) and the managed (NetTopologySuite) executors both evaluate
/// these members, so a predicate means exactly one thing everywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operand convention (honua-server#3069).</b> The containment members name
/// <i>both</i> operands in the member name itself — <c>JoinContainsTarget</c> /
/// <c>TargetContainsJoin</c> — instead of relying on a bare "contains" / "within"
/// that reads differently depending on which geometry the reader assumes is the
/// subject. Two independent enums previously used the same bare words with
/// opposite operand orders, which silently inverted synchronous point-in-polygon
/// enrichment; spelling the operands out removes the ambiguity structurally.
/// </para>
/// <para>
/// Protocol adapters own the mapping from their wire vocabulary to these members
/// and must state the direction they mean. The vocabularies deliberately differ:
/// the layer-scoped spatial-join endpoints are target-subject
/// (<c>predicate=contains</c> means the target/route layer contains the join
/// geometry), while the enrichment API is dataset-subject
/// (<c>method=point-in-polygon</c> means the enrichment dataset polygon contains
/// the caller's source feature).
/// </para>
/// </remarks>
public enum SpatialJoinPredicate
{
    /// <summary>
    /// Target and join geometries intersect (<c>ST_Intersects</c>). Symmetric —
    /// operand order does not change the result.
    /// </summary>
    Intersects,

    /// <summary>
    /// The join geometry contains the target geometry —
    /// <c>ST_Contains(join, target)</c> / <c>join.Contains(target)</c>. This is the
    /// classic point-in-polygon direction: a polygon on the join/reference layer
    /// containing a point on the target layer.
    /// </summary>
    JoinContainsTarget,

    /// <summary>
    /// The target geometry contains the join geometry —
    /// <c>ST_Contains(target, join)</c> / <c>target.Contains(join)</c>. The inverse
    /// of <see cref="JoinContainsTarget"/>: a polygon on the target layer
    /// containing a point on the join/reference layer.
    /// </summary>
    TargetContainsJoin,

    /// <summary>
    /// Target geometry is within <see cref="SpatialJoinQuery.DistanceMeters"/> of
    /// the join geometry (<c>ST_DWithin</c> on geography). Symmetric — operand
    /// order does not change the result.
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
    /// SRID of the join layer's stored geometry. The SQL builder uses this to
    /// tag the join geometry column when the storage format does not embed an
    /// SRID (Bytea WKB), and to transform the join geometry into the target
    /// layer's CRS so the spatial predicate is evaluated in a single coordinate
    /// system. The caller resolves this from the join layer's catalog metadata
    /// at the same time it authorizes access.
    /// </summary>
    public int? JoinLayerSrid { get; init; }

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
