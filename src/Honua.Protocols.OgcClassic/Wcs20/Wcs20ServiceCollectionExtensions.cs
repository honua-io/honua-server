// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Service collection extensions for WCS 2.0.1.
/// </summary>
internal static class Wcs20ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WCS 2.0.1 protocol services.
    /// </summary>
    internal static IServiceCollection AddWcs20(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<Wcs20CoverageBackend>();
        services.AddScoped<Wcs20Handler>();

        return services;
    }
}
