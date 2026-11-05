// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

// Containment names both operands, because a bare contains has meant opposite directions on different surfaces.
public enum SpatialJoinPredicate
{
    Intersects,

    JoinContainsTarget,

    TargetContainsJoin,

    DWithin,
}

public readonly record struct SpatialJoinQuery
{
    public required int JoinLayerId { get; init; }

    public int? JoinLayerSrid { get; init; }

    public required SpatialJoinPredicate Predicate { get; init; }

    public double? DistanceMeters { get; init; }

    public ImmutableArray<string>? CarryFields { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public int MaxInputFeatures { get; init; }
}
