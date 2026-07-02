// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Dependency-injection registration for the unified capability registry
/// (ADR-0058). This is the composition seam B2 (#2334) uses to bind the
/// <c>/mcp</c> surface to the registry; B1 provides the registration without
/// wiring any surface to it (no behaviour change).
/// </summary>
public static class CapabilityRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICapabilityRegistry"/> as a singleton
    /// <see cref="CapabilityRegistry"/>. Safe to call more than once.
    /// </summary>
    /// <param name="services">The service collection to add the registry to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddCapabilityRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        return services;
    }
}
