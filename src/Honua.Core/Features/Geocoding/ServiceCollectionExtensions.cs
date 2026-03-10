// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Core.Features.Geocoding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Geocoding;

/// <summary>
/// Service collection extensions for core geocoding infrastructure
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add core geocoding services to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodingCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Add configuration
        services.AddOptions<GeocodingConfiguration>()
            .Bind(configuration.GetSection(GeocodingConfiguration.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeocodingConfiguration>, GeocodingConfigurationValidator>();

        // Add core abstractions
        services.TryAddSingleton<IGeocodeProviderRegistry, GeocodeProviderRegistry>();
        services.TryAddSingleton<IGeocodeProviderFactory, GeocodeProviderFactory>();
        services.TryAddScoped<IGeocodeProviderCoordinator, GeocodeProviderCoordinator>();
        services.TryAddScoped<IGeocodeCoordinatorService, GeocodeCoordinatorService>();

        return services;
    }

    /// <summary>
    /// Register a geocoding provider
    /// </summary>
    /// <typeparam name="TProvider">Provider implementation type</typeparam>
    /// <param name="services">Service collection</param>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodeProvider<TProvider>(
        this IServiceCollection services,
        string providerName,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class, IGeocodeProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        services.Add(new ServiceDescriptor(typeof(TProvider), typeof(TProvider), lifetime));

        services.AddOptions<GeocodeProviderRegistrationOptions>()
            .Configure(options =>
            {
                options.Providers[providerName] = serviceProvider => serviceProvider.GetRequiredService<TProvider>();
            });

        return services;
    }

    /// <summary>
    /// Register a geocoding provider with a factory
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="factory">Factory function</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodeProvider(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, IGeocodeProvider> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddOptions<GeocodeProviderRegistrationOptions>()
            .Configure(options =>
            {
                options.Providers[providerName] = factory;
            });

        return services;
    }
}

/// <summary>
/// Options for provider registration
/// </summary>
internal sealed class GeocodeProviderRegistrationOptions
{
    /// <summary>
    /// Dictionary of provider factories
    /// </summary>
    public Dictionary<string, Func<IServiceProvider, IGeocodeProvider>> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Default implementation of the geocoding provider registry
/// </summary>
internal sealed class GeocodeProviderRegistry : IGeocodeProviderRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, IGeocodeProvider>> _providerFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IGeocodeProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;
    private readonly GeocodingConfiguration _configuration;

    public GeocodeProviderRegistry(IServiceProvider serviceProvider, IOptions<GeocodingConfiguration> configuration)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));

        // Register providers from configuration
        RegisterProvidersFromConfiguration();
    }

    public void RegisterProvider(IGeocodeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Name] = provider;
    }

    public void RegisterProvider(string providerName, Func<IServiceProvider, IGeocodeProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);
        _providerFactories[providerName] = factory;
    }

    public IGeocodeProvider? GetProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // Check cached providers first (only singleton instances)
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        // Try to create from factory - do not cache as these may be scoped
        if (_providerFactories.TryGetValue(providerName, out var factory))
        {
            return factory(_serviceProvider);
        }

        return null;
    }

    public IReadOnlyList<IGeocodeProvider> GetAllProviders()
    {
        var providers = new List<IGeocodeProvider>();

        // Add cached providers (singleton instances)
        providers.AddRange(_providers.Values);

        // Create providers from factories (do not cache as they may be scoped)
        foreach (var kvp in _providerFactories)
        {
            if (!_providers.ContainsKey(kvp.Key))
            {
                try
                {
                    var provider = kvp.Value(_serviceProvider);
                    providers.Add(provider);
                }
                catch
                {
                    // Skip providers that can't be created
                }
            }
        }

        return providers;
    }

    public IReadOnlyList<string> GetProvidersByPriority()
    {
        var providers = GetAllProviders();

        // For now, return providers ordered by name
        // In the future, this could be enhanced to use priority from configuration
        return providers
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsProviderRegistered(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return _providers.ContainsKey(providerName) || _providerFactories.ContainsKey(providerName);
    }

    public bool UnregisterProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        var removed = _providers.Remove(providerName);
        removed |= _providerFactories.Remove(providerName);
        return removed;
    }

    private void RegisterProvidersFromConfiguration()
    {
        var registrationOptions = _serviceProvider.GetService<IOptions<GeocodeProviderRegistrationOptions>>();
        if (registrationOptions?.Value.Providers != null)
        {
            foreach (var kvp in registrationOptions.Value.Providers)
            {
                _providerFactories[kvp.Key] = kvp.Value;
            }
        }
    }
}

/// <summary>
/// Default implementation of the geocoding provider factory
/// </summary>
internal sealed class GeocodeProviderFactory : IGeocodeProviderFactory
{
    private readonly IGeocodeProviderRegistry _registry;
    private readonly GeocodingConfiguration _configuration;

    public GeocodeProviderFactory(IGeocodeProviderRegistry registry, IOptions<GeocodingConfiguration> configuration)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IGeocodeProvider? CreateProvider(string providerName)
    {
        return _registry.GetProvider(providerName);
    }

    public IReadOnlyList<string> GetAvailableProviders()
    {
        return _registry.GetProvidersByPriority();
    }

    public IGeocodeProvider GetDefaultProvider()
    {
        var defaultProvider = _registry.GetProvider(_configuration.DefaultProvider);
        if (defaultProvider == null)
        {
            throw new GeocodeProviderException($"Default provider '{_configuration.DefaultProvider}' is not available.")
            {
                ProviderName = _configuration.DefaultProvider,
                ErrorCode = GeocodeErrorCodes.ProviderUnavailable
            };
        }

        return defaultProvider;
    }
}