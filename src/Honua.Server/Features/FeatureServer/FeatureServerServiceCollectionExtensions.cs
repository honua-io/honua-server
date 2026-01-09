// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.FeatureServer.Services;

namespace Honua.Server.Features.FeatureServer;

internal static class FeatureServerServiceCollectionExtensions
{
    public static IServiceCollection AddFeatureServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        services.AddScoped<IQueryFormatter, QueryFormatter>();
        services.AddScoped<IFeatureQueryValidator, FeatureQueryValidator>();
        services.AddScoped<IGeometryValidator, GeometryValidator>();
        services.AddScoped<SpatialReferenceResolver>();
        services.AddScoped<IFeatureServerQueryServices, FeatureServerQueryServices>();
        services.AddScoped<IFeatureServerGeometryServices, FeatureServerGeometryServices>();
        services.AddScoped<StreamingQueryFormatter>();
        services.AddScoped<IRelatedRecordsService, RelatedRecordsService>();
        services.AddScoped<FeatureServerQueryExecutor>();
        services.AddScoped<FeatureServerQueryDependencies>();
        services.AddScoped<FeatureServerQueryHandler>();
        services.AddScoped<FeatureServerRelatedRecordsDependencies>();
        services.AddScoped<FeatureServerRelatedRecordsHandler>();
        services.AddScoped<FeatureServerEditsDependencies>();
        services.AddScoped<FeatureServerEditsHandler>();

        return services;
    }
}
