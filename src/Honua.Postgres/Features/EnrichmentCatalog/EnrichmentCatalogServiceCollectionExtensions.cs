// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Postgres.Features.EnrichmentCatalog;

/// <summary>
/// DI registration helper for the Postgres-backed enrichment-dataset catalog store
/// (#2280). Mirrors the network-dataset / scene-registry gating: the registry table
/// is Postgres-only, so on non-Postgres data-source profiles this is a no-op and the
/// enrichment admin endpoints decline to map themselves.
/// </summary>
public static class EnrichmentCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEnrichmentDatasetCatalogStore"/> when the active data
    /// provider is Postgres.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (for provider gating).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPostgresEnrichmentDatasetCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!UsesPostgresProvider(configuration))
        {
            return services;
        }

        services.AddScoped<IEnrichmentDatasetCatalogStore, PostgresEnrichmentDatasetCatalogStore>();
        return services;
    }

    private static bool UsesPostgresProvider(IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("DataSource:Provider");

        return string.IsNullOrWhiteSpace(provider)
            || provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("postgis", StringComparison.OrdinalIgnoreCase);
    }
}
