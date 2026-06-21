// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Core abstraction for routing engines that solve point-to-point routes and
/// service areas (isochrones). Implementations adapt to a shared, protocol-neutral
/// request/response contract so protocol surfaces (e.g. the GeoServices NAServer
/// adapter) can route without binding to a specific engine.
/// </summary>
public interface IRoutingProvider
{
    /// <summary>
    /// Unique name of the provider (e.g. "pgrouting", "mock").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Capabilities and limitations of this provider (supported solves and
    /// service-area travel directions). Lets callers introspect the active engine.
    /// </summary>
    RoutingProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Solve a multi-stop route through the requested stops in order.
    /// </summary>
    /// <param name="request">The route solve request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The solved route, or an unsolved result when no path exists.</returns>
    Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solve service areas (isochrones) around the requested facilities for the
    /// requested cost breaks.
    /// </summary>
    /// <param name="request">The service-area solve request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The service-area polygons.</returns>
    Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solve closest-facility routes: for each incident, rank the supplied
    /// facilities by network impedance and materialize the route to the closest
    /// ones. Implementations that do not support this advertise
    /// <see cref="RoutingProviderCapabilities.SupportsClosestFacility"/> as
    /// <c>false</c>; the adapter then short-circuits with a 400 before calling this.
    /// </summary>
    /// <param name="request">The closest-facility solve request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ranked closest-facility routes.</returns>
    Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solve an origins×destinations cost matrix (attribute-only impedance, no
    /// route geometry). Implementations that do not support this advertise
    /// <see cref="RoutingProviderCapabilities.SupportsOdCostMatrix"/> as <c>false</c>.
    /// </summary>
    /// <param name="request">The OD cost matrix solve request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cost matrix lines.</returns>
    Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solve a location-allocation problem: choose facilities from candidates to
    /// optimize the requested objective over weighted demand points. Implementations
    /// that do not support this advertise
    /// <see cref="RoutingProviderCapabilities.SupportsLocationAllocation"/> as
    /// <c>false</c>.
    /// </summary>
    /// <param name="request">The location-allocation solve request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chosen facilities and demand allocations.</returns>
    Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default);
}
