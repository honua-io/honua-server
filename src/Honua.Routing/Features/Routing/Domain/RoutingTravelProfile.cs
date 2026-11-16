// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed record RoutingTravelProfile(
    string Name,
    string ForwardCostColumn,
    string ReverseCostColumn)
{
    public static RoutingTravelProfile Driving { get; } = new("driving", "cost", "reverse_cost");
}
