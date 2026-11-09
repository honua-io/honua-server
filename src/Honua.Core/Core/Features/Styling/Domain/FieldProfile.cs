// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public sealed class FieldProfile
{
    public required string FieldName { get; init; }

    public required string FieldType { get; init; }

    public long TotalCount { get; init; }

    public long NullCount { get; init; }

    public long DistinctCount { get; init; }

    public double NullPercentage => TotalCount > 0 ? (double)NullCount / TotalCount : 0d;

    public double CardinalityRatio
    {
        get
        {
            var nonNull = TotalCount - NullCount;
            return nonNull > 0 ? (double)DistinctCount / nonNull : 0d;
        }
    }

    public double? MinValue { get; init; }

    public double? MaxValue { get; init; }

    public double? MeanValue { get; init; }

    public double? StandardDeviation { get; init; }

    public IReadOnlyList<SampleValue> SampleValues { get; init; } = [];
}

public sealed record SampleValue(string Value, long Frequency);
