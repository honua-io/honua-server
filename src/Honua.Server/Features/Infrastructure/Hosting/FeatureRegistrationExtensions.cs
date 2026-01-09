// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.OData;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcTiles;

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
        services.AddOgcFeatures();
        services.AddOData();
        services.AddObservability(configuration);

        return services;
    }

    /// <summary>
    /// Maps feature endpoints in a single, auditable block.
    /// </summary>
    public static IEndpointRouteBuilder MapServerFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapFeatureServerEndpoints();
        endpoints.MapAttachmentEndpoints();
        endpoints.MapOgcFeaturesEndpoints();
        endpoints.MapOgcTilesEndpoints();
        endpoints.MapODataEndpoints();

        return endpoints;
    }
}
