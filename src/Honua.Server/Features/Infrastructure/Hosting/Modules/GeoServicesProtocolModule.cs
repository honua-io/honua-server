// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Protocols.GeoServices.ImageServer;
using Honua.Server.Features.Protocols.GeoServices.MapServer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Hosting.Modules;

/// <summary>
/// Esri GeoServices protocol module (FeatureServer / MapServer / ImageServer
/// and the GPServer / NAServer family). Wraps the existing
/// <c>MapFeatureServerEndpoints</c> / <c>MapMapServerEndpoints</c> /
/// <c>MapImageServerEndpoints</c> registration calls behind the
/// <see cref="IHonuaProtocolModule"/> contract. Service registration is
/// spread across multiple <c>AddXxx</c> calls in
/// <c>FeatureRegistrationExtensions</c>; this module wraps the canonical
/// endpoint mapping set.
/// </summary>
public sealed class GeoServicesProtocolModule : IHonuaProtocolModule
{
    /// <inheritdoc />
    public string Name => "GeoServices";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // FeatureServer, MapServer, ImageServer, GPServer, NAServer are
        // registered individually in FeatureRegistrationExtensions. This
        // module's umbrella registration surface lives there until follow-up
        // work consolidates it.
        ArgumentNullException.ThrowIfNull(services);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapFeatureServerEndpoints();
        endpoints.MapMapServerEndpoints();
        endpoints.MapImageServerEndpoints();
    }
}
