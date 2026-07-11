// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Protocol-neutral mapping from a named travel profile to the edge-table columns
/// that contain its forward and reverse impedance weights.
/// </summary>
/// <param name="Name">Stable lowercase profile name exposed to protocol adapters.</param>
/// <param name="ForwardCostColumn">Validated edge column used for source-to-target cost.</param>
/// <param name="ReverseCostColumn">Validated edge column used for target-to-source cost.</param>
public sealed record RoutingTravelProfile(
    string Name,
    string ForwardCostColumn,
    string ReverseCostColumn)
{
    /// <summary>The backward-compatible profile for standard pgRouting edge tables.</summary>
    public static RoutingTravelProfile Driving { get; } = new("driving", "cost", "reverse_cost");
}
