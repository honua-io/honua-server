// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;

namespace Honua.Infrastructure.Validation;

public static class ValidationServiceExtensions
{
    public static IServiceCollection AddValidationServices(this IServiceCollection services)
    {
        // Register core validation services
        services.AddSingleton<ICommonQueryValidator, CommonQueryValidator>();
        services.AddSingleton<IRouteParameterValidator, RouteParameterValidator>();
        services.AddSingleton<IAccessPolicyEvaluator>(_ => new AccessPolicyEvaluator());

        // Principal-based per-layer authorization seam (honua-server#3046). Shared by the
        // geoprocessing submit pipeline so a job that reads a catalog layer is gated by the
        // same decision the synchronous query surfaces apply. Needs the ambient request
        // accessor to reach the request scope (and its tenant rail) from a singleton.
        services.AddHttpContextAccessor();
        services.AddSingleton<ILayerAccessAuthorizer, LayerAccessAuthorizer>();
        services.AddScoped<IFilterExpressionTranslator, FilterExpressionTranslator>();
        services.AddScoped<IFilterExpressionService, FilterExpressionService>();
        services.AddScoped<FeatureMutationValidator>();

        // Register unified resource validator for consistent service/layer/collection checks
        services.AddScoped<IResourceValidator, ResourceValidator>();

        return services;
    }
}
