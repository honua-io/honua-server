// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Federation;

/// <summary>
/// Service registration helpers for the federated-query planning and execution core (issue #341).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the federation query planner and the remote-execution layer (executor). The
    /// planner is pure and stateless; the executor is a singleton because it caches per-source
    /// circuit-breaker state across calls. Transport connectors
    /// (<see cref="IFederatedSourceConnector"/>) are registered separately by the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFederationCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IFederationQueryPlanner, FederationQueryPlanner>();
        services.TryAddSingleton<FederationMetrics>();
        services.TryAddSingleton<IFederatedQueryExecutor>(static sp => new FederatedQueryExecutor(
            sp.GetRequiredService<IFederationQueryPlanner>(),
            sp.GetServices<IFederatedSourceConnector>(),
            sp.GetService<ResiliencePolicyOptions>() ?? ResiliencePolicyOptions.Default,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FederatedQueryExecutor>>(),
            sp.GetRequiredService<FederationMetrics>()));
        return services;
    }
}
