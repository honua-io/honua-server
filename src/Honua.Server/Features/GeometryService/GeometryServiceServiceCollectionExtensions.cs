// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.GeometryService.Services;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.GeometryService;

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
