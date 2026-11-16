// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed class RoutingConfiguration
{
    public const string SectionName = "Routing";

    public string Provider { get; set; } = "pgrouting";

    public int MaxStops { get; set; } = 1000;

    public int MaxFacilities { get; set; } = 1000;

    public int MaxBreaks { get; set; } = 50;

    public int MaxBarriers { get; set; } = 1000;

    public int MaxIncidents { get; set; } = 1000;

    public int MaxClosestFacilities { get; set; } = 1000;

    public int MaxOrigins { get; set; } = 1000;

    public int MaxDestinations { get; set; } = 1000;

    public int MaxLocationAllocationFacilities { get; set; } = 1000;

    public int MaxDemandPoints { get; set; } = 1000;

    public int MaxSolvePairs { get; set; } = 10_000;

    public string NetworkDatasetId { get; set; } = "default";

    public RoutingCostUnit CostUnit { get; set; } = RoutingCostUnit.Minutes;
}
