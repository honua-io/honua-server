// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Options;

namespace Honua.Routing.Features.Routing;

public static class ServiceCollectionExtensions
{
    public const string ConfigurationSection = RoutingConfiguration.SectionName;

    public const string ProviderConfigurationKey = "Routing:Provider";

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
        services.AddSingleton<RoutingSolveCapacityLimiter>();

        // Network-dataset registry (Phase 0 of #1882): resolves the active dataset's
        // edge/vertex table names. Backed by the honua.network_datasets registry table
        // when present, falling back to the built-in default (public.ways topology) so
        // the default deployment is unchanged. Registered unconditionally; only the
        // pgRouting provider consumes it.
        services.AddScoped<INetworkDatasetResolver, NetworkDatasetRegistry>();

        // Network-dataset editing store (#1882 editing surface): admin CRUD over the
        // honua.network_datasets registry. The registry table is Postgres-only, so the
        // store is registered only when the active data provider is Postgres (the admin
        // endpoints no-op when it is absent, mirroring the scene-dataset registry).
        if (UsesPostgresDataProvider(configuration))
        {
            services.AddScoped<INetworkDatasetStore, PostgresNetworkDatasetStore>();

            // Topology generation read/allocation and canonical batched content-edit
            // stores (#2716). Postgres-only, mirroring the registry store above; the admin
            // edit endpoints no-op when these are absent.
            services.AddScoped<INetworkTopologyGenerationStore, PostgresNetworkTopologyGenerationStore>();
            services.AddScoped<INetworkTopologyEditStore, PostgresNetworkTopologyEditStore>();

            // Durable isolated shadow-topology rebuild attempts and their multi-node
            // fencing lease (#2718/#2720), and atomic generation promotion/rollback (#2719).
            services.AddScoped<INetworkTopologyRebuildStore, PostgresNetworkTopologyRebuildStore>();
            services.AddScoped<NetworkTopologyShadowTopologyBuilder>();
            services.AddScoped<INetworkTopologyPromotionStore, PostgresNetworkTopologyPromotionStore>();
        }

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
