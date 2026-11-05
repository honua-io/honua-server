// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices.Catalog;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.MapServer;
using Honua.Protocols.GeoServices.Sharing;
using Honua.Protocols.GeoServices.VectorTileServer;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Hosting.Modules;

public sealed class GeoServicesProtocolModule : IHonuaProtocolModule
{
    public string Name => "GeoServices";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // FeatureServer, MapServer, ImageServer, GPServer, NAServer are
        // registered individually in FeatureRegistrationExtensions. This
        // module's umbrella registration surface lives there until follow-up
        // work consolidates it.
        ArgumentNullException.ThrowIfNull(services);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGeoservicesCatalogEndpoints();
        endpoints.MapFeatureServerEndpoints();
        endpoints.MapMapServerEndpoints();
        endpoints.MapImageServerEndpoints();
        endpoints.MapVectorTileServerEndpoints();
        endpoints.MapSharingRestEndpoints();
        endpoints.MapVersionManagementServerEndpoints();
    }
}
