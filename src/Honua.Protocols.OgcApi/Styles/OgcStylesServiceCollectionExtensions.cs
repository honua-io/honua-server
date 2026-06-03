// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Styles.Handlers;

namespace Honua.Protocols.Ogc.Api.Styles;

/// <summary>
/// Service collection extensions for OGC API - Styles feature registration.
/// </summary>
internal static class OgcStylesServiceCollectionExtensions
{
    /// <summary>
    /// Registers OGC API - Styles services with dependency injection. The style
    /// projection itself (<c>IOgcStyleProjection</c>) is registered in
    /// <c>Honua.Server</c> alongside the internal style services it composes.
    /// </summary>
    public static IServiceCollection AddOgcStyles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OgcStylesConformanceHandler>();

        return services;
    }
}
