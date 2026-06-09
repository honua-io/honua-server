// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// SDK-compatible summary kinds for indexed spatial aggregations.
/// </summary>
public enum SpatialAggregationSummaryKind
{
    /// <summary>Count of features in the aggregation.</summary>
    Count,

    /// <summary>Sum of a numeric field across the aggregation.</summary>
    Sum,

    /// <summary>Average of a numeric field across the aggregation.</summary>
    Avg,

    /// <summary>Minimum value of a numeric field across the aggregation.</summary>
    Min,

    /// <summary>Maximum value of a numeric field across the aggregation.</summary>
    Max,

    /// <summary>Distribution of features across categorical values.</summary>
    Category,

    /// <summary>Distribution of a numeric field across histogram bins.</summary>
    Histogram,

    /// <summary>Distribution of a numeric field across explicit ranges.</summary>
    Range
}

/// <summary>
/// Provider-facing definition for one indexed spatial aggregation summary.
/// </summary>
public readonly record struct SpatialAggregationSummaryDefinition
{
    /// <summary>Stable identifier for the summary within its layer.</summary>
    public required string Id { get; init; }

    /// <summary>Aggregation kind that determines how the summary is computed.</summary>
    public required SpatialAggregationSummaryKind Kind { get; init; }

    /// <summary>Optional human-readable title for the summary.</summary>
    public string? Title { get; init; }

    /// <summary>Source field the summary aggregates, when applicable.</summary>
    public string? Field { get; init; }

    /// <summary>Declared type of the source field, when known.</summary>
    public MetadataV2FieldType? FieldType { get; init; }

    /// <summary>Optional unit label for numeric summaries.</summary>
    public string? Unit { get; init; }

    /// <summary>Maximum number of categories to retain for category summaries.</summary>
    public int? CategoryLimit { get; init; }

    /// <summary>Whether categories beyond the limit are folded into an "other" bucket.</summary>
    public bool IncludeOther { get; init; }

    /// <summary>Optional ordering expression for category summaries.</summary>
    public string? CategoryOrderBy { get; init; }

    /// <summary>Explicit category buckets selected from the totals query, when fixed.</summary>
    public ImmutableArray<SpatialAggregationCategoryBucketDefinition>? CategoryBuckets { get; init; }

    /// <summary>Number of histogram bins for histogram summaries.</summary>
    public int? HistogramBins { get; init; }

    /// <summary>Lower bound of the histogram domain, when fixed.</summary>
    public double? HistogramMin { get; init; }

    /// <summary>Upper bound of the histogram domain, when fixed.</summary>
    public double? HistogramMax { get; init; }

    /// <summary>Optional method used to derive histogram bins.</summary>
    public string? HistogramMethod { get; init; }

    /// <summary>Explicit range buckets for range summaries.</summary>
    public ImmutableArray<SpatialAggregationRangeBucketDefinition>? Ranges { get; init; }
}

/// <summary>
/// Stable category bucket selected from the authoritative totals query.
/// </summary>
public readonly record struct SpatialAggregationCategoryBucketDefinition
{
    /// <summary>Categorical value the bucket represents.</summary>
    public required string Value { get; init; }

    /// <summary>Optional display label for the categorical value.</summary>
    public string? Label { get; init; }
}

/// <summary>
/// Range bucket requested by a spatial aggregation summary.
/// </summary>
public readonly record struct SpatialAggregationRangeBucketDefinition
{
    /// <summary>Stable identifier for the range bucket.</summary>
    public required string Id { get; init; }

    /// <summary>Optional display label for the range bucket.</summary>
    public string? Label { get; init; }

    /// <summary>Inclusive or exclusive lower bound of the range, when set.</summary>
    public double? Min { get; init; }

    /// <summary>Inclusive or exclusive upper bound of the range, when set.</summary>
    public double? Max { get; init; }

    /// <summary>Whether the lower bound is inclusive.</summary>
    public bool? IncludeMin { get; init; }

    /// <summary>Whether the upper bound is inclusive.</summary>
    public bool? IncludeMax { get; init; }
}
