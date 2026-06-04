// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Routing provider used when no usable routing engine is configured for the
/// active deployment — for example when the data provider is not PostgreSQL (so
/// the pgRouting SQL cannot run) and no explicit <c>Routing:Provider</c> was set.
/// It advertises no capabilities, so the NAServer adapter (which gates on
/// <see cref="IRoutingProvider.Capabilities"/>) returns a clear "not supported by
/// the configured routing provider" error instead of letting pgRouting SQL fail
/// with a 500 against an incompatible or unprovisioned database.
/// </summary>
internal sealed class UnavailableRoutingProvider : IRoutingProvider
{
    /// <summary>
    /// Provider name constant.
    /// </summary>
    public const string ProviderName = "unavailable";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public RoutingProviderCapabilities Capabilities { get; } = new(
        SupportsRoute: false,
        SupportsServiceArea: false)
    {
        SupportedTravelDirections = [],
    };

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new ServiceAreaSolveResult([]));
    }
}
