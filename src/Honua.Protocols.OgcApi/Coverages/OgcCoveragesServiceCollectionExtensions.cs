// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Coverages.Handlers;
using Honua.Protocols.Ogc.Api.Coverages.Services;

namespace Honua.Protocols.Ogc.Api.Coverages;

/// <summary>
/// Service collection extensions for OGC API - Coverages feature registration.
/// </summary>
internal static class OgcCoveragesServiceCollectionExtensions
{
    /// <summary>
    /// Registers OGC API - Coverages services with dependency injection.
    /// </summary>
    public static IServiceCollection AddOgcCoverages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ZarrCoverageService>();
        services.AddScoped<OgcCoveragesDependencies>();
        services.AddScoped<OgcCoveragesHandler>();

        return services;
    }
}
