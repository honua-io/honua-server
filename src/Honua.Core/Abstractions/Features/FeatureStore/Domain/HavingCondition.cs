// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct HavingCondition
{
    public required StatisticType StatisticType { get; init; }

    public required string OnStatisticField { get; init; }

    public required HavingComparisonOperator Operator { get; init; }

    public required double Value { get; init; }

    public string? TextValue { get; init; }

    public DateTimeOffset? TimestampValue { get; init; }

    public DateOnly? DateValue { get; init; }

    public TimeOnly? TimeValue { get; init; }

    public MetadataV2FieldType? FieldType { get; init; }

    public object BoundValue()
    {
        if (TextValue is not null)
        {
            return TextValue;
        }

        if (TimestampValue is { } timestamp)
        {
            return timestamp;
        }

        if (DateValue is { } date)
        {
            return date;
        }

        if (TimeValue is { } time)
        {
            return time;
        }

        return Value;
    }
}

public enum HavingComparisonOperator
{
    Equal,

    NotEqual,

    GreaterThan,

    GreaterThanOrEqual,

    LessThan,

    LessThanOrEqual
}
