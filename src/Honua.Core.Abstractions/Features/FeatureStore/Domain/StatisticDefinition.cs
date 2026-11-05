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
