// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct StatisticDefinition
{
    public required StatisticType StatisticType { get; init; }

    public required string OnStatisticField { get; init; }

    public required string OutStatisticFieldName { get; init; }

    [JsonIgnore]
    public MetadataV2FieldType? FieldType { get; init; }
}

public enum StatisticType
{
    Count,

    Sum,

    Min,

    Max,

    Avg,

    Stddev,

    Var
}

public static class StatisticTypeParser
{
    public static bool TryParse(string? type, out StatisticType result)
    {
        if (string.IsNullOrEmpty(type))
        {
            result = default;
            return false;
        }

        if (string.Equals(type, "count", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Count;
            return true;
        }

        if (string.Equals(type, "sum", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Sum;
            return true;
        }

        if (string.Equals(type, "min", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Min;
            return true;
        }

        if (string.Equals(type, "max", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Max;
            return true;
        }

        if (string.Equals(type, "avg", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Avg;
            return true;
        }

        if (string.Equals(type, "stddev", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Stddev;
            return true;
        }

        if (string.Equals(type, "var", StringComparison.OrdinalIgnoreCase))
        {
            result = StatisticType.Var;
            return true;
        }

        result = default;
        return false;
    }
}
