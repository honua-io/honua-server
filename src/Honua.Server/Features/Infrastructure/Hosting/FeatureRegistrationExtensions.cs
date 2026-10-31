// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance;
using Honua.Core.Features.Metadata;
using Honua.Core.Features.Portal;
using Honua.Core.Features.Temporal;
using Honua.Server.Features.Temporal;
using Honua.Db.Postgres.Features.Scene;
using Honua.Server.Features.Admin;
using Honua.ControlPlane;
using Honua.Ai.AnalysisContent;
using Honua.Server.Features.Capabilities;
using Honua.Infrastructure.Capabilities;
#if !HONUA_EXCLUDE_SCENE
using Honua.Infrastructure.Scene;
#endif
using Honua.Alerts;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.DataEnrichment;
using Honua.Server.Features.Protocols.Cog;
using Honua.Server.Features.Protocols.Coverages.Multidimensional;
using Honua.Server.Features.Protocols.Zarr;
#if !HONUA_EXCLUDE_GEOSERVICES
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.GeometryService;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Protocols.GeoServices.Sharing;
using Honua.Protocols.GeoServices.VectorTileServer;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Honua.Server.Features.PrintingTools;
#endif
using Honua.Server.Features.Geocoding;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Forms;
using Honua.Server.Features.Mobile.FieldCollection.Automations;
using Honua.Server.Features.FieldWorkflows;
using Honua.Server.Features.FieldWorkflows.Export;
using Honua.Server.Features.FieldWorkflows.Review;
using Honua.Ai.Grounding.Spec;
using Honua.Geoprocessing;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Styling;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.NlQuery;
using Honua.Ai.StudioAiProxy;
using Honua.Plugins;
using Honua.Server.Features.Plugins;
using Microsoft.Extensions.DependencyInjection.Extensions;
#if !HONUA_EXCLUDE_ODATA
using Honua.Protocols.OData;
#endif
#if !HONUA_EXCLUDE_OGC_API
using Honua.Protocols.Ogc.Api.Coverages;
using Honua.Protocols.Ogc.Api.Edr;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Maps;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.Protocols.Ogc.Api.Records;
using Honua.Protocols.Ogc.Api.Styles;
using Honua.Protocols.Ogc.Api.Tiles;
#endif
using Honua.Server.Features.Orchestration;
using Honua.PackageReview;
using Honua.Server.Features.Protocols.Tiles;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
#if !HONUA_EXCLUDE_OGC_CLASSIC
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wps20;
#endif
#if !HONUA_EXCLUDE_SCENE
using Honua.Protocols.Scene;
using Honua.Protocols.Scene.I3s;
#endif
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.Server.Features.Protocols.Elevation;
#if !HONUA_EXCLUDE_SENSORTHINGS
using Honua.Protocols.SensorThings;
#endif
#if !HONUA_EXCLUDE_STAC
using Honua.Protocols.Stac;
#endif
using Honua.Server.Features.Protocols.Terrain;
using Honua.Server.Features.Reporting;
using Honua.Server.Features.Spec;
using Honua.Server.Features.StaticMap;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Capabilities;

namespace Honua.Infrastructure.Hosting;

