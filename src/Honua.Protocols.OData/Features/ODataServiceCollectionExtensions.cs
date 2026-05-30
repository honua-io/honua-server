// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.OData;

internal static class ODataServiceCollectionExtensions
{
    public static IServiceCollection AddOData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Audit-A1: install the OData error formatter delegate so
        // StandardErrorResponseFormatter (in Honua.Infrastructure.Models) can
        // dispatch OData-formatted responses without a direct using-clause
        // dependency on the OData protocol. The override is idempotent —
        // re-running AddOData() simply re-assigns the same delegate.
        StandardErrorResponseFormatter.ODataErrorFormatterOverride = ODataErrorFormatter.Format;

        // Audit-A1: install the OData validation-error / cancellation-token
        // delegates so Honua.Infrastructure.Validation.LayerValidationHelpers can
        // dispatch OData-shaped validation errors without a direct using-clause
        // dependency on the OData protocol assembly.
        Honua.Infrastructure.Validation.LayerValidationHelpers.ODataErrorFactoryOverride =
            (context, errorCode, message, statusCode) => ODataUtilityService.CreateODataError(context, errorCode, message, statusCode);
        Honua.Infrastructure.Validation.LayerValidationHelpers.ODataCancellationTokenResolverOverride =
            ODataUtilityService.GetTimeoutAwareCancellationToken;

        services.TryAddScoped<IQueryProcessor, QueryProcessor>();
        services.TryAddScoped<IEditProcessor, EditProcessor>();
        services.TryAddScoped<IQueryParameterAdapter<ODataQueryParameters>, ODataQueryParameterAdapter>();
        services.TryAddScoped<IEditParameterAdapter<ODataEditRequest>, ODataEditParameterAdapter>();

        services.AddScoped<ODataMetadataService>();
        services.AddScoped<ODataQueryService>();
        services.AddScoped(sp => new ODataCrudDependencies(
            sp.GetRequiredService<IResourceValidator>(),
            sp.GetRequiredService<IFeatureReader>(),
            sp.GetRequiredService<IFeatureWriter>(),
            sp.GetRequiredService<IGeometryService>(),
            sp.GetRequiredService<ICrsRegistry>(),
            sp.GetRequiredService<IETagService>(),
            sp.GetRequiredService<FeatureMutationValidator>(),
            sp.GetRequiredService<IEditParameterAdapter<ODataEditRequest>>(),
            sp.GetRequiredService<IEditProcessor>()));
        services.AddScoped<ODataCrudService>();
        services.AddScoped<ODataSearchService>();
        services.AddScoped<ODataQuerySearchService>();
        services.AddScoped<ODataValidationService>();
        services.AddScoped<ODataQueryDependencies>();
        services.AddScoped(sp =>
        {
            var limitsOptions = sp.GetRequiredService<IOptions<LimitsOptions>>().Value;
            return new ODataBatchDependencies(
                sp.GetRequiredService<IFeatureReader>(),
                sp.GetRequiredService<IFeatureWriter>(),
                sp.GetRequiredService<IGeometryService>(),
                sp.GetRequiredService<FeatureMutationValidator>(),
                sp.GetRequiredService<ICrsRegistry>(),
                limitsOptions.Edits,
                sp.GetRequiredService<ODataValidationService>(),
                sp.GetRequiredService<IETagService>(),
                sp.GetRequiredService<IEditParameterAdapter<ODataEditRequest>>(),
                sp.GetRequiredService<IEditProcessor>(),
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
