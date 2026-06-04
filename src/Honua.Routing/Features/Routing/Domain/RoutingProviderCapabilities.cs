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
public sealed record RoutingProviderCapabilities(
    bool SupportsRoute = true,
    bool SupportsServiceArea = true)
{
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
    /// Whether point/line barriers are honored. MVP providers do not yet support
    /// barriers; the request model accepts them but they are ignored.
    /// </summary>
    public bool SupportsBarriers { get; init; }

    /// <summary>
    /// Whether multiple named travel modes are honored. MVP providers route on the
    /// topology's stored cost weights regardless of mode.
    /// </summary>
    public bool SupportsTravelModes { get; init; }
}