/// <summary>
/// Feature registration helpers for the Honua composition root.
/// </summary>
internal static class FeatureRegistrationExtensions
{
    /// <summary>
    /// Registers feature services in a single, auditable block.
    /// </summary>
    public static IServiceCollection AddServerFeatures(
        this IServiceCollection services,
        IConfiguration configuration,
        bool redisCacheEntitled = false,
        string? hostEnvironmentName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
#if HONUA_EXCLUDE_OGC_API
        _ = hostEnvironmentName;
#endif

#if !HONUA_EXCLUDE_GEOSERVICES
        services.AddFeatureServer();
#endif
        // Plugin/extension SDK (#347): registers IPluginEditPipeline (a no-op unless the
        // Enterprise plugin.sdk entitlement is active and plugins are compiled in) so the
        // FeatureServer edit path can depend on it unconditionally. Custom builds add plugins
        // via the AddHonuaPlugins(configuration, plugins => plugins.Add<...>()) overload.
        services.AddHonuaPlugins(configuration);
        services.AddCloudDemoServices(configuration);
        // Seed a default GPServer-capable service on a bare instance so the
        // geoprocessing facade is usable out of the box (honua-server#2349).
        services.AddGeoprocessingDefaultServiceSeeding(configuration);
        services.AddGeocoding(configuration);
        services.AddForms(configuration);
        services.AddFieldWorkflows();
        services.AddCogServices(configuration);
        services.AddMultidimensionalCoverageServices();
        services.AddZarrServices();
        services.AddTileExportRuntime();
#if !HONUA_EXCLUDE_GEOSERVICES
        services.AddImageServer(configuration);
        services.AddMapServer(configuration);
#endif
#if !HONUA_EXCLUDE_OGC_API
        services.AddOgcCoverages();
        services.AddEdr();
        services.AddOgcFeatures(configuration);
        services.AddOgcMaps();
        services.AddOgcStyles();
        services.AddOgcProcesses(configuration, hostEnvironmentName);
#endif
#if !HONUA_EXCLUDE_OGC_CLASSIC
        services.AddWfs20(configuration);
        services.AddWps20(configuration);
        services.AddWcs20();
#endif
#if !HONUA_EXCLUDE_ODATA
        services.AddOData(configuration);
#endif
#if !HONUA_EXCLUDE_GEOSERVICES
        services.AddGeometryService();
#endif
#if !HONUA_EXCLUDE_GRPC
        services.AddHonuaGrpc(configuration);
#endif
        services.AddObservability(configuration, redisCacheEntitled);
        // Alert health and streaming abstractions are shared by always-on operations
        // surfaces. AlertOptions keeps the workers dormant when alerting is disabled;
        // only the geofence-specific HTTP surfaces are lifecycle-gated below.
        services.AddAlerts(configuration);
        services.AddFieldCollectionAutomations(configuration);
        services.AddNlQuery(configuration);
        services.AddStudioAiProxy(configuration);
#if !HONUA_EXCLUDE_STAC
        services.AddStac();
#endif
#if !HONUA_EXCLUDE_SENSORTHINGS
        // Preview gate: SensorThings is off by default. Set
        // Capabilities__Experimental__serve.sensorthings__Enabled=true to opt in.
        if (CapabilityFlagOptions.IsExperimentalEnabled(configuration, "serve.sensorthings"))
        {
            services.AddSensorThings();
        }
#endif
        services.AddStaticMap();
        services.AddTerrain();
        var sceneCatalogEnabled = CapabilityFlagOptions.IsExperimentalEnabled(configuration, "scene.catalog");
        var scene3dTilesEnabled = CapabilityFlagOptions.IsExperimentalEnabled(configuration, "serve.3d-tiles-scene");
        var sceneI3sEnabled = CapabilityFlagOptions.IsExperimentalEnabled(configuration, "serve.i3s-scene");
        var sceneBimEnabled = CapabilityFlagOptions.IsExperimentalEnabled(configuration, "scene.bim-ingest");
        var scenePointCloudEnabled = CapabilityFlagOptions.IsExperimentalEnabled(configuration, "scene.pointcloud-ingest");
#if !HONUA_EXCLUDE_SCENE
        if (sceneCatalogEnabled || scene3dTilesEnabled || sceneI3sEnabled || sceneBimEnabled || scenePointCloudEnabled)
        {
            services.AddScene(configuration);
            services.AddPostgresSceneRegistry(configuration);
        }
        // Read-through local materialization cache for hosted scene assets so a
        // scene published on one node is servable from any node (#2459, ADR-0060).
        if (scene3dTilesEnabled || sceneI3sEnabled)
        {
            services.AddSceneAssetHydration();
        }

        services.AddSceneGeneration(configuration, scene3dTilesEnabled, sceneBimEnabled, scenePointCloudEnabled);
#else
        if (sceneCatalogEnabled || scene3dTilesEnabled || sceneI3sEnabled || sceneBimEnabled || scenePointCloudEnabled)
        {
            services.AddPostgresSceneRegistry(configuration);
        }
#endif
        services.AddElevation();
#if !HONUA_EXCLUDE_GEOSERVICES
        services.AddPrintingTools();
#endif
        services.AddGeoprocessing(configuration);
        // Routing subsystem (#1266): selects pgRouting or the mock provider via
        // Routing:Provider. The GeoServices NAServer adapter consumes IRoutingProvider.
        Honua.Routing.Features.Routing.ServiceCollectionExtensions.AddRouting(services, configuration);
        // Job-orchestration substrate (queue + log store) used by AddGeoprocessing.
        // Lives in Honua.Server because it composes a Share-export terminal callback
        // that depends on Honua.Server.Features.Admin.Share — out of reach for the
        // carved Honua.Geoprocessing assembly.
        services.AddJobOrchestration();
        // In-process job worker: the hosted service that claims queued jobs, dispatches
        // them to the registered IJobExecutor (the GPServer/Processes execution path), and
        // writes terminal status. Without it, AddJobOrchestration only registers the
        // API-side store/queue, so authenticated GPServer execute/submitJob jobs enqueue
        // and never drain (honua-server#1841). AddJobWorker self-gates on IJobQueue being
        // registered, which AddJobOrchestration only does when the Redis multiplexer is
        // wired — i.e. under the same DevGrant/license Redis entitlement that enables GP
        // (honua-server#1827/#1787). On a Redis-less profile this is a no-op, so the lean
        // API-only and Community editions never start an unconditional worker.
        services.AddJobWorker(configuration);
        // Durable isolated shadow-topology rebuild worker + self-healing reconciler
        // (#2718/#2720). Self-gates on the routing rebuild store (Postgres-only) and the
        // execution-job store (Redis-gated) both being registered above.
        Honua.Server.Features.Admin.Routing.NetworkTopologyRebuildServiceCollectionExtensions.AddNetworkTopologyRebuildJobs(services, configuration);
        services.AddAnalysisContent(configuration);
        services.AddTemporalHistory();
        services.AddAnalysisReporting(configuration);
        services.AddCapabilityManifest(configuration);
        // #2343 (T7): register the capability-gate OpenAPI document transformer into
        // the AddOpenApi pipeline so operations gated on a disabled-experimental
        // capability are pruned from the generated OpenAPI document, keeping the
        // published contract in agreement with the runtime gate (T5). No-op until an
        // experimental capability is flipped off-by-default (T10 / #2346). Depends on
        // the ICapabilityRegistry registered by AddCapabilityManifest above.
        services.AddCapabilityGatedOpenApi();
        services.AddPackageReview();
#if !HONUA_EXCLUDE_MCP
        services.AddMcpDataAccessSurface(configuration);
#endif
        services.AddSpecGrounding();
        services.AddSpatialAnalytics();
        services.AddDataEnrichment(configuration);
        services.AddSpec(configuration);
        services.AddEnhancedAdminServices();
        services.AddMetadataReleaseServices();
        // RBAC-aware ArcGIS Portal item projector consumed by the Portal/Sharing
        // REST read surface (#1371 → #1243).
        services.AddPortalItemProjection();
        services.AddStudioPackageLifecycle(configuration);
        services.TryAddScoped<Honua.Infrastructure.Security.StudioEndpointAuthorization>();
        // Server-side deliverable export: render a Studio map/dashboard/report content version to a
        // shareable PDF/PNG. Reuses the SkiaSharp raster/font surface (the headless Lambda
        // fontconfig/freetype infra from #1728) and the canonical Studio lifecycle store; PDFs are
        // composed via SkiaSharp's SKDocument (no new PDF dependency).
        // Scoped (not Singleton): the exporter consumes the scoped IStudioPackageLifecycleService,
        // so a singleton registration is a captive dependency that fails DI scope validation at
        // host build. It is resolved per-request via [FromServices] on the export endpoint.
        services.TryAddScoped<Honua.Server.Features.Studio.Export.IStudioDeliverableExporter,
            Honua.Server.Features.Studio.Export.StudioDeliverableExporter>();
        // Durable Studio map collaboration: comment threads + activity feed (#1278, slice 1).
        // In-memory store is the default; AddPostgreSqlServices overrides it with durable storage.
        Honua.Core.Features.Console.Collaboration.StudioMapCollaborationServiceCollectionExtensions
            .AddStudioMapCollaboration(services);
        services.AddCompliance(configuration);
        services.AddOrchestration();
        services.AddPMTilesProxy();

#if !HONUA_EXCLUDE_MCP
        // Promotion resources require their canonical stores. Ordinary service
        // publication reads its persisted metadata graph independently of promotion.
        if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.Publishing.Abstractions.IPublishedServiceStore))
            && services.Any(d => d.ServiceType == typeof(Honua.Core.Features.Deployment.Abstractions.IDeploymentStore)))
        {
            services.AddMcpPromotionSurface();
        }
        else if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider)))
        {
            services.AddMcpCatalogPublicationSurface();
        }
