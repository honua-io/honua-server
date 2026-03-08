// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines a top features filter for queryTopFeatures operations.
/// Groups features by specified fields and returns the top N per group.
/// </summary>
public readonly record struct TopFilter
{
    /// <summary>
    /// Fields to partition (group) features by
    /// </summary>
    public required ImmutableArray<string> GroupByFields { get; init; }

    /// <summary>
    /// Number of top features to return per group
    /// </summary>
    public required int TopCount { get; init; }

    /// <summary>
    /// Fields and directions for ordering within each group
    /// </summary>
    public required ImmutableArray<OrderByClause> OrderByFields { get; init; }
}
