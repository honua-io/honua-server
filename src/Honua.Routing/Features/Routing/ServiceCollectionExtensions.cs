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
    /// </summary>
    /// <remarks>
    /// Provider selection:
    /// <list type="bullet">
    /// <item><c>Routing:Provider=mock</c> — the database-free mock provider.</item>
    /// <item><c>Routing:Provider=pgrouting</c> — the pgRouting provider, regardless
    /// of the data provider (operator opt-in).</item>
    /// <item><c>Routing:Provider=none</c>/<c>unavailable</c> — no routing engine.</item>
    /// <item>Unset (default) — pgRouting <em>only</em> when the active
    /// <c>DataSource:Provider</c> is PostgreSQL (or unset, since Postgres is the
    /// default). On non-Postgres data providers (DuckDB/MySQL/etc.) the pgRouting
    /// SQL cannot run, so an <see cref="UnavailableRoutingProvider"/> is registered
    /// instead; the NAServer adapter then returns a clear "not supported" error
    /// rather than a 500 from executing pgRouting SQL against an incompatible
    /// database.</item>
    /// </list>
    /// </remarks>
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
        else if (string.Equals(providerName, UnavailableRoutingProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "none", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IRoutingProvider, UnavailableRoutingProvider>();
        }
        else if (string.Equals(providerName, PgRoutingProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            // Explicit pgRouting opt-in — honored regardless of the data provider.
            services.AddScoped<IRoutingProvider, PgRoutingProvider>();
        }
        else if (UsesPostgresDataProvider(configuration))
        {
            // Default on Postgres deployments: pgRouting over the shared connection
            // substrate (its SQL targets PostGIS + pgRouting in the same database).
            services.AddScoped<IRoutingProvider, PgRoutingProvider>();
        }
        else
        {
            // Default on non-Postgres data providers: pgRouting SQL cannot run, so
            // advertise no routing capability and let the adapter respond clearly.
            services.AddScoped<IRoutingProvider, UnavailableRoutingProvider>();
        }

        return services;
    }

    private static bool UsesPostgresDataProvider(IConfiguration configuration)
    {
        // Mirrors the Postgres-only gating used elsewhere (e.g. AddSceneGeneration):
        // an unset provider defaults to Postgres/PostGIS.
        var provider = configuration.GetValue<string>("DataSource:Provider");
        return string.IsNullOrWhiteSpace(provider)
            || provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgis", StringComparison.OrdinalIgnoreCase);
    }
}
