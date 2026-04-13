// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Base class for feature service collection extensions that provides common patterns
/// and eliminates duplication across feature registration implementations.
/// </summary>
public abstract class FeatureServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section name for this feature. Override in derived classes.
    /// </summary>
    protected abstract string ConfigurationSectionName { get; }

    /// <summary>
    /// Feature display name for logging and validation messages.
    /// </summary>
    protected abstract string FeatureName { get; }

    /// <summary>
    /// Register services for this feature with standard configuration binding and validation.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration root</param>
    /// <param name="schemaName">Optional database schema name</param>
    /// <returns>Service collection for chaining</returns>
    public IServiceCollection AddFeatureServices(
        IServiceCollection services,
        IConfiguration configuration,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Register core services
        RegisterCoreServices(services, configuration, schemaName);

        // Register configuration if section exists
        var configSection = configuration.GetSection(ConfigurationSectionName);
        if (configSection.Exists())
        {
            RegisterConfiguration(services, configSection);
        }

        // Register feature-specific services
        RegisterFeatureServices(services, configuration, schemaName);

        return services;
    }

    /// <summary>
    /// Register core services that are common across most features.
    /// Override to customize core service registration.
    /// </summary>
    protected virtual void RegisterCoreServices(
        IServiceCollection services,
        IConfiguration configuration,
        string? schemaName)
    {
        // Base implementation is empty - features override as needed
    }

    /// <summary>
    /// Register configuration options with validation.
    /// Override to customize configuration registration.
    /// </summary>
    protected virtual void RegisterConfiguration(
        IServiceCollection services,
        IConfigurationSection configSection)
    {
        // Base implementation is empty - features override as needed
    }

    /// <summary>
    /// Register feature-specific services. Must be implemented by derived classes.
    /// </summary>
    protected abstract void RegisterFeatureServices(
        IServiceCollection services,
        IConfiguration configuration,
        string? schemaName);
}

/// <summary>
/// Helper methods for consistent service registration patterns.
/// </summary>
public static class ServiceRegistrationHelpers
{
    /// <summary>
    /// Register a scoped service with interface and implementation.
    /// Uses TryAddScoped to avoid duplicate registrations.
    /// </summary>
    public static IServiceCollection AddScopedService<TInterface, TImplementation>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        services.TryAddScoped<TInterface, TImplementation>();
        return services;
    }

    /// <summary>
    /// Register a scoped service with factory.
    /// Uses TryAddScoped to avoid duplicate registrations.
    /// </summary>
    public static IServiceCollection AddScopedService<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        services.TryAddScoped(factory);
        return services;
    }

    /// <summary>
    /// Register a singleton service with interface and implementation.
    /// Uses TryAddSingleton to avoid duplicate registrations.
    /// </summary>
    public static IServiceCollection AddSingletonService<TInterface, TImplementation>(
        this IServiceCollection services)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        services.TryAddSingleton<TInterface, TImplementation>();
        return services;
    }

    /// <summary>
    /// Register a singleton service with factory.
    /// Uses TryAddSingleton to avoid duplicate registrations.
    /// </summary>
    public static IServiceCollection AddSingletonService<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        services.TryAddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Register configuration options with standard validation pattern.
    /// </summary>
    public static IServiceCollection AddConfigurationOptions<TOptions, TValidator>(
        this IServiceCollection services,
        IConfigurationSection configSection)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .Bind(configSection)
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<TOptions>, TValidator>();
        return services;
    }

    /// <summary>
    /// Register configuration options without validator.
    /// </summary>
    public static IServiceCollection AddConfigurationOptions<TOptions>(
        this IServiceCollection services,
        IConfigurationSection configSection)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Register multiple services with the same interface using a factory pattern.
    /// Useful for plugin/provider patterns like geocoding or styling providers.
    /// </summary>
    public static IServiceCollection AddProviderRegistry<TInterface, TRegistryOptions>(
        this IServiceCollection services,
        string optionsConfigurationKey = "Providers")
        where TInterface : class
        where TRegistryOptions : class, new()
    {
        services.AddOptions<TRegistryOptions>(optionsConfigurationKey);
        services.TryAddScoped<IProviderRegistry<TInterface>, DefaultProviderRegistry<TInterface, TRegistryOptions>>();
        return services;
    }

    /// <summary>
    /// Register services based on schema name with consistent factory pattern.
    /// </summary>
    public static IServiceCollection AddSchemaBasedService<TInterface, TImplementation>(
        this IServiceCollection services,
        string? schemaName,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        var factory = CreateSchemaBasedFactory<TInterface, TImplementation>(schemaName);

        var descriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton(factory),
            ServiceLifetime.Transient => ServiceDescriptor.Transient(factory),
            _ => ServiceDescriptor.Scoped(factory)
        };

        services.TryAdd(descriptor);
        return services;
    }

    /// <summary>
    /// Register multiple segregated interfaces that point to the same implementation.
    /// Common pattern for feature stores that implement multiple interfaces.
    /// </summary>
    public static IServiceCollection AddSegregatedInterfaces<TImplementation>(
        this IServiceCollection services,
        params Type[] interfaceTypes)
        where TImplementation : class
    {
        // Register the main implementation
        services.TryAddScoped<TImplementation>();

        // Register each interface to resolve to the main implementation
        foreach (var interfaceType in interfaceTypes)
        {
            services.TryAddScoped(interfaceType, provider => provider.GetRequiredService<TImplementation>());
        }

        return services;
    }

    /// <summary>
    /// Register read-only implementations for write operations when feature doesn't support them.
    /// </summary>
    public static IServiceCollection AddReadOnlyImplementations(
        this IServiceCollection services,
        params (Type ServiceType, Type ReadOnlyImplementation)[] readOnlyServices)
    {
        foreach (var (serviceType, implementation) in readOnlyServices)
        {
            services.TryAddScoped(serviceType, provider => Activator.CreateInstance(implementation)!);
        }

        return services;
    }

    private static Func<IServiceProvider, TInterface> CreateSchemaBasedFactory<TInterface, TImplementation>(string? schemaName)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        return serviceProvider =>
        {
            // Get constructor that accepts schema name
            var constructor = typeof(TImplementation).GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(string) &&
                    p.Name?.Contains("schema", StringComparison.OrdinalIgnoreCase) == true));

            if (constructor == null)
            {
                // Fallback to default constructor
                return (TInterface)ActivatorUtilities.CreateInstance<TImplementation>(serviceProvider);
            }

            // Build constructor arguments
            var parameters = constructor.GetParameters();
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

            return (TInterface)Activator.CreateInstance(typeof(TImplementation), args)!;
        };
    }
}

