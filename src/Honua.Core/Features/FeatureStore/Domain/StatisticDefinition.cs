// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines a single aggregate statistic to compute in a statistics query
/// </summary>
public readonly record struct StatisticDefinition
{
    /// <summary>
    /// The type of aggregate function to apply
    /// </summary>
    public required StatisticType StatisticType { get; init; }

    /// <summary>
    /// The field name to compute the statistic on
    /// </summary>
    public required string OnStatisticField { get; init; }

    /// <summary>
    /// The output alias for the computed statistic in results
    /// </summary>
    public required string OutStatisticFieldName { get; init; }

    /// <summary>
    /// Optional field type hint supplied by the caller when the layer schema is
    /// known. Providers use this to emit correctly typed aggregates for
    /// JSON-backed fields without changing the public request contract.
    /// </summary>
    [JsonIgnore]
    public FieldType? FieldType { get; init; }
}

/// <summary>
/// Aggregate function types for statistics queries
/// </summary>
public enum StatisticType
{
    /// <summary>
    /// Count of non-null values
    /// </summary>
    Count,

    /// <summary>
    /// Sum of numeric values
    /// </summary>
    Sum,

    /// <summary>
    /// Minimum value
    /// </summary>
    Min,

    /// <summary>
    /// Maximum value
    /// </summary>
    Max,

    /// <summary>
    /// Arithmetic mean of numeric values
    /// </summary>
    Avg,

    /// <summary>
    /// Sample standard deviation of numeric values
    /// </summary>
    Stddev,

    /// <summary>
    /// Sample variance of numeric values
    /// </summary>
    Var
}
