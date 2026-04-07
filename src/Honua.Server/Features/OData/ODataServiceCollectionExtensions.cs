// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
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
        services.AddScoped(sp => new ODataCrudDependencies(
            sp.GetRequiredService<IResourceValidator>(),
            sp.GetRequiredService<IFeatureReader>(),
            sp.GetRequiredService<IFeatureWriter>(),
            sp.GetRequiredService<IGeometryService>(),
            sp.GetRequiredService<ICrsRegistry>(),
            sp.GetRequiredService<IETagService>(),
            sp.GetRequiredService<FeatureMutationValidator>()));
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
                sp.GetRequiredService<IGeometryService>(),
                sp.GetRequiredService<FeatureMutationValidator>(),
                sp.GetRequiredService<ICrsRegistry>(),
                limitsOptions.Edits,
                sp.GetRequiredService<ODataValidationService>(),
                sp.GetRequiredService<IETagService>(),
                sp.GetRequiredService<FeatureMutationEventService>());
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
