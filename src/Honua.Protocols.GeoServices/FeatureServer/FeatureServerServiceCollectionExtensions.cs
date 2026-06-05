// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Query;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static class FeatureServerServiceCollectionExtensions
{
    public static IServiceCollection AddFeatureServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        // Esri-default datum-transformation parity catalog (stateless, loaded once).
        services.TryAddSingleton<IDatumTransformationCatalog>(static _ => EsriDatumTransformationCatalog.Create());

        services.AddScoped<PbfQueryFormatter>();
        services.AddScoped<IQueryFormatter, QueryFormatter>();
        services.AddScoped<IFeatureQueryValidator, FeatureQueryValidator>();
        services.AddScoped<IGeometryValidator, GeometryValidator>();
        services.TryAddScoped<SpatialReferenceResolver>();
        services.AddScoped<IFeatureServerQueryServices, FeatureServerQueryServices>();
        services.AddScoped<IFeatureServerGeometryServices, FeatureServerGeometryServices>();
        services.TryAddScoped<IQueryProcessor, QueryProcessor>();
        services.TryAddScoped<IEditProcessor, EditProcessor>();
        services.TryAddScoped<IQueryParameterAdapter<GeoServicesQueryRequest>, GeoServicesQueryParameterAdapter>();
        services.TryAddScoped<IEditParameterAdapter<GeoServicesEditRequest>, GeoServicesEditParameterAdapter>();
        services.AddScoped<StreamingQueryFormatter>();
        services.AddScoped<IRelatedRecordsService, RelatedRecordsService>();
        services.AddScoped<FeatureServerQueryExecutor>();
        services.AddScoped<FeatureServerQueryDependencies>();
        services.AddScoped<FeatureServerQueryHandler>();
        services.AddScoped<IFeatureQueryDispatcher>(static serviceProvider =>
            serviceProvider.GetRequiredService<FeatureServerQueryHandler>());
        services.AddScoped<FeatureServerRelatedRecordsDependencies>();
        services.AddScoped<FeatureServerRelatedRecordsHandler>();
        services.AddScoped<FeatureServerEditsDependencies>();
        services.AddScoped<FeatureServerEditsHandler>();

        return services;
    }
}
