// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.Admin.Services;
using Honua.Infrastructure.Configuration;
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
        services.TryAddSingleton<IConfigurationValidator, ConfigurationValidator>();
        services.TryAddSingleton<IConfigurationDiscovery>(sp => (ConfigurationValidator)sp.GetRequiredService<IConfigurationValidator>());
        services.TryAddSingleton<IExternalServiceDiscoveryNetworkGuard, ExternalServiceDiscoveryNetworkGuard>();
        services.TryAddScoped<IExternalServiceDiscoveryService, ExternalServiceDiscoveryService>();
        services.AddHttpClient(ExternalServiceDiscoveryService.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaServer/1.0");
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        // Register configuration documentation service (existing)
        services.TryAddScoped<ConfigurationDocumentationService>();

        // Register enhanced configuration discovery service
        services.TryAddScoped<ConfigurationDiscoveryService>();

        // Register startup connectivity testing service
        services.TryAddScoped<StartupConnectivityTestService>();

        services.TryAddScoped<ILayerValidationService, LayerValidationService>();

        return services;
    }
}
