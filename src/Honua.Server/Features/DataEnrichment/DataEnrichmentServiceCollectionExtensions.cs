// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.DataEnrichment.Domain;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Service collection extensions for the data-enrichment slice (#374). Binds the
/// operator-curated enrichment catalog from configuration and registers the
/// lookup wrapper consumed by the enrichment endpoints. The underlying spatial
/// join is executed by the shared <see cref="Core.Features.SpatialAnalytics.Abstractions.ISpatialAnalyticsReader"/>
/// (registered by the Postgres feature-store wiring), so this slice owns no data
/// access of its own.
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

        return services;
    }
}
