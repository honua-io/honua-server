// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

        return services;
    }
}
