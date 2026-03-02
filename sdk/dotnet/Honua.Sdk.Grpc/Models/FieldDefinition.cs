// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// Describes a field in the layer schema.
/// </summary>
public sealed class FieldDefinition
{
    /// <summary>Field name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Field data type.</summary>
    public FieldType FieldType { get; init; }

    /// <summary>Maximum length for string fields.</summary>
    public int Length { get; init; }

    /// <summary>Whether the field is nullable.</summary>
    public bool Nullable { get; init; }
}

/// <summary>
/// Specifies an aggregate statistic to compute.
/// </summary>
public sealed class StatisticDefinition
{
    /// <summary>Field to compute the statistic on.</summary>
    public string OnStatisticField { get; init; } = string.Empty;

    /// <summary>Type of statistic.</summary>
    public StatisticType StatisticType { get; init; }

    /// <summary>Output field name for the result.</summary>
    public string OutStatisticFieldName { get; init; } = string.Empty;
}
