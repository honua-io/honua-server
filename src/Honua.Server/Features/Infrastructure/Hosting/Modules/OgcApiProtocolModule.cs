// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Hosting.Modules;

/// <summary>
/// OGC API Features protocol module. Wraps the existing
/// <see cref="OgcFeaturesServiceCollectionExtensions.AddOgcFeatures"/> and
/// <see cref="OgcFeaturesEndpoints.MapOgcFeaturesEndpoints"/> registration
/// calls behind the <see cref="IHonuaProtocolModule"/> contract. The wrapper
/// is additive — direct calls in <c>FeatureRegistrationExtensions</c> still
/// own the canonical wiring.
/// </summary>
public sealed class OgcApiProtocolModule : IHonuaProtocolModule
{
    /// <inheritdoc />
    public string Name => "OgcApi";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOgcFeatures(configuration);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapOgcFeaturesEndpoints();
    }
}
