// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct BinDefinition
{
    public required BinType Type { get; init; }

    public required string Field { get; init; }

    public double? IntervalSize { get; init; }

    public double? IntervalStart { get; init; }

    public double? IntervalEnd { get; init; }

    public ImmutableArray<double>? Boundaries { get; init; }

    public int? NumBins { get; init; }

    public DateBinDefinition? DateBin { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }
}

public enum BinType
{
    FixedInterval,

    FixedBoundaries,

    AutoInterval,

    Date,

    Classification
}
