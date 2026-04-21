// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.Import;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Wfs20;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.DependencyInjection;

/// <summary>
/// Centralized service registration following SOLID principles and best practices.
/// Replaces scattered service registration with organized, focused registrations.
/// </summary>
internal static class RefactoredServiceRegistration
{
    /// <summary>
    /// Register all core application services using SOLID principles
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Application configuration</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddRefactoredApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services
            .AddCoreInfrastructureServices(configuration)
            .AddGeoSpatialServices(configuration)
            .AddProtocolServices(configuration)
            .AddRenderingServices(configuration)
            .AddSecurityServices(configuration);
    }

    /// <summary>
    /// Register core infrastructure services (logging, caching, monitoring)
    /// </summary>
    private static IServiceCollection AddCoreInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Performance monitoring services (use existing extension)
        services.AddPerformanceMonitoring();

        // Caching services with proper separation of concerns
        services.TryAddSingleton<IQueryResultCacheManager, QueryResultCacheManager>();

        // Health check services (register when available)
        // services.TryAddScoped<IHealthCheckService>();

        return services;
    }

    /// <summary>
    /// Register geospatial data services (import, export, processing)
    /// </summary>
    private static IServiceCollection AddGeoSpatialServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Import services using composition
        services.AddRefactoredImportServices();

        // Feature store services - Use the refactored PostgresFeatureStore
        services.TryAddScoped<IFeatureReader, PostgresFeatureStoreRefactored>();
        services.TryAddScoped<IFeatureWriter, PostgresFeatureStoreRefactored>();

        return services;
    }

    /// <summary>
    /// Register OGC protocol services (WFS, WMS, WMTS, etc.)
    /// </summary>
    private static IServiceCollection AddProtocolServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // WFS 2.0 services using interface segregation
        services.AddWfs20Services();

        return services;
    }

    /// <summary>
    /// Register rendering and styling services
    /// </summary>
    private static IServiceCollection AddRenderingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Rendering services would be registered here when implemented
        return services;
    }

    /// <summary>
    /// Register security and authorization services
    /// </summary>
    private static IServiceCollection AddSecurityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Security services would be registered here when implemented
        return services;
    }

    /// <summary>
    /// Register services with validation to ensure required dependencies are present
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection ValidateServiceRegistration(this IServiceCollection services)
    {
        // Validate that core abstractions have implementations
        ValidateServiceRegistration<IFeatureReader>(services);
        ValidateServiceRegistration<IFeatureWriter>(services);

        return services;
    }

    private static void ValidateServiceRegistration<TService>(IServiceCollection services)
        where TService : class
    {
        var serviceDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TService));
        if (serviceDescriptor == null)
        {
            throw new InvalidOperationException(
                $"Required service {typeof(TService).Name} is not registered. " +
                "Ensure all necessary service registration extensions are called.");
        }
    }
}