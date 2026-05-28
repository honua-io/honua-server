// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Compression;
using Honua.Server.Features.Infrastructure.Models;
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
        services.TryAddSingleton<ConnectionPoolMetrics>();
        services.AddSingleton<ProductionMetricsCollector>();
        services.Configure<RecentErrorBufferOptions>(
            configuration.GetSection(RecentErrorBufferOptions.SectionName));
        services.AddSingleton<RecentErrorBuffer>();

        // Audit-A1 / ADR-0044: RecentErrorBuffer lives in the Server-side
        // Monitoring sub-area (not yet carved into Honua.Hosting). The
        // hosting-layer error formatter records errors through this delegate
        // so the formatter does not take a compile-time reference back to
        // Server. The delegate resolves the buffer per-request via
        // HttpContext.RequestServices to preserve the original lifetime.
        StandardErrorResponseFormatter.RecentErrorBufferRecordOverride = (context, errorResponse) =>
        {
            var buffer = context.RequestServices?.GetService<RecentErrorBuffer>();
            buffer?.Record(context, errorResponse);
        };
        ConfigureResponseCompression(services);

        AddOperateObservability(services);

        return services;
    }

    /// <summary>
    /// Register the Console Operate observability feed (#1168) and its
    /// background OTLP probe. The probe stays opt-in: when no OTLP endpoint
    /// is configured it reports <c>disabled</c> without generating outbound
    /// traffic, so air-gapped deployments are unaffected.
    /// </summary>
    private static void AddOperateObservability(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(BackgroundOtlpProbe.HttpClientName);
        services.AddSingleton<BackgroundOtlpProbe>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundOtlpProbe>());

        services.AddScoped<IOperateEventFeed, LocalOperateEventFeed>();
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

            // Content publication public route reads (#1183). The anonymous-only base
            // policy applies; ContentPublishedRouteCachePolicy disables caching for
            // link/embed reads and tags responses for invalidation on route pointer or
            // policy changes.
            options.AddPolicy("ContentPublishedRoute", policy =>
            {
                policy.Expire(ttl.LayerMetadata);
                policy.SetVaryByRouteValue("routeSlug");
                policy.SetVaryByQuery("version", "expand");
                policy.SetVaryByHeader("Accept");
                policy.AddPolicy<ContentPublishedRouteCachePolicy>();
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
            options.AddPolicy("OgcMapsLandingPage", policy =>
            {
                policy.Expire(ttl.OgcMapsLandingPage);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-maps", "metadata");
            });

            options.AddPolicy("OgcMapsConformance", policy =>
            {
                policy.Expire(ttl.OgcMapsConformance);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-maps", "metadata");
            });

            options.AddPolicy("OgcMapsOpenApi", policy =>
            {
                policy.Expire(ttl.OgcMapsOpenApi);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-maps", "metadata");
            });

            options.AddPolicy("OgcCoveragesLandingPage", policy =>
            {
                policy.Expire(ttl.OgcCoveragesLandingPage);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-coverages", "metadata");
            });

            options.AddPolicy("OgcCoveragesConformance", policy =>
            {
                policy.Expire(ttl.OgcCoveragesConformance);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-coverages", "metadata");
            });

            options.AddPolicy("OgcCoveragesOpenApi", policy =>
            {
                policy.Expire(ttl.OgcCoveragesOpenApi);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-coverages", "metadata");
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
                // `where` for attribute filtering; `time` for the temporal-animation
                // contract (#379) so /tiles/...mvt?time=A,B does not collide with the
                // unfiltered or other-range cache entries.
                policy.SetVaryByQuery("where", "time");
                policy.Tag("mvt-tiles", "tiles");
            });

            // H3 MVT tile caching policy — same TTL as standard tiles but also
            // varies by resolution since different resolutions produce different cells
            options.AddPolicy("H3MvtTile", policy =>
            {
                policy.Expire(ttl.MvtTile);
                policy.SetVaryByRouteValue("layerId", "z", "x", "y");
                policy.SetVaryByQuery("where", "resolution");
                policy.Tag("mvt-tiles", "tiles", "h3");
            });

            // TileJSON metadata caching policy
            options.AddPolicy("TileJson", policy =>
            {
                policy.Expire(ttl.TileJson);
                policy.SetVaryByRouteValue("layerId");
                policy.Tag("mvt-tiles", "metadata");
            });

            // Terrain TileJSON-compatible metadata caching policy
            options.AddPolicy("TerrainMetadata", policy =>
            {
                policy.Expire(ttl.TerrainMetadata);
                policy.SetVaryByRouteValue("datasetId");
                policy.SetVaryByHeader("Accept");
                policy.Tag("terrain", "metadata");
            });

            // Terrain-RGB finite grid tile caching policy
            options.AddPolicy("TerrainTile", policy =>
            {
                policy.Expire(ttl.TerrainTile);
                policy.SetVaryByRouteValue("datasetId", "z", "x", "y");
                policy.Tag("terrain", "tiles");
            });

            // Hosted 3D Tiles tileset.json metadata caching policy.
            // Range requests bypass output cache so the static-file pipeline
            // can return 206 Partial Content directly. Responses marked
            // Cache-Control: no-store (per-scene cachePolicy.noStore) are
            // suppressed at storage time so the cache cannot outlive the
            // dataset's no-store directive. Requests carrying a scene
            // access envelope token (?token= or X-Honua-Token) also bypass
            // the cache: the token authorizes a specific principal for a
            // short window, and caching by URL alone would either store
            // unique entries per token or replay an authorized payload to
            // a later anonymous client.
            options.AddPolicy("SceneTilesetMetadata", policy =>
            {
                policy.Expire(ttl.SceneTilesetMetadata);
                policy.SetVaryByRouteValue("sceneId");
                policy.AddPolicy<BypassOutputCacheOnRangeRequestPolicy>();
                policy.AddPolicy<BypassOutputCacheOnNoStoreResponsePolicy>();
                policy.AddPolicy<BypassOutputCacheOnSceneAccessTokenPolicy>();
                policy.Tag("scene", "metadata");
            });

            // Hosted 3D Tiles tile/binary/texture asset caching policy.
            // Range requests bypass output cache so the static-file pipeline
            // can return 206 Partial Content directly. Per-scene no-store
            // responses are suppressed at storage time, mirroring the
            // metadata policy. Requests carrying a scene access envelope
            // token (?token= or X-Honua-Token) also bypass the cache.
            options.AddPolicy("SceneTileAsset", policy =>
            {
                policy.Expire(ttl.SceneTileAsset);
                policy.SetVaryByRouteValue("sceneId", "assetPath");
                policy.AddPolicy<BypassOutputCacheOnRangeRequestPolicy>();
                policy.AddPolicy<BypassOutputCacheOnNoStoreResponsePolicy>();
                policy.AddPolicy<BypassOutputCacheOnSceneAccessTokenPolicy>();
                policy.Tag("scene", "tiles");
            });

            // Layer style caching policy.  Theme variants share the cache tag so
            // a layer style invalidation flushes both canonical and themed entries.
            options.AddPolicy("LayerStyle", policy =>
            {
                policy.Expire(ttl.LayerStyle);
                policy.SetVaryByRouteValue("layerId");
                policy.SetVaryByQuery("theme");
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

            // STAC catalog (root landing page) caching policy
            options.AddPolicy("StacCatalog", policy =>
            {
                policy.Expire(ttl.StacCatalog);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("stac-metadata", "metadata");
            });

            // STAC collections list caching policy
            options.AddPolicy("StacCollections", policy =>
            {
                policy.Expire(ttl.StacCollections);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("stac-metadata", "metadata");
            });

            // STAC single collection caching policy
            options.AddPolicy("StacCollection", policy =>
            {
                policy.Expire(ttl.StacCollection);
                policy.SetVaryByRouteValue("collectionId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("stac-metadata", "metadata");
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
            "application/geo+json"     // GeoJSON format
        ];

        services.AddResponseCompression(options =>
        {
            // Enable compression for HTTPS requests
            options.EnableForHttps = true;

            // PERFORMANCE FIX: Use adaptive compression providers for content-size based optimization
            options.Providers.Add<AdaptiveBrotliCompressionProvider>();
            options.Providers.Add<AdaptiveGzipCompressionProvider>();

            // Add MIME types for geospatial data formats
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(additionalMimeTypes);
        });

        // PERFORMANCE FIX: Configure adaptive compression that adjusts level based on content size
        services.Configure<AdaptiveCompressionOptions>(options =>
        {
            // Small content (< 4KB): Use fastest compression for low latency
            options.SmallContentLevel = CompressionLevel.Fastest;
            // Medium and large API payloads are frequently CPU-bound in practice; favor
            // low-latency compression over maximal byte reduction on the hot path.
            options.MediumContentLevel = CompressionLevel.Fastest;
            options.LargeContentLevel = CompressionLevel.Fastest;
        });

        // Register adaptive compression providers
        services.TryAddTransient<AdaptiveBrotliCompressionProvider>();
        services.TryAddTransient<AdaptiveGzipCompressionProvider>();
    }
}
