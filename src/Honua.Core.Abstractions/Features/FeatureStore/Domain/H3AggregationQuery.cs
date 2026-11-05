// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct H3AggregationQuery
{
    public required int Resolution { get; init; }

    public byte[]? PolyfillGeometry { get; init; }

    public int? PolyfillSrid { get; init; }

    public int? KRingDistance { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public ImmutableArray<SpatialAggregationSummaryDefinition>? SummaryDefinitions { get; init; }

    public int? MaxCells { get; init; }

    public const int MinResolution = 0;

    public const int MaxResolution = 15;

    public const int MaxKRingDistance = 20;

    public const string CapabilityErrorTitle = "H3 operations are not available";

    public const string CapabilityErrorDetail =
        "The h3-pg PostgreSQL extension is not installed. Contact your database administrator to install it: https://github.com/zachasme/h3-pg";

    public const string CapabilityCheckFailedDetail =
        "Unable to determine h3-pg extension availability. The database may be temporarily unreachable. Please retry the request.";
}
