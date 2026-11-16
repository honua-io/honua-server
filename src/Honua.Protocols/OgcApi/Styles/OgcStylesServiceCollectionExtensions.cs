// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Styles.Handlers;

namespace Honua.Protocols.Ogc.Api.Styles;

internal static class OgcStylesServiceCollectionExtensions
{
    public static IServiceCollection AddOgcStyles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OgcStylesConformanceHandler>();

        return services;
    }
}
