// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Edit;
using Honua.Core.Features.Query;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

internal static class OgcFeaturesServiceCollectionExtensions
{
    public static IServiceCollection AddOgcFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<OgcFeaturesOptions>(
            configuration.GetSection(OgcFeaturesOptions.SectionName));

        services.AddScoped<OgcFeaturesGeometryServices>();
        services.AddScoped<OgcFilterProcessor>();
        services.TryAddScoped<IQueryProcessor, QueryProcessor>();
        services.TryAddScoped<IEditProcessor, EditProcessor>();
        services.TryAddScoped<IQueryParameterAdapter<OgcFeaturesQueryParameters>, OgcFeaturesQueryParameterAdapter>();
        services.TryAddScoped<IEditParameterAdapter<OgcFeaturesEditRequest>, OgcFeaturesEditParameterAdapter>();
        services.AddScoped<OgcFeaturesQueryDependencies>();
        services.AddScoped<OgcFeaturesQueryHandler>();
        services.AddScoped<OgcFeaturesCrudDependencies>();
        services.AddScoped<OgcFeaturesCrudHandler>();
        services.AddScoped<OgcFeaturesTransactionDependencies>();
        services.AddScoped<OgcFeaturesTransactionHandler>();

        return services;
    }
}
