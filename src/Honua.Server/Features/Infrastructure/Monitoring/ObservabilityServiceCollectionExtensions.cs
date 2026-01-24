// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        ConfigureOutputCaching(services, configuration);
        services.AddSingleton<OutputCacheInvalidationService>();
        services.AddETags();
        services.TryAddSingleton<ISystemMetricsCollector, SystemMetricsCollector>();
        services.AddPerformanceMonitoring();
        ConfigureResponseCompression(services);

        return services;
    }

    // Configure output caching for metadata endpoints
    private static void ConfigureOutputCaching(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOutputCache(options =>
        {
            // Add dynamic tags and restrict caching to anonymous requests only.
            options.AddBasePolicy(policy =>
            {
                policy.AddPolicy<RouteTagOutputCachePolicy>();
                policy.AddPolicy<AnonymousOnlyOutputCachePolicy>();
            });

            // Service metadata caching policy
            options.AddPolicy("ServiceMetadata", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(5));
                policy.SetVaryByRouteValue("serviceId");
                policy.SetVaryByQuery("f"); // Support for format parameter if used
                policy.Tag("service-metadata", "metadata");
            });

            // Layer metadata caching policy
            options.AddPolicy("LayerMetadata", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(5));
                policy.SetVaryByRouteValue("serviceId", "layerId");
                policy.SetVaryByQuery("f"); // Support for format parameter if used
                policy.Tag("layer-metadata", "metadata");
            });

            // OGC API Features landing page caching policy
            options.AddPolicy("OgcLandingPage", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(30));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features conformance caching policy
            options.AddPolicy("OgcConformance", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features collections list caching policy
            options.AddPolicy("OgcCollections", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features single collection caching policy
            options.AddPolicy("OgcCollection", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            options.AddPolicy("OgcOpenApi", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Tiles landing page caching policy
            options.AddPolicy("OgcTilesLandingPage", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(30));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles conformance caching policy
            options.AddPolicy("OgcTilesConformance", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles OpenAPI caching policy
            options.AddPolicy("OgcTilesOpenApi", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collections list caching policy
            options.AddPolicy("OgcTilesCollections", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles single collection caching policy
            options.AddPolicy("OgcTilesCollection", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tile matrix sets list caching policy
            options.AddPolicy("OgcTilesTileMatrixSets", policy =>
            {
                policy.Expire(TimeSpan.FromHours(12));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tile matrix set caching policy
            options.AddPolicy("OgcTilesTileMatrixSet", policy =>
            {
                policy.Expire(TimeSpan.FromHours(12));
                policy.SetVaryByRouteValue("tileMatrixSetId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tilesets list caching policy
            options.AddPolicy("OgcTilesTilesets", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles dataset tileset metadata caching policy
            options.AddPolicy("OgcTilesDatasetTileset", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("tileMatrixSetId");
                policy.SetVaryByQuery("f", "collections");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collection tilesets list caching policy
            options.AddPolicy("OgcTilesCollectionTilesets", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collection tileset metadata caching policy
            options.AddPolicy("OgcTilesCollectionTileset", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("collectionId", "tileMatrixSetId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles dataset tile caching policy
            options.AddPolicy("OgcTilesDatasetTile", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByRouteValue("tileMatrixSetId", "tileMatrix", "tileRow", "tileCol");
                policy.SetVaryByQuery("f", "datetime", "subset", "crs", "subset-crs", "collections");
                policy.Tag("ogc-tiles", "tiles");
            });

            // OGC API Tiles tile caching policy
            options.AddPolicy("OgcTilesTile", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1));
                policy.SetVaryByRouteValue("collectionId", "tileMatrixSetId", "tileMatrix", "tileRow", "tileCol");
                policy.SetVaryByQuery("f", "datetime", "subset", "crs", "subset-crs");
                policy.Tag("ogc-tiles", "tiles");
            });

            // MVT tile caching policy
            options.AddPolicy("MvtTile", policy =>
            {
                policy.Expire(TimeSpan.FromHours(1)); // Cache tiles for 1 hour by default
                policy.SetVaryByRouteValue("layerId", "z", "x", "y");
                policy.SetVaryByQuery("where"); // Support for WHERE clause filtering
                policy.Tag("mvt-tiles", "tiles");
            });

            // TileJSON metadata caching policy
            options.AddPolicy("TileJson", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("layerId");
                policy.Tag("mvt-tiles", "metadata");
            });

            // Layer style caching policy
            options.AddPolicy("LayerStyle", policy =>
            {
                policy.Expire(TimeSpan.FromMinutes(10));
                policy.SetVaryByRouteValue("layerId");
                policy.Tag("layer-styles", "metadata");
            });

            // Note: No default base policy - endpoints must explicitly opt into caching for security
        });

        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddStackExchangeRedisOutputCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "honua:outputcache:";
            });
        }
    }

    // Configure response compression for GeoJSON and JSON responses
    private static void ConfigureResponseCompression(IServiceCollection services)
    {
        // MIME types for geospatial data formats
        string[] additionalMimeTypes =
        [
            "application/geo+json",    // GeoJSON format
            "application/json"         // Standard JSON responses
        ];

        services.AddResponseCompression(options =>
        {
            // Enable compression for HTTPS requests
            options.EnableForHttps = true;

            // Configure compression providers
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();

            // Add MIME types for geospatial data formats
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(additionalMimeTypes);
        });

        // Configure Brotli compression for fastest performance (low latency)
        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        // Configure Gzip compression for fastest performance (fallback)
        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });
    }
}
