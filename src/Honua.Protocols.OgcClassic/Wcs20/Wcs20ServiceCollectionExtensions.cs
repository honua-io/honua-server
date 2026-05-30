// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

        services.AddScoped<Wcs20Handler>();

        return services;
    }
}
