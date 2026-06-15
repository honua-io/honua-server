// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
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

        // Shared Esri datum-transformation catalog (WKID -> PROJ pipeline) used by the
        // project operation. TryAdd keeps a single instance shared with the FeatureServer
        // and ImageServer registrations regardless of registration order.
        services.TryAddSingleton<IDatumTransformationCatalog>(static _ => EsriDatumTransformationCatalog.Create());
        return services;
    }
}
