// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.GeoServices.GeometryService;

/// <summary>
/// Registers geometry service dependencies.
/// </summary>
internal static class GeometryServiceServiceCollectionExtensions
{
    /// <summary>
    /// Adds geometry service handler to the service collection.
    /// </summary>
    public static IServiceCollection AddGeometryService(this IServiceCollection services)
    {
        services.TryAddScoped<SpatialReferenceResolver>();
        services.AddScoped<GeometryServiceHandler>();
        return services;
    }
}
