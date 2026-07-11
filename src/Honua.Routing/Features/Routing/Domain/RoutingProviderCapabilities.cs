// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Declares what a routing provider supports. Mirrors the geocoding provider's
/// capability surface so callers (and the NAServer adapter) can introspect the
/// active engine without binding to a concrete provider type.
/// </summary>
/// <param name="SupportsRoute">
/// Whether the provider can solve multi-stop point-to-point routes.
/// </param>
/// <param name="SupportsServiceArea">
/// Whether the provider can solve service-area (isochrone) polygons.
/// </param>
/// <param name="SupportsClosestFacility">
/// Whether the provider can solve closest-facility routes (rank facilities by
/// network impedance per incident).
/// </param>
/// <param name="SupportsOdCostMatrix">
/// Whether the provider can solve an origins×destinations cost matrix.
/// </param>
/// <param name="SupportsLocationAllocation">
/// Whether the provider can solve location-allocation problems over a cost matrix.
/// </param>
public sealed record RoutingProviderCapabilities(
    bool SupportsRoute = true,
    bool SupportsServiceArea = true,
    bool SupportsClosestFacility = false,
    bool SupportsOdCostMatrix = false,
    bool SupportsLocationAllocation = false)
{
    /// <summary>
    /// Whether OD cost-matrix solves can materialize straight-line geometry in the
    /// requested output SRID. Providers that only compute impedance cells leave
    /// this false so adapters reject geometry requests instead of fabricating them.
    /// </summary>
    public bool SupportsOdStraightLines { get; init; }

    /// <summary>
    /// Service-area travel directions the provider honors. An empty set means the
    /// provider does not differentiate travel direction.
    /// </summary>
    public IReadOnlyList<ServiceAreaTravelDirection> SupportedTravelDirections { get; init; } =
    [
        ServiceAreaTravelDirection.FromFacility,
        ServiceAreaTravelDirection.ToFacility,
    ];

    /// <summary>
    /// Barrier kinds the provider honours by excluding the graph edges each
    /// barrier restricts. An empty set means barriers are not supported; the
    /// NAServer adapter then rejects any barrier-bearing request with a 400 rather
    /// than silently ignoring the barrier and returning an unrestricted solve.
    /// </summary>
    public IReadOnlyList<RouteBarrierKind> SupportedBarrierKinds { get; init; } = [];

    /// <summary>
    /// Whether the provider honours any barrier kind. Derived from
    /// <see cref="SupportedBarrierKinds"/>.
    /// </summary>
    public bool SupportsBarriers => SupportedBarrierKinds.Count > 0;

    /// <summary>
    /// Named travel modes the provider can route, compared case-insensitively. An
    /// empty set means the provider does not differentiate modes; the adapter then
    /// accepts only an absent/empty <c>travelMode</c> and routes on the topology's
    /// stored cost weights. When non-empty, the adapter rejects any
    /// <c>travelMode</c> not in this set with a 400.
    /// </summary>
    public IReadOnlyList<string> SupportedTravelModes { get; init; } = [];

    /// <summary>
    /// Whether the provider differentiates multiple named travel modes. Derived
    /// from <see cref="SupportedTravelModes"/>.
    /// </summary>
    public bool SupportsTravelModes => SupportedTravelModes.Count > 0;

    /// <summary>
    /// Location-allocation problem types the provider can solve. Empty when
    /// <see cref="SupportsLocationAllocation"/> is <c>false</c>. The NAServer adapter
    /// rejects any requested problem type not in this set with a 400 rather than
    /// silently substituting a different objective.
    /// </summary>
    public IReadOnlyList<LocationAllocationProblemType> SupportedLocationAllocationProblemTypes { get; init; } = [];
}
