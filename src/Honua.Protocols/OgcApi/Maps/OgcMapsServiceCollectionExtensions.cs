// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Maps.Handlers;

namespace Honua.Protocols.Ogc.Api.Maps;

internal static class OgcMapsServiceCollectionExtensions
{
    public static IServiceCollection AddOgcMaps(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register handlers
        services.AddScoped<OgcMapsConformanceHandler>();
        services.AddScoped<OgcMapsRenderingHandler>();
        services.AddScoped<OgcMapsTileSetHandler>();

        return services;
    }
}
