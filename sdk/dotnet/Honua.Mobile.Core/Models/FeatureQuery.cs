// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents a feature query with all possible parameters.
/// </summary>
public sealed record FeatureQuery
{
    /// <summary>
    /// SQL-like where clause for filtering features.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Specific object IDs to query.
    /// </summary>
    public IReadOnlyList<long>? ObjectIds { get; init; }

    /// <summary>
    /// Fields to return in the result (null means all fields).
    /// </summary>
    public IReadOnlyList<string>? OutFields { get; init; }

    /// <summary>
    /// Whether to include geometry in the results.
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Spatial reference for the output geometry.
    /// </summary>
    public SpatialReference? OutputSpatialReference { get; init; }

    /// <summary>
    /// Number of records to skip (for pagination).
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Maximum number of records to return (for pagination).
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Field(s) to order results by.
    /// </summary>
    public string? OrderBy { get; init; }

    /// <summary>
    /// Whether to return only distinct values.
    /// </summary>
    public bool Distinct { get; init; }

    /// <summary>
    /// Spatial filter for geometric queries.
    /// </summary>
    public SpatialFilter? SpatialFilter { get; init; }

    /// <summary>
    /// Statistical operations to perform.
    /// </summary>
    public IReadOnlyList<StatisticDefinition>? Statistics { get; init; }

    /// <summary>
    /// Fields to group results by (for statistics).
    /// </summary>
    public IReadOnlyList<string>? GroupByFields { get; init; }

    /// <summary>
    /// Creates an empty query.
    /// </summary>
    public static FeatureQuery Empty => new();
}