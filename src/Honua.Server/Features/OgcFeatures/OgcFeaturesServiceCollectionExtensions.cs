// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

internal static class OgcFeaturesServiceCollectionExtensions
{
    public static IServiceCollection AddOgcFeatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OgcFeaturesGeometryServices>();
        services.AddScoped<OgcFilterProcessor>();
        services.AddScoped<OgcFeaturesQueryDependencies>();
        services.AddScoped<OgcFeaturesQueryHandler>();
        services.AddScoped<OgcFeaturesCrudDependencies>();
        services.AddScoped<OgcFeaturesCrudHandler>();
        services.AddScoped<OgcFeaturesTransactionDependencies>();
        services.AddScoped<OgcFeaturesTransactionHandler>();

        return services;
    }
}
