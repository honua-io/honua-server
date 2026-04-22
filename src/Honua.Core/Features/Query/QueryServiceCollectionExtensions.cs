// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Query;

/// <summary>
/// Extension methods for registering unified query services.
/// </summary>
public static class QueryServiceCollectionExtensions
{
    /// <summary>
    /// Adds unified query processing services to the service collection.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddUnifiedQueryServices(this IServiceCollection services)
    {
        // Register core query services
        services.TryAddSingleton<IQueryProcessor, QueryProcessor>();
        services.TryAddSingleton<UnifiedQueryService>();

        return services;
    }

    /// <summary>
    /// Adds a protocol-specific query parameter adapter.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <typeparam name="TAdapter">Adapter implementation type</typeparam>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddQueryParameterAdapter<TParams, TAdapter>(this IServiceCollection services)
        where TAdapter : class, IQueryParameterAdapter<TParams>
    {
        services.TryAddSingleton<IQueryParameterAdapter<TParams>, TAdapter>();

        // Register the adapter with the unified query service
        services.Configure<UnifiedQueryServiceOptions>(options =>
        {
            options.AdapterRegistrations.Add(typeof(TParams));
        });

        return services;
    }

    /// <summary>
    /// Configures the unified query service with registered adapters.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection ConfigureUnifiedQueryService(this IServiceCollection services)
    {
        services.AddSingleton<IUnifiedQueryServiceConfigurator, UnifiedQueryServiceConfigurator>();

        return services;
    }
}

/// <summary>
/// Options for configuring the unified query service.
/// </summary>
public sealed class UnifiedQueryServiceOptions
{
    /// <summary>
    /// List of adapter parameter types to register.
    /// </summary>
    public List<Type> AdapterRegistrations { get; } = new();
}

/// <summary>
/// Interface for configuring the unified query service with adapters.
/// </summary>
public interface IUnifiedQueryServiceConfigurator
{
    /// <summary>
    /// Configures the unified query service with registered adapters.
    /// </summary>
    /// <param name="unifiedQueryService">Unified query service instance</param>
    /// <param name="serviceProvider">Service provider for resolving adapters</param>
    void Configure(UnifiedQueryService unifiedQueryService, IServiceProvider serviceProvider);
}

/// <summary>
/// Default implementation of unified query service configurator.
/// </summary>
internal sealed class UnifiedQueryServiceConfigurator : IUnifiedQueryServiceConfigurator
{
    private readonly UnifiedQueryServiceOptions _options;

    public UnifiedQueryServiceConfigurator(Microsoft.Extensions.Options.IOptions<UnifiedQueryServiceOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public void Configure(UnifiedQueryService unifiedQueryService, IServiceProvider serviceProvider)
    {
        foreach (var parameterType in _options.AdapterRegistrations)
        {
            var adapterType = typeof(IQueryParameterAdapter<>).MakeGenericType(parameterType);
            var adapter = serviceProvider.GetService(adapterType);

            if (adapter != null)
            {
                // Use reflection to call RegisterAdapter<T>
                var registerMethod = typeof(UnifiedQueryService)
                    .GetMethod(nameof(UnifiedQueryService.RegisterAdapter))!
                    .MakeGenericMethod(parameterType);

                registerMethod.Invoke(unifiedQueryService, new[] { adapter });
            }
        }
    }
}

/// <summary>
/// Extension methods for using unified query services.
/// </summary>
public static class UnifiedQueryServiceExtensions
{
    /// <summary>
    /// Configures the unified query service on application startup.
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <returns>Configured unified query service</returns>
    public static UnifiedQueryService ConfigureUnifiedQueryService(this IServiceProvider serviceProvider)
    {
        var unifiedQueryService = serviceProvider.GetRequiredService<UnifiedQueryService>();
        var configurator = serviceProvider.GetService<IUnifiedQueryServiceConfigurator>();

        configurator?.Configure(unifiedQueryService, serviceProvider);

        return unifiedQueryService;
    }
}
