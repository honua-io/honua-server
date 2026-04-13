// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Service collection extensions for admin features.
/// </summary>
public static class AdminServiceCollectionExtensions
{
    /// <summary>
    /// Adds enhanced admin services including configuration discovery and management.
    /// </summary>
    /// <param name="services">Service collection to configure</param>
    /// <returns>Updated service collection for chaining</returns>
    public static IServiceCollection AddEnhancedAdminServices(this IServiceCollection services)
    {
        // Register configuration documentation service (existing)
        services.TryAddScoped<ConfigurationDocumentationService>();

        // Register enhanced configuration discovery service
        services.TryAddScoped<ConfigurationDiscoveryService>();

        // Register startup connectivity testing service
        services.TryAddScoped<StartupConnectivityTestService>();

        return services;
    }
}
