// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance;
using Honua.Core.Features.Metadata;
using Honua.Postgres.Features.Scene;
using Honua.Server.Features.Admin;
using Honua.ControlPlane;
using Honua.Ai.AnalysisContent;
using Honua.Server.Features.Capabilities;
using Honua.Infrastructure.Scene;
using Honua.Alerts;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.Protocols.Cog;
using Honua.Server.Features.Protocols.Coverages.Multidimensional;
using Honua.Server.Features.Protocols.Zarr;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Geocoding;
using Honua.Server.Features.Forms;
using Honua.Ai.Grounding.Spec;
using Honua.Protocols.GeoServices.GeometryService;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Styling;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.NlQuery;
using Honua.Protocols.OData;
using Honua.Protocols.Ogc.Api.Coverages;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Maps;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.Protocols.Ogc.Api.Records;
using Honua.Protocols.Ogc.Api.Tiles;
using Honua.Server.Features.Orchestration;
using Honua.PackageReview;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.Protocols.Tiles;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.Protocols.Scene;
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.Server.Features.Protocols.Elevation;
using Honua.Protocols.Stac;
using Honua.Server.Features.Protocols.Terrain;
using Honua.Server.Features.Reporting;
using Honua.Server.Features.Spec;
using Honua.Server.Features.StaticMap;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Core.Features.Studio;

namespace Honua.Infrastructure.Hosting;

/// <summary>
/// Feature registration helpers for the Honua composition root.
/// </summary>
internal static class FeatureRegistrationExtensions
{
    /// <summary>
    /// Registers feature services in a single, auditable block.
    /// </summary>
    public static IServiceCollection AddServerFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFeatureServer();
        services.AddCloudDemoServices(configuration);
        services.AddGeocoding(configuration);
        services.AddForms(configuration);
        services.AddCogServices(configuration);
        services.AddMultidimensionalCoverageServices();
        services.AddZarrServices();
        services.AddImageServer();
        services.AddMapServer();
        services.AddOgcCoverages();
        services.AddOgcFeatures(configuration);
        services.AddOgcMaps();
        services.AddOgcProcesses(configuration);
        services.AddWfs20(configuration);
        services.AddWcs20();
        services.AddOData();
        services.AddGeometryService();
        services.AddHonuaGrpc(configuration);
        services.AddObservability(configuration);
        services.AddAlerts(configuration);
        services.AddNlQuery(configuration);
        services.AddStac();
        services.AddStaticMap();
        services.AddTerrain();
        services.AddScene(configuration);
        services.AddPostgresSceneRegistry(configuration);
        services.AddElevation();
        services.AddSceneGeneration(configuration);
        services.AddPrintingTools();
        services.AddGeoprocessing(configuration);
        // Job-orchestration substrate (queue + log store) used by AddGeoprocessing.
        // Lives in Honua.Server because it composes a Share-export terminal callback
        // that depends on Honua.Server.Features.Admin.Share — out of reach for the
        // carved Honua.Geoprocessing assembly.
        services.AddJobOrchestration();
        services.AddAnalysisContent(configuration);
        services.AddAnalysisReporting(configuration);
        services.AddCapabilityManifest();
        services.AddPackageReview();
        services.AddMcpOperatorSurface(configuration);
        services.AddSpecGrounding();
        services.AddSpatialAnalytics();
        services.AddSpec(configuration);
        services.AddEnhancedAdminServices();
        services.AddMetadataReleaseServices();
        services.AddStudioPackageLifecycle();
        // Durable Studio map collaboration: comment threads + activity feed (#1278, slice 1).
        // In-memory store is the default; AddPostgreSqlServices overrides it with durable storage.
        Honua.Core.Features.Console.Collaboration.StudioMapCollaborationServiceCollectionExtensions
            .AddStudioMapCollaboration(services);
        services.AddCompliance(configuration);
        services.AddOrchestration();
        services.AddPMTilesProxy();

        return services;
    }

    private static IServiceCollection AddPMTilesProxy(this IServiceCollection services)
    {
        services.AddSingleton<PMTilesProxyService>();
        return services;
    }

    /// <summary>
    /// Maps feature endpoints in a single, auditable block.
    /// </summary>
    public static IEndpointRouteBuilder MapServerFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapFeatureServerEndpoints();
        endpoints.MapCloudDemoEndpoints();
        endpoints.MapGeocodingEndpoints();
        endpoints.MapFormPackageEndpoints();
        endpoints.MapCogEndpoints();
        endpoints.MapMultidimensionalCoverageEndpoints();
        endpoints.MapZarrEndpoints();
        endpoints.MapGeoservicesCatalogEndpoints();
        endpoints.MapImageServerEndpoints();
        endpoints.MapMapServerEndpoints();
        endpoints.MapOgcClassicEndpoints();
        endpoints.MapAttachmentEndpoints();
        endpoints.MapTileJsonEndpoints();
        endpoints.MapTerrainEndpoints();
        endpoints.MapSceneDiscoveryEndpoints();
        endpoints.MapSceneEndpoints();
        endpoints.MapSceneDatasetEndpoints();
        endpoints.MapElevationEndpoints();
        endpoints.MapSceneAnalysisEndpoints();
        endpoints.MapVisibilityAnalysisEndpoints();
        endpoints.MapSceneGenerationEndpoints();
        endpoints.MapPMTilesProxyEndpoints();
        endpoints.MapStyleEndpoints();
        endpoints.MapOgcCoveragesEndpoints();
        endpoints.MapOgcFeaturesEndpoints();
        endpoints.MapOgcMapsEndpoints();
        endpoints.MapOgcProcessesEndpoints();
        endpoints.MapOgcRecordsEndpoints();
        endpoints.MapOgcTilesEndpoints();
        endpoints.MapWfs20Endpoints();
        endpoints.MapWcs20Endpoints();
        endpoints.MapODataEndpoints();
        endpoints.MapGeometryServiceEndpoints();
        endpoints.MapStacEndpoints();
        endpoints.MapStaticMapEndpoints();
        endpoints.MapNAServerEndpoints();
        endpoints.MapPrintingToolsEndpoints();
        endpoints.MapSpatialAnalyticsRestEndpoints();
        endpoints.MapSpatialAnalyticsOgcEndpoints();
        endpoints.MapGPServerEndpoints();
        endpoints.MapAnalysisContentEndpoints();
        endpoints.MapAnalysisReporting();
        endpoints.MapCapabilityManifestEndpoints();
        endpoints.MapMcpOperatorSurface();
        endpoints.MapSpecGroundingEndpoints();
        endpoints.MapSpecEndpoints();

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
    private static readonly IReadOnlyList<IHonuaProtocolModule> BuiltInProtocolModules = new IHonuaProtocolModule[]
    {
        new Modules.ODataProtocolModule(),
        new Modules.OgcApiProtocolModule(),
        new Modules.OgcClassicProtocolModule(),
        new Modules.GeoServicesProtocolModule(),
    };
}