#endif

        return services;
    }

    private static IServiceCollection AddPMTilesProxy(this IServiceCollection services)
    {
        services.AddSingleton<PMTilesProxyService>();
        services.AddSingleton<Honua.Server.Features.Infrastructure.FileStorage.ArtifactByteReader>();
        services.AddScoped<Honua.Server.Features.Protocols.Rasters.CogArtifacts.CogArtifactService>();
        // Lets a protocol assembly advertise a published COG without referencing the
        // server: Honua.Server references the protocols, never the reverse, so STAC
        // could otherwise only ever offer the GeoJSON representation of a layer whose
        // raster is published and range-readable.
        services.AddScoped<Honua.Core.Abstractions.Features.Rasters.Abstractions.ICogArtifactLocator,
            Honua.Server.Features.Protocols.Rasters.CogArtifacts.CogArtifactLocator>();
        return services;
    }

    /// <summary>
    /// Maps feature endpoints in a single, auditable block.
    /// </summary>
    public static IEndpointRouteBuilder MapServerFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapFeatureServerEndpoints();
        endpoints.MapVersionManagementServerEndpoints();
#endif
        endpoints.MapCloudDemoEndpoints();
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapGeocodingEndpoints();
#endif
        endpoints.MapFormPackageEndpoints();
        endpoints.MapFieldReviewEndpoints();
        endpoints.MapFieldExportEndpoints();
        endpoints.MapCogEndpoints();
        endpoints.MapMultidimensionalCoverageEndpoints();
        endpoints.MapZarrEndpoints();
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapGeoservicesCatalogEndpoints();
        endpoints.MapSharingRestEndpoints();
        endpoints.MapGeoServicesServerAdminEndpoints();
        endpoints.MapImageServerEndpoints();
        endpoints.MapMapServerEndpoints();
        endpoints.MapVectorTileServerEndpoints();
