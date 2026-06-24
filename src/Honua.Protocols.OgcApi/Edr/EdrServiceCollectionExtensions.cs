// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Api.Edr;

/// <summary>
/// Service collection extensions for OGC API - EDR feature registration.
/// </summary>
internal static class EdrServiceCollectionExtensions
{
    /// <summary>Registers OGC API - EDR services with dependency injection.</summary>
    public static IServiceCollection AddEdr(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<EdrDependencies>();
        services.AddScoped<EdrHandler>();

        return services;
    }
}
