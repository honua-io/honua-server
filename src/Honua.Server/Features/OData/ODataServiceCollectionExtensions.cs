// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.OData.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OData;

internal static class ODataServiceCollectionExtensions
{
    public static IServiceCollection AddOData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ODataMetadataService>();
        services.AddScoped<ODataQueryService>();
        services.AddScoped<ODataCrudService>();
        services.AddScoped<ODataSearchService>();
        services.AddScoped<ODataQuerySearchService>();
        services.AddScoped<ODataValidationService>();
        services.AddScoped<ODataQueryDependencies>();
        services.AddScoped(sp =>
        {
            var limitsOptions = sp.GetRequiredService<IOptions<LimitsOptions>>().Value;
            return new ODataBatchDependencies(
                sp.GetRequiredService<ILayerCatalog>(),
                sp.GetRequiredService<IFeatureReader>(),
                sp.GetRequiredService<IFeatureWriter>(),
                sp.GetRequiredService<IGeometryValidator>(),
                limitsOptions.Edits);
        });

        services.AddScoped<ODataMetadataHandler>();
        services.AddScoped<ODataQueryHandler>();
        services.AddScoped<ODataStreamingQueryHandler>();
        services.AddScoped<ODataCrudHandler>();
        services.AddScoped<ODataBatchOperationHandler>();
        services.AddScoped<ODataAdvancedQueryHandler>();

        return services;
    }
}
