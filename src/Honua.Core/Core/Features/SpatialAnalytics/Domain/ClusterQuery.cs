// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.SpatialAnalytics.Domain;

public enum ClusterAlgorithm
{
    DbScan,

    KMeans,
}

public readonly record struct ClusterQuery
{
    public required ClusterAlgorithm Algorithm { get; init; }

    public double? Eps { get; init; }

    public int? MinPoints { get; init; }

    public int? K { get; init; }

    public DistanceUnit DistanceUnit { get; init; }

    public bool ReturnHullPerCluster { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public int MaxInputFeatures { get; init; }

    public int MaxClusters { get; init; }

    public const double MinEps = 0d;

    public const int MinMinPoints = 1;

    public const int MinK = 1;

    public const int MaxK = 10_000;
}
