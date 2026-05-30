// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Classic;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Hosting.Modules;

/// <summary>
/// OGC Classic (WMS / WMTS / WFS / WCS) protocol module. Wraps the existing
/// <see cref="OgcClassicEndpoints.MapOgcClassicEndpoints"/> registration call
/// behind the <see cref="IHonuaProtocolModule"/> contract. Service registration
/// for classic OGC services (WFS 2.0, WCS 2.0, …) is spread across multiple
/// <c>AddXxx</c> calls in <c>FeatureRegistrationExtensions</c>; this module
/// wraps the umbrella endpoint mapping and leaves the granular DI registrations
/// in the canonical site until a follow-up PR migrates them.
/// </summary>
public sealed class OgcClassicProtocolModule : IHonuaProtocolModule
{
    /// <inheritdoc />
    public string Name => "OgcClassic";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Classic OGC services (WFS 2.0, WCS 2.0, OGC Maps, OGC Coverages,
        // OGC Processes, OGC Tiles, OGC Records) are registered individually
        // in FeatureRegistrationExtensions. This module's umbrella registration
        // surface lives there until follow-up work consolidates it.
        ArgumentNullException.ThrowIfNull(services);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapOgcClassicEndpoints();
    }
}