/// <summary>
/// Generic provider registry interface for plugin patterns.
/// </summary>
public interface IProviderRegistry<T>
{
    void RegisterProvider(string name, Func<IServiceProvider, T> factory);
    T? GetProvider(string name);
    IReadOnlyList<T> GetAllProviders();
    IReadOnlyList<string> GetProviderNames();
    bool IsProviderRegistered(string name);
    bool UnregisterProvider(string name);
}

/// <summary>
/// Default implementation of provider registry.
/// </summary>
internal class DefaultProviderRegistry<TInterface, TOptions> : IProviderRegistry<TInterface>
    where TInterface : class
    where TOptions : class
{
    private readonly Dictionary<string, Func<IServiceProvider, TInterface>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;

    public DefaultProviderRegistry(IServiceProvider serviceProvider, IOptions<TOptions> options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // Initialize providers from configuration if TOptions has a Providers property
        InitializeFromConfiguration(options?.Value);
    }

    public void RegisterProvider(string name, Func<IServiceProvider, TInterface> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[name] = factory;
    }

    public TInterface? GetProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_factories.TryGetValue(name, out var factory))
        {
            return factory(_serviceProvider);
        }

        return null;
    }

    public IReadOnlyList<TInterface> GetAllProviders()
    {
        var providers = new List<TInterface>();
        foreach (var factory in _factories.Values)
        {
            try
            {
                var provider = factory(_serviceProvider);
                providers.Add(provider);
            }
            catch
            {
                // Skip providers that can't be created
            }
        }
        return providers;
    }

    public IReadOnlyList<string> GetProviderNames()
    {
        return _factories.Keys.ToArray();
    }

    public bool IsProviderRegistered(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _factories.ContainsKey(name);
    }

    public bool UnregisterProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _factories.Remove(name);
    }

    private void InitializeFromConfiguration(TOptions? options)
    {
        if (options == null) return;

        // Use reflection to check for Providers property
        var providersProperty = typeof(TOptions).GetProperty("Providers");
        if (providersProperty?.GetValue(options) is not IDictionary<string, object> providers) return;

        foreach (var kvp in providers)
        {
            if (kvp.Value is Func<IServiceProvider, TInterface> factory)
            {
                _factories[kvp.Key] = factory;
            }
        }
    }
}