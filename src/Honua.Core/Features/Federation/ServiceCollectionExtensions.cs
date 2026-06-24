// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Federation;

/// <summary>
/// Service registration helpers for the federated-query planning core (issue #341).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the federation query planner. The planner is pure and stateless, so it is
    /// registered as a singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFederationCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IFederationQueryPlanner, FederationQueryPlanner>();
        return services;
    }
}
