// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance;
using Honua.Core.Features.Metadata;
using Honua.Postgres.Features.Scene;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Infrastructure.Scene;
using Honua.Server.Features.Alerts;
using Honua.Server.Features.CloudDemo;
using Honua.Server.Features.Protocols.Cog;
using Honua.Server.Features.Protocols.Coverages.Multidimensional;
using Honua.Server.Features.Protocols.Zarr;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Geocoding;
using Honua.Server.Features.Grounding.Spec;
using Honua.Server.Features.Protocols.GeoServices.GeometryService;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Protocols.GeoServices.GPServer;
using Honua.Server.Features.Protocols.GeoServices.Catalog;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Features.Protocols.GeoServices.ImageServer;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.Protocols.GeoServices.MapServer;
using Honua.Server.Features.Protocols.GeoServices.NAServer;
using Honua.Server.Features.Protocols.Mcp;
using Honua.Server.Features.NlQuery;
using Honua.Server.Features.Protocols.OData;
using Honua.Server.Features.Protocols.Ogc.Api.Coverages;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Maps;
using Honua.Server.Features.Protocols.Ogc.Api.Processes;
using Honua.Server.Features.Protocols.Ogc.Api.Records;
using Honua.Server.Features.Protocols.Ogc.Api.Tiles;
using Honua.Server.Features.Orchestration;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.Protocols.Tiles;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Honua.Server.Features.Protocols.Ogc.Classic.Wcs20;
using Honua.Server.Features.Protocols.Scene;
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.Server.Features.Protocols.Elevation;
using Honua.Server.Features.Protocols.Stac;
using Honua.Server.Features.Protocols.Terrain;
using Honua.Server.Features.Reporting;
using Honua.Server.Features.Spec;
using Honua.Server.Features.StaticMap;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;
using Honua.Core.Features.Studio;

namespace Honua.Server.Features.Infrastructure.Hosting;

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
        services.AddAnalysisReporting(configuration);
        services.AddMcpOperatorSurface(configuration);
        services.AddSpecGrounding();
        services.AddSpatialAnalytics();
        services.AddSpec(configuration);
        services.AddEnhancedAdminServices();
        services.AddMetadataReleaseServices();
        services.AddStudioPackageLifecycle();
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
        endpoints.MapAnalysisReporting();
        endpoints.MapMcpOperatorSurface();
        endpoints.MapSpecGroundingEndpoints();
        endpoints.MapSpecEndpoints();

        return endpoints;
    }
}
