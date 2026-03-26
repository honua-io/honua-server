// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Wfs20.Services;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// Service collection extensions for WFS 2.0
/// </summary>
internal static class Wfs20ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WFS 2.0 services
    /// </summary>
    internal static IServiceCollection AddWfs20(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<Wfs20Options>(
            configuration.GetSection(Wfs20Options.SectionName));

        // Register WFS 2.0 core services following established patterns
        services.AddScoped<Wfs20QueryServices>();
        services.AddScoped<Wfs20Handler>();

        // TODO: Register additional WFS 2.0 services as needed:
        // - Feature type schema generator
        // - GML serializer/deserializer
        // - Transaction handler
        // - Feature format converters (GML, GeoJSON, CSV)

        return services;
    }
}