#endif
#if !HONUA_EXCLUDE_OGC_CLASSIC
        endpoints.MapOgcClassicEndpoints();
#endif
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapAttachmentEndpoints();
#endif
        endpoints.MapTileJsonEndpoints();
        endpoints.MapTerrainEndpoints();
        var lifecycleConfiguration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
#if !HONUA_EXCLUDE_SCENE
        if (CapabilityFlagOptions.IsExperimentalEnabled(lifecycleConfiguration, "scene.catalog"))
        {
            endpoints.MapSceneDiscoveryEndpoints();
            endpoints.MapSceneDatasetEndpoints();
        }
        if (CapabilityFlagOptions.IsExperimentalEnabled(lifecycleConfiguration, "serve.3d-tiles-scene"))
        {
            endpoints.MapSceneEndpoints();
            endpoints.MapSceneGenerationEndpoints();
        }
        if (CapabilityFlagOptions.IsExperimentalEnabled(lifecycleConfiguration, "serve.i3s-scene"))
        {
            endpoints.MapI3sSceneServerEndpoints();
        }
#else
        _ = lifecycleConfiguration;
#endif
        endpoints.MapNetworkDatasetAdminEndpoints();
        endpoints.MapNetworkTopologyEditAdminEndpoints();
        Honua.Server.Features.Admin.Routing.NetworkTopologyRebuildAdminEndpoints.MapNetworkTopologyRebuildAdminEndpoints(endpoints);
        Honua.Server.Features.Admin.Routing.NetworkTopologyPromotionAdminEndpoints.MapNetworkTopologyPromotionAdminEndpoints(endpoints);
        endpoints.MapElevationEndpoints();
        endpoints.MapSceneAnalysisEndpoints();
        endpoints.MapVisibilityAnalysisEndpoints();
