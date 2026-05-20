// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Import;

/// <summary>
/// Service registration helpers for import schema analysis.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the import schema suggestion service and supporting services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddImportSuggestionsCore(this IServiceCollection services)
    {
        services.TryAddScoped<IFileFormatDetectionService, FileFormatDetectionService>();
        services.TryAddSingleton<IImportSchemaSuggestionService, ImportSchemaSuggestionService>();
        services.AddResilientHttpClient<OgcApiFeaturesMigrationScanner>(
            "ogc-api-features-migration",
            HttpResiliencePolicies.SlowServiceDefaults,
            configureClient: client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "HonuaServer/1.0");
                client.Timeout = TimeSpan.FromMinutes(2);
            },
            configureHandler: static () => OgcApiFeaturesMigrationScanner.CreatePinnedDnsHttpMessageHandler());
        services.TryAddScoped<IOgcApiFeaturesMigrationScanner>(serviceProvider =>
            serviceProvider.GetRequiredService<OgcApiFeaturesMigrationScanner>());

        // Default in-memory evidence store for ArcGIS migration runs. Postgres composition
        // root replaces this with a durable implementation; tests rely on this default to
        // exercise the admin endpoints without a database (#1025 slice 6).
        services.TryAddSingleton<IArcGisMigrationEvidenceStore, InMemoryArcGisMigrationEvidenceStore>();

        return services;
    }
}
