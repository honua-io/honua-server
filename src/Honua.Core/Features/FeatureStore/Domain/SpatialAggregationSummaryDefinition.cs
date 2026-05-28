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
    Count,
    Sum,
    Avg,
    Min,
    Max,
    Category,
    Histogram,
    Range
}

/// <summary>
/// Provider-facing definition for one indexed spatial aggregation summary.
/// </summary>
public readonly record struct SpatialAggregationSummaryDefinition
{
    public required string Id { get; init; }

    public required SpatialAggregationSummaryKind Kind { get; init; }

    public string? Title { get; init; }

    public string? Field { get; init; }

    public MetadataV2FieldType? FieldType { get; init; }

    public string? Unit { get; init; }

    public int? CategoryLimit { get; init; }

    public bool IncludeOther { get; init; }

    public string? CategoryOrderBy { get; init; }

    public ImmutableArray<SpatialAggregationCategoryBucketDefinition>? CategoryBuckets { get; init; }

    public int? HistogramBins { get; init; }

    public double? HistogramMin { get; init; }

    public double? HistogramMax { get; init; }

    public string? HistogramMethod { get; init; }

    public ImmutableArray<SpatialAggregationRangeBucketDefinition>? Ranges { get; init; }
}

/// <summary>
/// Stable category bucket selected from the authoritative totals query.
/// </summary>
public readonly record struct SpatialAggregationCategoryBucketDefinition
{
    public required string Value { get; init; }

    public string? Label { get; init; }
}

/// <summary>
/// Range bucket requested by a spatial aggregation summary.
/// </summary>
public readonly record struct SpatialAggregationRangeBucketDefinition
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool? IncludeMin { get; init; }

    public bool? IncludeMax { get; init; }
}
