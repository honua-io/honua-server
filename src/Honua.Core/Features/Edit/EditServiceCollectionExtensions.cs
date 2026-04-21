// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Service collection extensions for registering unified edit services.
/// </summary>
public static class EditServiceCollectionExtensions
{
    /// <summary>
    /// Adds the unified edit services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddUnifiedEditServices(
        this IServiceCollection services,
        Action<UnifiedEditServiceOptions>? configure = null)
    {
        // Register core edit services
        services.TryAddSingleton<IEditProcessor, EditProcessor>();
        services.TryAddSingleton<UnifiedEditService>();

        // Configure options
        if (configure != null)
        {
            services.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Adds a protocol-specific edit parameter adapter.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <typeparam name="TAdapter">Adapter implementation type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEditParameterAdapter<TRequest, TAdapter>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TAdapter : class, IEditParameterAdapter<TRequest>
    {
        services.Add(new ServiceDescriptor(
            typeof(IEditParameterAdapter<TRequest>),
            typeof(TAdapter),
            lifetime));

        return services;
    }

    /// <summary>
    /// Adds a protocol-specific edit parameter adapter with factory.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="factory">Factory function</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEditParameterAdapter<TRequest>(
        this IServiceCollection services,
        Func<IServiceProvider, IEditParameterAdapter<TRequest>> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services.Add(new ServiceDescriptor(
            typeof(IEditParameterAdapter<TRequest>),
            factory,
            lifetime));

        return services;
    }
}

/// <summary>
/// Configuration options for unified edit services.
/// </summary>
public sealed class UnifiedEditServiceOptions
{
    /// <summary>
    /// Default transaction timeout in milliseconds.
    /// </summary>
    public int DefaultTransactionTimeoutMs { get; set; } = 300_000; // 5 minutes

    /// <summary>
    /// Maximum number of operations per edit request.
    /// </summary>
    public int MaxOperationsPerRequest { get; set; } = 10_000;

    /// <summary>
    /// Maximum number of requests per batch transaction.
    /// </summary>
    public int MaxRequestsPerBatch { get; set; } = 100;

    /// <summary>
    /// Whether to enable performance estimation.
    /// </summary>
    public bool EnablePerformanceEstimation { get; set; } = true;

    /// <summary>
    /// Whether to enable edit request optimization.
    /// </summary>
    public bool EnableEditOptimization { get; set; } = true;

    /// <summary>
    /// Whether to enable parallel processing for independent operations.
    /// </summary>
    public bool EnableParallelProcessing { get; set; } = true;

    /// <summary>
    /// Default validation options for edit requests.
    /// </summary>
    public EditValidationOptions DefaultValidationOptions { get; set; } = EditValidationOptions.Strict();

    /// <summary>
    /// Default transaction configuration.
    /// </summary>
    public TransactionConfiguration DefaultTransactionConfiguration { get; set; } = TransactionConfiguration.Default;
}

/// <summary>
/// Extensions for configuring unified edit service with specific adapters.
/// </summary>
public static class UnifiedEditServiceExtensions
{
    /// <summary>
    /// Configures the unified edit service with registered adapters.
    /// Should be called after all adapters are registered.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection ConfigureUnifiedEditService(this IServiceCollection services)
    {
        services.AddSingleton<IEditServiceConfigurator, EditServiceConfigurator>();
        return services;
    }
}

/// <summary>
/// Interface for configuring the unified edit service with registered adapters.
/// </summary>
public interface IEditServiceConfigurator
{
    /// <summary>
    /// Configures the unified edit service with all registered adapters.
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    void Configure(IServiceProvider serviceProvider);
}

/// <summary>
/// Default implementation of edit service configurator.
/// </summary>
internal sealed class EditServiceConfigurator : IEditServiceConfigurator
{
    public void Configure(IServiceProvider serviceProvider)
    {
        var unifiedEditService = serviceProvider.GetRequiredService<UnifiedEditService>();

        // Register all available adapters with the unified service
        var adapters = serviceProvider.GetServices<object>()
            .Where(service => service.GetType().GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEditParameterAdapter<>)))
            .ToList();

        foreach (var adapter in adapters)
        {
            var adapterType = adapter.GetType();
            var interfaceType = adapterType.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEditParameterAdapter<>));

            var requestType = interfaceType.GetGenericArguments()[0];
            var registerMethod = typeof(UnifiedEditService)
                .GetMethod("RegisterAdapter")!
                .MakeGenericMethod(requestType);

            registerMethod.Invoke(unifiedEditService, new[] { adapter });
        }
    }
}