#if !HONUA_EXCLUDE_SCENE
        if (CapabilityFlagOptions.IsExperimentalEnabled(lifecycleConfiguration, "scene.bim-ingest"))
        {
            endpoints.MapSceneBimIngestEndpoints();
        }
        if (CapabilityFlagOptions.IsExperimentalEnabled(lifecycleConfiguration, "scene.pointcloud-ingest"))
        {
            endpoints.MapScenePointCloudIngestEndpoints();
        }
#endif
        endpoints.MapPMTilesProxyEndpoints();
        Honua.Server.Features.Protocols.Rasters.CogArtifacts.CogArtifactEndpoints.MapCogArtifactEndpoints(endpoints);
        endpoints.MapStyleEndpoints();
#if !HONUA_EXCLUDE_OGC_API
        endpoints.MapOgcCoveragesEndpoints();
        endpoints.MapEdrEndpoints();
        endpoints.MapOgcFeaturesEndpoints();
        endpoints.MapOgcMapsEndpoints();
        endpoints.MapOgcStylesEndpoints();
#endif
        endpoints.MapGeoprocessingOutputArtifactEndpoints();
#if !HONUA_EXCLUDE_OGC_API
        endpoints.MapOgcProcessesEndpoints();
        endpoints.MapOgcRecordsEndpoints();
        endpoints.MapOgcTilesEndpoints();
#endif
#if !HONUA_EXCLUDE_OGC_CLASSIC
        endpoints.MapWfs20Endpoints();
        endpoints.MapWcs20Endpoints();
#endif
#if !HONUA_EXCLUDE_ODATA
        endpoints.MapODataEndpoints();
#endif
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapGeometryServiceEndpoints();
#endif
#if !HONUA_EXCLUDE_STAC
        endpoints.MapStacEndpoints();
#endif
#if !HONUA_EXCLUDE_SENSORTHINGS
        // Experimental gate (PA-096/PA-103/PA-116/PA-145): SensorThings is off by default.
        var staConfig = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (CapabilityFlagOptions.IsExperimentalEnabled(staConfig, "serve.sensorthings"))
        {
            endpoints.MapSensorThingsEndpoints();
        }
#endif
        endpoints.MapStaticMapEndpoints();
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapNAServerEndpoints();
        endpoints.MapPrintingToolsEndpoints();
        endpoints.MapSpatialAnalyticsRestEndpoints();
        endpoints.MapSpatialAnalyticsOgcEndpoints();
#endif
        endpoints.MapDataEnrichmentEndpoints();
#if !HONUA_EXCLUDE_GEOSERVICES
        endpoints.MapGPServerEndpoints();
#endif
        endpoints.MapAnalysisContentEndpoints();
        endpoints.MapTemporalHistoryEndpoints();
        endpoints.MapTemporalHistorySliceEndpoints();
        endpoints.MapAnalysisReporting();
        endpoints.MapCapabilityManifestEndpoints();
