// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

internal sealed class UnavailableRoutingProvider : IRoutingProvider
{
    public const string ProviderName = "unavailable";

    public string Name => ProviderName;

    public RoutingProviderCapabilities Capabilities { get; } = new(
        SupportsRoute: false,
        SupportsServiceArea: false)
    {
        SupportedTravelDirections = [],
    };

    public Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Unreachable in practice: the adapter short-circuits on Capabilities
        // before solving. Return an unsolved result rather than throwing so any
        // direct caller degrades gracefully.
        return Task.FromResult(new RouteSolveResult(string.Empty, 0, 0, []));
    }

    public Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new ServiceAreaSolveResult([]));
    }

    public Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new ClosestFacilitySolveResult([]));
    }

    public Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new OdCostMatrixSolveResult([]));
    }

    public Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new LocationAllocationSolveResult([], [], 0, 0));
    }
}
