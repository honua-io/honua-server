// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.FileStorage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Cog;

internal static class CogServiceCollectionExtensions
{
    public static IServiceCollection AddCogServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register the AOT-safe COG metadata reader
        services.AddSingleton<ICogMetadataReader, CogMetadataExtractor>();

        // Bind the catalog raster-source guardrails (decoded-size ceiling). The resolver
        // enforces the ceiling from its own ETag-pinned bounded header probe (#3090).
        services.Configure<CatalogRasterSourceOptions>(
            configuration.GetSection(CatalogRasterSourceOptions.SectionName));

        // Register the tile resolver
        services.AddScoped<ICogTileResolver, CogTileResolver>();

        // Register the geoprocessing raster-source resolver (#2264) so native
        // raster/surface processes can source a registered catalog raster by
        // layerId/rasterId. Singleton (the GP submit service is a singleton); it
        // opens a per-call scope to reach the scoped COG store and fails cleanly
        // when no store is configured.
        services.TryAddSingleton<IGeoprocessingRasterSourceResolver, CatalogRasterSourceResolver>();

        // Register range readers based on available provider configurations.
#if !HONUA_EXCLUDE_AWS
        services.AddAwsCloudRangeReader(configuration);
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddAzureCloudRangeReader(configuration);
#endif

        return services;
    }
}
