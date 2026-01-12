// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Services;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Extension methods for registering validation services in the dependency injection container.
/// Consolidates validation service registration to ensure consistent configuration.
/// </summary>
public static class ValidationServiceExtensions
{
    /// <summary>
    /// Adds common validation services to the service collection.
    /// Registers all shared validation components used across protocols.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddValidationServices(this IServiceCollection services)
    {
        // Register core validation services
        services.AddSingleton<ICommonQueryValidator, CommonQueryValidator>();
        services.AddSingleton<IRouteParameterValidator, RouteParameterValidator>();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddScoped<IFilterExpressionTranslator, FilterExpressionTranslator>();
        services.AddScoped<IFilterExpressionService, FilterExpressionService>();
        services.AddScoped<FeatureMutationValidator>();

        // Register unified resource validator for consistent service/layer/collection checks
        services.AddScoped<IResourceValidator, ResourceValidator>();

        // Register unified geometry service for consistent format conversion and Z/M detection
        services.AddSingleton<IGeometryService, GeometryService>();

        return services;
    }
}
