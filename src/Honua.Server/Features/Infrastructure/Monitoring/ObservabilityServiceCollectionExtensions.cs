// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Compression;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Operations.Status;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Monitoring;

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
        services.AddSingleton<IOpsDatabasePressureSignal, ProductionMetricsDatabasePressureSignal>();
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

        // Ops-health snapshot composer + deterministic ops-findings engine (ADR-0060 WS4 / #2457).
        // Both are scoped because they compose the scoped deploy-preflight probe.
        services.AddOptions<OpsFindingsOptions>()
            .Bind(configuration.GetSection(OpsFindingsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<OpsAutonomyOptions>()
            .Bind(configuration.GetSection(OpsAutonomyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IOpsHealthSnapshotService, OpsHealthSnapshotService>();

        // Ops "who watches the watcher" seams (#2810): a scheduled audit hash-chain verifier that
        // publishes the last result for the audit-chain-integrity finding, and the fleet-wide
        // alert-evaluation lease heartbeat probe for the alert-evaluation-no-leader finding. Both
        // are optional collaborators folded into OpsFindingsExtendedSignals so the findings-service
        // constructor stays under the DI collaborator ceiling.
        services.AddOptions<AuditChainVerificationOptions>()
            .Bind(configuration.GetSection(AuditChainVerificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IAuditChainVerificationSignal, AuditChainVerificationSignal>();
        services.AddHostedService<AuditChainVerificationBackgroundService>();

        services.AddScoped(sp => new OpsFindingsExtendedSignals
        {
            DatabasePressureSignal = sp.GetService<IOpsDatabasePressureSignal>(),
            AdmissionGate = sp.GetService<Honua.Core.Features.Infrastructure.Abstractions.IRuntimeTunableAdmissionGate>(),
            RollupStore = sp.GetService<IOpsHealthRollupStore>(),
            EvaluationLeader = BuildEvaluationLeaderProbe(sp),
            AuditChainVerification = sp.GetService<IAuditChainVerificationSignal>(),
        });
        services.AddScoped<IOpsFindingsService, OpsFindingsService>();
        services.AddScoped<IOpsAutonomyEvaluator, OpsAutonomyEvaluator>();
        services.AddScoped<IMcpOpsObservabilityReader, McpOpsObservabilityReader>();
        services.AddScoped<IMcpPlatformOpsReader, McpPlatformOpsReader>();

        // Persisted ops-health rollup store + per-replica sampler (#2553). The store itself is registered by
        // the Postgres provider; the sampler and history service resolve it as optional so non-Postgres
        // deployments still expose the (empty) history surface and the realtime flush seam. The flush signal
        // is the in-process seam the realtime broadcast layer (#2554) subscribes to.
        services.AddOptions<OpsHealthRollupOptions>()
            .Bind(configuration.GetSection(OpsHealthRollupOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OpsHealthFlushSignal>();
        services.AddScoped<IOpsHealthHistoryService, OpsHealthHistoryService>();
        services.AddHostedService<OpsHealthRollupSampler>();

        // Aggregated operate-status surface (A12): composes the ops-health snapshot + findings into
        // one server-authoritative verdict, and binds the SLO/error-budget contract.
        services.AddOperateStatus(configuration);

        return services;
    }

    /// <summary>
    /// Builds the fleet-wide alert-evaluation lease probe when the alert pipeline and its checkpoint
    /// store are both registered; returns null otherwise so the no-leader rule stays inert on
    /// deployments without alerts (or without a Postgres checkpoint store).
    /// </summary>
    private static AlertEvaluationLeaderProbe? BuildEvaluationLeaderProbe(IServiceProvider sp)
    {
        var health = sp.GetService<Honua.Alerts.IAlertEvaluationHealth>();
        var checkpointStore = sp.GetService<Honua.Core.Features.Alerts.Abstractions.IAlertCheckpointStore>();
        var options = sp.GetService<Microsoft.Extensions.Options.IOptions<Honua.Core.Features.Alerts.Domain.AlertOptions>>();
        if (health is null || checkpointStore is null || options is null)
        {
            return null;
        }

        return new AlertEvaluationLeaderProbe(health, checkpointStore, options);
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

        // Test isolation (#1851): the integration suite drives one process-wide, schema-routed
        // server (and several isolated per-fixture hosts) where every test selects its data
        // partition via the X-Honua-Test-Schema header rather than a tenant identifier. The
        // output cache keys on tenant (always "<none>" in tests), format, and route values —
        // NOT the schema header — so cacheable metadata responses such as the GeoServices
        // service directory (GET /rest/services) bleed across tests and across license-tier
        // fixtures: the first response cached for a given (tenant, f) is replayed to peers
        // running under a different schema/edition. When test schema headers are enabled we
        // therefore fold the header into the base cache key so the per-test isolation boundary
        // also scopes the output cache. The flag is never set in production, so production
        // cache keys are unchanged.
        var varyOutputCacheByTestSchema =
            configuration.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS");

        services.AddOutputCache(options =>
        {
            // Add dynamic tags and restrict caching to anonymous requests only.
            options.AddBasePolicy(policy =>
            {
                policy.AddPolicy<RouteTagOutputCachePolicy>();
                policy.AddPolicy<AnonymousOnlyOutputCachePolicy>();
                policy.VaryByValue(static context => ResolveTenantOutputCacheKey(context));
                // Metadata responses (service directory, capabilities, collections, STAC,
                // tiles, styles) are filtered by the process-wide license edition and
                // entitlements, but the cache key otherwise only varies by route + tenant +
                // f/Accept. Without an edition fingerprint, anonymous metadata requests that
                // differ only by license tier collide for the TTL — a cross-tier (and, in
                // multi-tenant-per-process deployments, cross-tenant) disclosure and the
                // root cause of the service-directory cache flake (#1983). Add a license
                // fingerprint so tiers never share a cache entry.
                policy.VaryByValue(static context => ResolveLicenseOutputCacheKey(context));

                if (varyOutputCacheByTestSchema)
                {
                    policy.VaryByValue(static context => ResolveTestSchemaOutputCacheKey(context));
                }
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

            // OGC API Styles caching policies (ADR-0048). Styles map to finite
            // styleId/encoding/format keys, so output caching is appropriate.
            options.AddPolicy("OgcStylesList", policy =>
            {
                policy.Expire(ttl.OgcStylesList);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-styles", "metadata");
            });

            options.AddPolicy("OgcStylesConformance", policy =>
            {
                policy.Expire(ttl.OgcStylesConformance);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-styles", "metadata");
            });

            options.AddPolicy("OgcStylesOpenApi", policy =>
            {
                policy.Expire(ttl.OgcStylesOpenApi);
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-styles", "metadata");
            });

            options.AddPolicy("OgcStylesStylesheet", policy =>
            {
                policy.Expire(ttl.OgcStylesStylesheet);
                // Vary by every behavior-changing input: the style identifier and the
                // negotiated encoding (Accept selects MapLibre vs SLD 1.0/1.1).
                policy.SetVaryByRouteValue("styleId");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-styles");
            });

            options.AddPolicy("OgcStylesMetadata", policy =>
            {
                policy.Expire(ttl.OgcStylesMetadata);
                policy.SetVaryByRouteValue("styleId");
                policy.SetVaryByQuery("f");
                policy.SetVaryByHeader("Accept");
                policy.Tag("ogc-styles", "metadata");
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

    private static KeyValuePair<string, string> ResolveTenantOutputCacheKey(HttpContext context)
        => new("tenant", TenantScopeHelpers.ResolveRequestTenantId(context) ?? "<none>");

    /// <summary>
    /// Resolves a license fingerprint (edition + validation state) for the output
    /// cache vary-by so license-tier-filtered metadata responses never share a cache
    /// entry across editions (#1983). Falls back to a stable sentinel when the license
    /// provider is unavailable so caching still functions in minimal hosts.
    /// </summary>
    private static KeyValuePair<string, string> ResolveLicenseOutputCacheKey(HttpContext context)
    {
        var provider = context.RequestServices.GetService<ILicenseStatusProvider>();
        if (provider is null)
        {
            return new KeyValuePair<string, string>("license", "<none>");
        }

        try
        {
            var status = provider.GetCurrentStatus();
            return new KeyValuePair<string, string>(
                "license",
                $"{status.Edition}:{status.ValidationState}:{(status.IsValid ? "1" : "0")}");
        }
        catch
        {
            // Never let cache-key resolution fault a request; degrade to a sentinel.
            return new KeyValuePair<string, string>("license", "<unknown>");
        }
    }
    // Test isolation (#1851): fold the per-test schema header into the output cache key so the
    // schema-routed integration server cannot replay one test's (or license-tier fixture's)
    // cached metadata to a peer running under a different schema. Only wired in when
    // HONUA_TEST_SCHEMA_HEADERS is enabled (test host only); never active in production.
    private static KeyValuePair<string, string> ResolveTestSchemaOutputCacheKey(HttpContext context)
        => new("test-schema", context.Request.Headers["X-Honua-Test-Schema"].ToString());

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
