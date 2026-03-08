// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines how to bin features into value intervals for queryBins operations
/// </summary>
public readonly record struct BinDefinition
{
    /// <summary>
    /// The type of binning algorithm to apply
    /// </summary>
    public required BinType Type { get; init; }

    /// <summary>
    /// The field to bin on
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Interval size for fixed-interval binning
    /// </summary>
    public double? IntervalSize { get; init; }

    /// <summary>
    /// Start value for fixed-interval binning range
    /// </summary>
    public double? IntervalStart { get; init; }

    /// <summary>
    /// End value for fixed-interval binning range
    /// </summary>
    public double? IntervalEnd { get; init; }

    /// <summary>
    /// Explicit boundary values for fixed-boundaries binning
    /// </summary>
    public ImmutableArray<double>? Boundaries { get; init; }

    /// <summary>
    /// Number of bins for auto-interval binning
    /// </summary>
    public int? NumBins { get; init; }

    /// <summary>
    /// Date bin definition for date-type binning (reuses DateBinDefinition)
    /// </summary>
    public DateBinDefinition? DateBin { get; init; }

    /// <summary>
    /// Optional aggregate statistics to compute per bin
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }
}

/// <summary>
/// Binning algorithm type for queryBins operations
/// </summary>
public enum BinType
{
    /// <summary>
    /// Fixed-size numeric intervals (width_bucket)
    /// </summary>
    FixedInterval,

    /// <summary>
    /// Explicit user-defined boundary values
    /// </summary>
    FixedBoundaries,

    /// <summary>
    /// Automatic equal-width intervals computed from data range
    /// </summary>
    AutoInterval,

    /// <summary>
    /// Temporal binning using date functions
    /// </summary>
    Date,

    /// <summary>
    /// Classification-based binning using GROUP BY
    /// </summary>
    Classification
}
