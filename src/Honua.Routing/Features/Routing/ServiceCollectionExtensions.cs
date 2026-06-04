// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Options;

namespace Honua.Routing.Features.Routing;

/// <summary>
/// Service collection extensions for the routing subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section name for routing options.
    /// </summary>
    public const string ConfigurationSection = RoutingConfiguration.SectionName;

    /// <summary>
    /// Configuration key selecting the routing provider (e.g. "pgrouting", "mock").
    /// </summary>
    public const string ProviderConfigurationKey = "Routing:Provider";

    /// <summary>
    /// Registers the routing subsystem: binds and validates
    /// <see cref="RoutingConfiguration"/> from the <c>Routing</c> section and
    /// registers the routing provider selected by <c>Routing:Provider</c>.
    /// Defaults to the pgRouting provider; set <c>Routing:Provider=mock</c> to use
    /// the database-free mock provider.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RoutingConfiguration>()
            .Bind(configuration.GetSection(RoutingConfiguration.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoutingConfiguration>, RoutingConfigurationValidator>();

        // Resolve the selected provider. The Routing:Provider key remains the
        // authoritative selector; the bound RoutingConfiguration.Provider mirrors it.
        var providerName = configuration[ProviderConfigurationKey];

        if (string.Equals(providerName, MockRoutingProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IRoutingProvider, MockRoutingProvider>();
        }
        else
        {
            // Default: pgRouting-backed provider over the shared connection substrate.
            services.AddScoped<IRoutingProvider, PgRoutingProvider>();
        }

        return services;
    }
}
