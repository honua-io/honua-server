// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

/// <summary>
/// Defines a buffer-and-aggregate operation that buffers each input feature by
/// a fixed distance and either dissolves the result with <c>ST_Union</c> or
/// returns the per-row buffers, optionally grouped by attribute fields.
/// </summary>
public readonly record struct BufferAggregateQuery
{
    /// <summary>
    /// Buffer distance value. Interpreted in <see cref="Unit"/>; converted to
    /// meters before binding to the SQL parameter.
    /// </summary>
    public required double Distance { get; init; }

    /// <summary>
    /// Distance unit for <see cref="Distance"/>. Defaults to meters.
    /// </summary>
    public DistanceUnit Unit { get; init; }

    /// <summary>
    /// When true (default), buffered geometries are merged with <c>ST_Union</c>
    /// per group. When false, the response is one row per source feature with
    /// its individual buffer geometry.
    /// </summary>
    public bool Dissolve { get; init; }

    /// <summary>
    /// Optional attribute fields to group buffers by. When omitted, all matching
    /// features are dissolved into a single result row (or returned individually
    /// when <see cref="Dissolve"/> is false).
    /// </summary>
    public ImmutableArray<string>? GroupByFields { get; init; }

    /// <summary>
    /// Optional aggregate statistics computed per group.
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Maximum number of input features the operation will process before
    /// short-circuiting. Enforced via <c>LIMIT n+1</c> overflow detection.
    /// </summary>
    public int MaxInputFeatures { get; init; }

    /// <summary>
    /// Minimum allowed buffer distance (in meters after unit conversion).
    /// </summary>
    public const double MinDistanceMeters = 0d;

    /// <summary>
    /// Maximum allowed buffer distance in meters. Caps unbounded buffer growth
    /// that would otherwise create planet-sized geometries.
    /// </summary>
    public const double MaxDistanceMeters = 1_000_000d;
}
