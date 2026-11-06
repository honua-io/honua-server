// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

public readonly record struct BufferAggregateQuery
{
    public required double Distance { get; init; }

    public DistanceUnit Unit { get; init; }

    public bool Dissolve { get; init; }

    public ImmutableArray<string>? GroupByFields { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public int MaxInputFeatures { get; init; }

    public const double MinDistanceMeters = 0d;

    public const double MaxDistanceMeters = 1_000_000d;
}
