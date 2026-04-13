// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Common service registration patterns to eliminate duplication across features.
/// </summary>
public static class ServiceRegistrationPatterns
{
    /// <summary>
    /// Register PostgreSQL-based services with standard schema pattern.
    /// </summary>
    public static IServiceCollection AddPostgresFeatureServices<TStore, TInterface>(
        this IServiceCollection services,
        string? schemaName = null,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TStore : class, TInterface
        where TInterface : class
    {
        var factory = CreatePostgresServiceFactory<TStore, TInterface>(schemaName);

        var descriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton<TInterface>(factory),
            ServiceLifetime.Transient => ServiceDescriptor.Transient<TInterface>(factory),
            _ => ServiceDescriptor.Scoped<TInterface>(factory)
        };

        services.TryAdd(descriptor);
        return services;
    }

    /// <summary>
    /// Register multiple PostgreSQL services with the same schema pattern.
    /// </summary>
    public static IServiceCollection AddPostgresFeatureServices(
        this IServiceCollection services,
        string? schemaName = null,
        params (Type ServiceInterface, Type Implementation, ServiceLifetime Lifetime)[] serviceDefinitions)
    {
        foreach (var (serviceInterface, implementation, lifetime) in serviceDefinitions)
        {
            var factory = CreateGenericPostgresServiceFactory(serviceInterface, implementation, schemaName);

            var descriptor = lifetime switch
            {
                ServiceLifetime.Singleton => ServiceDescriptor.Singleton(serviceInterface, factory),
                ServiceLifetime.Transient => ServiceDescriptor.Transient(serviceInterface, factory),
                _ => ServiceDescriptor.Scoped(serviceInterface, factory)
            };

            services.TryAdd(descriptor);
        }

        return services;
    }

    /// <summary>
    /// Register feature store services with multiple segregated interfaces.
    /// Common pattern for stores that implement IFeatureReader, IFeatureWriter, etc.
    /// </summary>
    public static IServiceCollection AddFeatureStoreServices<TStore>(
        this IServiceCollection services,
        string? schemaName = null,
        params Type[] segregatedInterfaces)
        where TStore : class
    {
        // Register the main store
        services.AddPostgresFeatureServices<TStore, TStore>(schemaName);

        // Register segregated interfaces
        foreach (var interfaceType in segregatedInterfaces)
        {
            services.TryAddScoped(interfaceType, provider => provider.GetRequiredService<TStore>());
        }

        return services;
    }

    /// <summary>
    /// Register object pools for performance optimization.
    /// Common pattern across feature stores for StringBuilder and Dictionary pooling.
    /// </summary>
    public static IServiceCollection AddPerformanceOptimizedObjectPools(this IServiceCollection services)
    {
        var poolProvider = new DefaultObjectPoolProvider();

        services.TryAddSingleton<ObjectPool<System.Text.StringBuilder>>(provider =>
            poolProvider.Create(new DefaultPooledObjectPolicy<System.Text.StringBuilder>()));

        services.TryAddSingleton<ObjectPool<Dictionary<string, object?>>>(provider =>
            poolProvider.Create(new DictionaryPooledObjectPolicy()));

        return services;
    }

