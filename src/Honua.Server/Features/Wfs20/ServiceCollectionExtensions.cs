// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Wfs20.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// Dependency injection registration for WFS 2.0 services following SOLID principles.
/// Replaces monolithic service registration with focused, segregated services.
/// NOTE: Query services now use the unified query architecture.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WFS 2.0 services using composition and interface segregation
    /// </summary>
    /// <param name="services">Service collection to register services with</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddWfs20Services(this IServiceCollection services)
    {
        // Register segregated WFS 2.0 service interfaces following ISP
        services.TryAddScoped<IWfs20CapabilitiesService, Wfs20CapabilitiesService>();
        services.TryAddScoped<IWfs20SchemaService, Wfs20SchemaService>();
        // NOTE: IWfs20QueryService is now registered by the unified query system
        // services.TryAddScoped<IWfs20QueryService, Wfs20QueryService>();
        services.TryAddScoped<IWfs20TransactionService, Wfs20TransactionService>();

        // Register the facade that coordinates the segregated services
        services.TryAddScoped<Wfs20HandlerFacade>();

        // Register the original handler for backward compatibility
        services.TryAddScoped<Wfs20Handler>();

        return services;
    }

    /// <summary>
    /// Registers WFS 2.0 services with custom configuration
    /// </summary>
    /// <param name="services">Service collection to register services with</param>
    /// <param name="configureOptions">Action to configure WFS 2.0 options</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddWfs20Services(
        this IServiceCollection services,
        Action<Wfs20Options> configureOptions)
    {
        services.Configure(configureOptions);
        return services.AddWfs20Services();
    }
}