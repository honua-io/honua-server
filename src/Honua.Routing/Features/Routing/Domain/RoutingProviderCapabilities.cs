// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed record RoutingProviderCapabilities(
    bool SupportsRoute = true,
    bool SupportsServiceArea = true,
    bool SupportsClosestFacility = false,
    bool SupportsOdCostMatrix = false,
    bool SupportsLocationAllocation = false)
{
    public bool SupportsOdStraightLines { get; init; }

    public IReadOnlyList<ServiceAreaTravelDirection> SupportedTravelDirections { get; init; } =
    [
        ServiceAreaTravelDirection.FromFacility,
        ServiceAreaTravelDirection.ToFacility,
    ];

    public IReadOnlyList<RouteBarrierKind> SupportedBarrierKinds { get; init; } = [];

    public bool SupportsBarriers => SupportedBarrierKinds.Count > 0;

    public IReadOnlyList<string> SupportedTravelModes { get; init; } = [];

    public bool SupportsTravelModes => SupportedTravelModes.Count > 0;

    public IReadOnlyList<LocationAllocationProblemType> SupportedLocationAllocationProblemTypes { get; init; } = [];
}