#if !HONUA_EXCLUDE_MCP
        endpoints.MapMcpDataAccessSurface();
#endif
        endpoints.MapSpecGroundingEndpoints();
        endpoints.MapSpecEndpoints();
        // Plugin-contributed REST routes (ICustomEndpoint, #1562). A no-op unless a custom build
        // compiled in a plugin that contributes endpoints; gated per request behind the Enterprise
        // plugin.sdk entitlement and the operator kill-switch.
        endpoints.MapHonuaPluginEndpoints();

        return endpoints;
    }

    /// <summary>
    /// Discovers every <see cref="IHonuaProtocolModule"/> implementation in the
    /// currently-loaded assemblies and invokes its <c>ConfigureServices</c>. The
    /// optional <paramref name="enabledNames"/> filter (typically bound from
    /// <c>Protocols:Enabled</c> in configuration) restricts which modules run;
    /// when null or empty, every discovered module runs.
    /// </summary>
    /// <remarks>
    /// This entry point is additive — it sits alongside
    /// <see cref="AddServerFeatures"/>, which still owns the canonical direct
    /// <c>AddXxx</c> wiring. A follow-up PR will migrate the per-protocol
    /// registrations into modules and remove the duplicated direct calls.
    /// Today calling this method <em>and</em> <c>AddServerFeatures</c> would
    /// register the wrapped protocol services twice; callers must pick one
    /// path until the migration completes.
    /// </remarks>
    public static IServiceCollection AddDiscoveredProtocolModules(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        IReadOnlyCollection<string>? enabledNames = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var module in DiscoverProtocolModules(enabledNames))
        {
            module.ConfigureServices(services, configuration);
        }

        return services;
    }

    /// <summary>
    /// Discovers every <see cref="IHonuaProtocolModule"/> implementation and
    /// invokes its <c>MapEndpoints</c>. Sequencing mirrors
    /// <see cref="AddDiscoveredProtocolModules"/>: when the optional filter
    /// is null or empty, every discovered module runs.
    /// </summary>
    /// <remarks>
    /// This entry point is additive (see <see cref="AddDiscoveredProtocolModules"/>).
    /// </remarks>
    public static IEndpointRouteBuilder MapDiscoveredProtocolModules(
        this IEndpointRouteBuilder endpoints,
        IReadOnlyCollection<string>? enabledNames = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var module in DiscoverProtocolModules(enabledNames))
        {
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }

    private static IEnumerable<IHonuaProtocolModule> DiscoverProtocolModules(
        IReadOnlyCollection<string>? enabledNames)
    {
        foreach (var module in BuiltInProtocolModules)
        {
            if (enabledNames is { Count: > 0 } && !enabledNames.Contains(module.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return module;
        }
    }

    // AOT-safe registry of built-in protocol modules. PublishAot=true on
    // Honua.Server flags reflection-based type discovery + Activator.CreateInstance
    // with IL2070 / IL2072 (no DynamicallyAccessedMembers annotation); maintaining
    // an explicit list keeps the trim analyzer happy and surfaces module-add
    // / module-remove in code review. Protocol-module assemblies extracted in
    // Phase 1 follow-ups will append their new()-able module here.
    // AddServerFeatures remains the canonical registration. Do not also call
    // AddDiscoveredProtocolModules from that path.
    private static readonly IReadOnlyList<IHonuaProtocolModule> BuiltInProtocolModules = new IHonuaProtocolModule[]
    {
#if !HONUA_EXCLUDE_ODATA
        new Modules.ODataProtocolModule(),
#endif
#if !HONUA_EXCLUDE_OGC_API
        new Modules.OgcApiProtocolModule(),
#endif
#if !HONUA_EXCLUDE_OGC_CLASSIC
        new Modules.OgcClassicProtocolModule(),
#endif
#if !HONUA_EXCLUDE_GEOSERVICES
        new Modules.GeoServicesProtocolModule(),
#endif
    };
}
