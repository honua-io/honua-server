// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

public interface IRoutingProvider
{
    string Name { get; }

    RoutingProviderCapabilities Capabilities { get; }

    Task<RoutingProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Capabilities);

    Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default);

    Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default);

    Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default);

    Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default);

    Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default);
}