    /// <summary>
    /// Register configuration-based provider pattern services.
    /// Used by geocoding, styling, and other pluggable features.
    /// </summary>
    public static IServiceCollection AddProviderBasedFeature<TProvider, TRegistry, TFactory, TCoordinator, TService, TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string configurationSectionName)
        where TProvider : class
        where TRegistry : class
        where TFactory : class
        where TCoordinator : class
        where TService : class
        where TOptions : class, new()
    {
        // Add configuration
        services.AddConfigurationOptions<TOptions>(configuration.GetSection(configurationSectionName));

        // Add core abstractions using generic helpers
        services.AddScopedService<TRegistry, TRegistry>();
        services.AddScopedService<TFactory, TFactory>();
        services.AddScopedService<TCoordinator, TCoordinator>();
        services.AddScopedService<TService, TService>();

        return services;
    }

    /// <summary>
    /// Register simple core feature services pattern.
    /// Used by Import, AutoDocs, Styling core features.
    /// </summary>
    public static IServiceCollection AddSimpleCoreFeature<TService, TImplementation>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TService : class
        where TImplementation : class, TService
    {
        var descriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton<TService, TImplementation>(),
            ServiceLifetime.Transient => ServiceDescriptor.Transient<TService, TImplementation>(),
            _ => ServiceDescriptor.Scoped<TService, TImplementation>()
        };

        services.TryAdd(descriptor);
        return services;
    }

    /// <summary>
    /// Register HTTP client-based services with resilience policies.
    /// Common pattern for external service integrations.
    /// </summary>
    public static IServiceCollection AddResilientHttpClientService<TClient, TService>(
        this IServiceCollection services,
        string clientName,
        Action<HttpClient>? configureClient = null,
        Func<HttpMessageHandler>? handlerFactory = null)
        where TClient : class
        where TService : class
    {
        // Register the HTTP client with resilience
        var clientBuilder = services.AddHttpClient<TClient>(clientName, configureClient ?? (_ => { }));

        if (handlerFactory != null)
        {
            clientBuilder.ConfigurePrimaryHttpMessageHandler(handlerFactory);
        }

        // Register the service
        services.TryAddScoped<TService>();

        return services;
    }

    /// <summary>
    /// Register database-dependent services with connection provider pattern.
    /// Common across most PostgreSQL-based features.
    /// </summary>
    public static IServiceCollection AddDatabaseDependentServices(
        this IServiceCollection services,
        params (Type ServiceType, Type Implementation, ServiceLifetime Lifetime)[] servicesList)
    {
        foreach (var (serviceType, implementation, lifetime) in servicesList)
        {
            var factory = CreateDatabaseServiceFactory(serviceType, implementation);

            var descriptor = lifetime switch
            {
                ServiceLifetime.Singleton => ServiceDescriptor.Singleton(serviceType, factory),
                ServiceLifetime.Transient => ServiceDescriptor.Transient(serviceType, factory),
                _ => ServiceDescriptor.Scoped(serviceType, factory)
            };

            services.TryAdd(descriptor);
        }

        return services;
    }

    private static Func<IServiceProvider, TInterface> CreatePostgresServiceFactory<TStore, TInterface>(string? schemaName)
        where TStore : class, TInterface
        where TInterface : class
    {
        return serviceProvider =>
        {
            var constructors = typeof(TStore).GetConstructors();

            // Try to find constructor with schema parameter
            var schemaConstructor = constructors.FirstOrDefault(c =>
                c.GetParameters().Any(p =>
                    p.ParameterType == typeof(string) &&
                    (p.Name?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true)));

            if (schemaConstructor != null)
            {
                var parameters = schemaConstructor.GetParameters();
                var args = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    if (param.ParameterType == typeof(string) &&
                        param.Name?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        args[i] = schemaName ?? "honua";
                    }
                    else
                    {
                        args[i] = serviceProvider.GetRequiredService(param.ParameterType);
                    }
                }

                return (TInterface)Activator.CreateInstance(typeof(TStore), args)!;
            }

            // Fallback to default constructor
            return ActivatorUtilities.CreateInstance<TStore>(serviceProvider);
        };
    }

    private static Func<IServiceProvider, object> CreateGenericPostgresServiceFactory(
        Type serviceInterface,
        Type implementation,
        string? schemaName)
    {
        return serviceProvider =>
        {
            var constructors = implementation.GetConstructors();

            // Try to find constructor with schema parameter
            var schemaConstructor = constructors.FirstOrDefault(c =>
                c.GetParameters().Any(p =>
                    p.ParameterType == typeof(string) &&
                    (p.Name?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true)));

            if (schemaConstructor != null)
            {
                var parameters = schemaConstructor.GetParameters();
                var args = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    if (param.ParameterType == typeof(string) &&
                        param.Name?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        args[i] = schemaName ?? "honua";
                    }
                    else
                    {
                        args[i] = serviceProvider.GetRequiredService(param.ParameterType);
                    }
                }

                return Activator.CreateInstance(implementation, args)!;
            }

            // Fallback to ActivatorUtilities
            return ActivatorUtilities.CreateInstance(serviceProvider, implementation);
        };
    }

    private static Func<IServiceProvider, object> CreateDatabaseServiceFactory(Type serviceType, Type implementation)
    {
        return serviceProvider => ActivatorUtilities.CreateInstance(serviceProvider, implementation);
    }
}

/// <summary>
/// Pooled object policy for Dictionary&lt;string, object?&gt; instances.
/// </summary>
public class DictionaryPooledObjectPolicy : PooledObjectPolicy<Dictionary<string, object?>>
{
    public override Dictionary<string, object?> Create() => new(StringComparer.OrdinalIgnoreCase);

    public override bool Return(Dictionary<string, object?> obj)
    {
        obj.Clear();
        return true;
    }
}

/// <summary>
/// Default pooled object policy for types with parameterless constructor.
/// </summary>
public class DefaultPooledObjectPolicy<T> : PooledObjectPolicy<T> where T : class, new()
{
    public override T Create() => new();

    public override bool Return(T obj) => true;
}
