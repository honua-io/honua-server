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
        services.Configure<RecentErrorBufferOptions>(
            configuration.GetSection(RecentErrorBufferOptions.SectionName));
        services.AddSingleton<RecentErrorBuffer>();
        ConfigureResponseCompression(services);

        return services;
    }

    // Configure output caching for metadata endpoints
    private static void ConfigureOutputCaching(IServiceCollection services, IConfiguration configuration)
    {
        var ttl = new OutputCacheTtlOptions();
        configuration.GetSection(OutputCacheTtlOptions.SectionName).Bind(ttl);

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
                policy.Expire(ttl.ServiceMetadata);
                policy.SetVaryByRouteValue("serviceId");
                policy.SetVaryByQuery("f"); // Support for format parameter if used
                policy.Tag("service-metadata", "metadata");
            });

            // MapServer legend caching policy
            options.AddPolicy("MapServerLegend", policy =>
            {
                policy.Expire(ttl.ServiceMetadata);
                policy.SetVaryByRouteValue("serviceId");
                policy.SetVaryByQuery("f", "size", "dynamicLayers");
                policy.Tag("service-metadata", "metadata");
            });

            // MapServer tile caching policy
            options.AddPolicy("MapServerTile", policy =>
            {
                policy.Expire(ttl.ServiceMetadata);
                policy.SetVaryByRouteValue("serviceId", "z", "y", "x");
                policy.Tag("service-metadata", "tiles");
            });

            // GeoServices service directory caching policy
            options.AddPolicy("ServiceDirectory", policy =>
            {
                policy.Expire(ttl.ServiceDirectory);
                policy.SetVaryByQuery("f");
                policy.Tag("service-directory", "metadata");
            });

            // Layer metadata caching policy
            options.AddPolicy("LayerMetadata", policy =>
            {
                policy.Expire(ttl.LayerMetadata);
                policy.SetVaryByRouteValue("serviceId", "layerId");
                policy.SetVaryByQuery("f"); // Support for format parameter if used
                policy.Tag("layer-metadata", "metadata");
            });

            // OGC API Features landing page caching policy
            options.AddPolicy("OgcLandingPage", policy =>
            {
                policy.Expire(ttl.OgcLandingPage);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features conformance caching policy
            options.AddPolicy("OgcConformance", policy =>
            {
                policy.Expire(ttl.OgcConformance);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features collections list caching policy
            options.AddPolicy("OgcCollections", policy =>
            {
                policy.Expire(ttl.OgcCollections);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Features single collection caching policy
            options.AddPolicy("OgcCollection", policy =>
            {
                policy.Expire(ttl.OgcCollection);
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            options.AddPolicy("OgcOpenApi", policy =>
            {
                policy.Expire(ttl.OgcOpenApi);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // OGC API Tiles landing page caching policy
            options.AddPolicy("OgcTilesLandingPage", policy =>
            {
                policy.Expire(ttl.OgcTilesLandingPage);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles conformance caching policy
            options.AddPolicy("OgcTilesConformance", policy =>
            {
                policy.Expire(ttl.OgcTilesConformance);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Maps conformance caching policy
            options.AddPolicy("OgcMapsConformance", policy =>
            {
                policy.Expire(ttl.OgcMapsConformance);
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-maps", "metadata");
            });

            // OGC API Tiles OpenAPI caching policy
            options.AddPolicy("OgcTilesOpenApi", policy =>
            {
                policy.Expire(ttl.OgcTilesOpenApi);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collections list caching policy
            options.AddPolicy("OgcTilesCollections", policy =>
            {
                policy.Expire(ttl.OgcTilesCollections);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles single collection caching policy
            options.AddPolicy("OgcTilesCollection", policy =>
            {
                policy.Expire(ttl.OgcTilesCollection);
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tile matrix sets list caching policy
            options.AddPolicy("OgcTilesTileMatrixSets", policy =>
            {
                policy.Expire(ttl.OgcTilesTileMatrixSets);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tile matrix set caching policy
            options.AddPolicy("OgcTilesTileMatrixSet", policy =>
            {
                policy.Expire(ttl.OgcTilesTileMatrixSet);
                policy.SetVaryByRouteValue("tileMatrixSetId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles tilesets list caching policy
            options.AddPolicy("OgcTilesTilesets", policy =>
            {
                policy.Expire(ttl.OgcTilesTilesets);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles dataset tileset metadata caching policy
            options.AddPolicy("OgcTilesDatasetTileset", policy =>
            {
                policy.Expire(ttl.OgcTilesDatasetTileset);
                policy.SetVaryByRouteValue("tileMatrixSetId");
                policy.SetVaryByQuery("f", "collections");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collection tilesets list caching policy
            options.AddPolicy("OgcTilesCollectionTilesets", policy =>
            {
                policy.Expire(ttl.OgcTilesCollectionTilesets);
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles collection tileset metadata caching policy
            options.AddPolicy("OgcTilesCollectionTileset", policy =>
            {
                policy.Expire(ttl.OgcTilesCollectionTileset);
                policy.SetVaryByRouteValue("collectionId", "tileMatrixSetId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "metadata");
            });

            // OGC API Tiles dataset tile caching policy
            options.AddPolicy("OgcTilesDatasetTile", policy =>
            {
                policy.Expire(ttl.OgcTilesDatasetTile);
                policy.SetVaryByRouteValue("tileMatrixSetId", "tileMatrix", "tileRow", "tileCol");
                policy.SetVaryByQuery("f", "datetime", "subset", "crs", "subset-crs", "collections");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "tiles");
            });

            // OGC API Tiles tile caching policy
            options.AddPolicy("OgcTilesTile", policy =>
            {
                policy.Expire(ttl.OgcTilesTile);
                policy.SetVaryByRouteValue("collectionId", "tileMatrixSetId", "tileMatrix", "tileRow", "tileCol");
                policy.SetVaryByQuery("f", "datetime", "subset", "crs", "subset-crs");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-tiles", "tiles");
            });

            // MVT tile caching policy
            options.AddPolicy("MvtTile", policy =>
            {
                policy.Expire(ttl.MvtTile);
                policy.SetVaryByRouteValue("layerId", "z", "x", "y");
                policy.SetVaryByQuery("where"); // Support for WHERE clause filtering
                policy.Tag("mvt-tiles", "tiles");
            });

            // TileJSON metadata caching policy
            options.AddPolicy("TileJson", policy =>
            {
                policy.Expire(ttl.TileJson);
                policy.SetVaryByRouteValue("layerId");
                policy.Tag("mvt-tiles", "metadata");
            });

            // Layer style caching policy
            options.AddPolicy("LayerStyle", policy =>
            {
                policy.Expire(ttl.LayerStyle);
                policy.SetVaryByRouteValue("layerId");
                policy.Tag("layer-styles", "metadata");
            });

            // Image Server service metadata caching policy
            options.AddPolicy("ImageServerMetadata", policy =>
            {
                policy.Expire(ttl.ImageServerMetadata);
                policy.SetVaryByRouteValue("id");
                policy.SetVaryByQuery("f");
                policy.Tag("layer-metadata", "metadata");
            });

            // OGC API Features queryables caching policy
            options.AddPolicy("OgcQueryables", policy =>
            {
                policy.Expire(ttl.OgcQueryables);
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-metadata", "metadata");
            });

            // Note: No default base policy - endpoints must explicitly opt into caching for security
        });

        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            var cacheKeyPrefix = configuration.GetSection("Cache")["KeyPrefix"] ?? "honua:";
            services.AddStackExchangeRedisOutputCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = $"{cacheKeyPrefix}outputcache:";
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
