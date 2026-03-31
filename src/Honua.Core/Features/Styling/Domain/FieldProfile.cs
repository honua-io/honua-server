// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Statistical profile of a single layer attribute field.
/// </summary>
public sealed class FieldProfile
{
    /// <summary>
    /// Name of the profiled field.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Data type of the field.
    /// </summary>
    public required string FieldType { get; init; }

    /// <summary>
    /// Total number of features in the layer.
    /// </summary>
    public long TotalCount { get; init; }

    /// <summary>
    /// Number of features with a null value for this field.
    /// </summary>
    public long NullCount { get; init; }

    /// <summary>
    /// Number of distinct non-null values.
    /// </summary>
    public long DistinctCount { get; init; }

    /// <summary>
    /// Percentage of null values (0.0 to 1.0).
    /// </summary>
    public double NullPercentage => TotalCount > 0 ? (double)NullCount / TotalCount : 0d;

    /// <summary>
    /// Ratio of distinct values to total non-null values (0.0 to 1.0).
    /// </summary>
    public double CardinalityRatio
    {
        get
        {
            var nonNull = TotalCount - NullCount;
            return nonNull > 0 ? (double)DistinctCount / nonNull : 0d;
        }
    }

    /// <summary>
    /// Minimum numeric value (null for non-numeric fields).
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum numeric value (null for non-numeric fields).
    /// </summary>
    public double? MaxValue { get; init; }

    /// <summary>
    /// Mean numeric value (null for non-numeric fields).
    /// </summary>
    public double? MeanValue { get; init; }

    /// <summary>
    /// Standard deviation of numeric values (null for non-numeric fields).
    /// </summary>
    public double? StandardDeviation { get; init; }

    /// <summary>
    /// Top distinct values by frequency, capped at 20.
    /// </summary>
    public IReadOnlyList<SampleValue> SampleValues { get; init; } = [];
}

/// <summary>
/// A sampled distinct value and its frequency.
/// </summary>
/// <param name="Value">The distinct value as a string.</param>
/// <param name="Frequency">Number of occurrences.</param>
public sealed record SampleValue(string Value, long Frequency);
