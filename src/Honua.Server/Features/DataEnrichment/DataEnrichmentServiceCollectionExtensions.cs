// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.DataEnrichment.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.EnrichmentCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Service collection extensions for the data-enrichment slice (#374; #2280/#2282).
/// Binds the operator-curated configuration catalog, registers the managed
/// Postgres-backed enrichment-dataset registry store (when the active provider is
/// Postgres), and the composing <see cref="EnrichmentDatasetCatalogService"/>
/// consumed by the discovery and compute endpoints. The underlying spatial join is
/// executed by the shared <see cref="Core.Features.SpatialAnalytics.Abstractions.ISpatialAnalyticsReader"/>
/// (registered by the Postgres feature-store wiring), so this slice owns no data
/// access of its own beyond the catalog registry.
/// </summary>
internal static class DataEnrichmentServiceCollectionExtensions
{
    public static IServiceCollection AddDataEnrichment(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<EnrichmentCatalogOptions>(
            configuration.GetSection(EnrichmentCatalogOptions.SectionName));
        services.AddSingleton<EnrichmentCatalog>();

        // Managed registry store (Postgres-only; no-op on other providers, #2280).
        services.AddPostgresEnrichmentDatasetCatalog(configuration);

        // Composing catalog service. Registered via a factory so the registry store,
        // cache, and schema context are all optional (the service degrades to the
        // configuration catalog when they are absent).
        services.AddScoped(sp => new EnrichmentDatasetCatalogService(
            sp.GetService<IEnrichmentDatasetCatalogStore>(),
            sp.GetRequiredService<EnrichmentCatalog>(),
            sp.GetService<ICacheService>(),
            sp.GetService<ISchemaContext>()));

        // Neutral resolver seam (#2283) consumed by the enrichment.enrich
        // geoprocessing job executor, so the async batch path resolves datasets
        // through the same merged catalog as POST /api/enrich.
        services.AddScoped<IEnrichmentDatasetResolver, EnrichmentDatasetResolver>();

        return services;
    }
}
