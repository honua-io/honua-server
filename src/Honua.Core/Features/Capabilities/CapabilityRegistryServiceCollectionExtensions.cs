// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// Binds <see cref="CapabilityFlagOptions"/> from the
    /// <c>Capabilities:Experimental</c> configuration section (#2339 / T2). The global
    /// master switch and the per-capability overrides are siblings under one section,
    /// so binding is done explicitly via <see cref="CapabilityFlagOptions.Bind"/>
    /// rather than the default binder. Safe to call more than once.
    /// </summary>
    /// <param name="services">The service collection to add the options to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddCapabilityFlagOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CapabilityFlagOptions>()
            .Configure(options =>
                CapabilityFlagOptions.Bind(options, configuration.GetSection(CapabilityFlagOptions.SectionName)));
        return services;
    }
}
