// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Alerts;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Geocoding;
using Honua.Server.Features.GeometryService;
using Honua.Server.Features.GeoservicesCatalog;
using Honua.Server.Features.Grpc;
using Honua.Server.Features.ImageServer;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Styling;
using Honua.Server.Features.MapServer;
using Honua.Server.Features.NlQuery;
using Honua.Server.Features.OData;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcMaps;
using Honua.Server.Features.OgcTiles;
using Honua.Server.Features.Tiles;
using Honua.Server.Features.Wfs20;

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
        services.AddGeocoding(configuration);
        services.AddImageServer();
        services.AddMapServer();
        services.AddOgcFeatures();
        services.AddOgcMaps();
        services.AddWfs20();
        services.AddOData();
        services.AddGeometryService();
        services.AddHonuaGrpc(configuration);
        services.AddObservability(configuration);
        services.AddAlerts(configuration);
        services.AddNlQuery(configuration);

        return services;
    }

    /// <summary>
    /// Maps feature endpoints in a single, auditable block.
    /// </summary>
    public static IEndpointRouteBuilder MapServerFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapFeatureServerEndpoints();
        endpoints.MapGeocodingEndpoints();
        endpoints.MapGeoservicesCatalogEndpoints();
        endpoints.MapImageServerEndpoints();
        endpoints.MapMapServerEndpoints();
        endpoints.MapAttachmentEndpoints();
        endpoints.MapTileJsonEndpoints();
        endpoints.MapStyleEndpoints();
        endpoints.MapOgcFeaturesEndpoints();
        endpoints.MapOgcMapsEndpoints();
        endpoints.MapOgcTilesEndpoints();
        endpoints.MapWfs20Endpoints();
        endpoints.MapODataEndpoints();
        endpoints.MapGeometryServiceEndpoints();

        return endpoints;
    }
}
