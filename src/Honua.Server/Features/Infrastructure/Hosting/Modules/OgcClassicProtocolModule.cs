// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Ogc.Classic;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Hosting.Modules;

public sealed class OgcClassicProtocolModule : IHonuaProtocolModule
{
    public string Name => "OgcClassic";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Classic OGC services (WFS 2.0, WCS 2.0, OGC Maps, OGC Coverages,
        // OGC Processes, OGC Tiles, OGC Records) are registered individually
        // in FeatureRegistrationExtensions. This module's umbrella registration
        // surface lives there until follow-up work consolidates it.
        ArgumentNullException.ThrowIfNull(services);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapOgcClassicEndpoints();
    }
}
