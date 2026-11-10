// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wcs20;

internal static class Wcs20ServiceCollectionExtensions
{
    internal static IServiceCollection AddWcs20(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<Wcs20CoverageBackend>();
        services.AddScoped<Wcs20Handler>();

        return services;
    }
}
