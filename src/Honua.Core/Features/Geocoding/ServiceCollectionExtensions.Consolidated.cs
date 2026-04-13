// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Core.Features.Geocoding.Services;
using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Geocoding;

/// <summary>
/// Consolidated service collection extensions for geocoding infrastructure using the new framework.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add core geocoding services to the service collection using consolidated patterns.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodingCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configSection = configuration.GetSection(GeocodingConfiguration.SectionName);

        // Register configuration with validation using consolidated pattern
        services.AddValidatedConfiguration<GeocodingConfiguration, GeocodingConfigurationValidator>(configSection);

        // Register core geocoding services using consolidated patterns
        services
            .AddScopedService<IGeocodeProviderRegistry, GeocodeProviderRegistry>()
            .AddScopedService<IGeocodeProviderFactory, GeocodeProviderFactory>()
            .AddScopedService<IGeocodeProviderCoordinator, GeocodeProviderCoordinator>()
            .AddScopedService<IGeocodeCoordinatorService, GeocodeCoordinatorService>();

        // Register provider registry pattern
        services.AddProviderRegistry<IGeocodeProvider, GeocodeProviderRegistrationOptions>();

        return services;
    }

    /// <summary>
    /// Register a geocoding provider using consolidated pattern.
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

        // Use consolidated pattern for provider registration
        return services.AddProviderService<TProvider, IGeocodeProvider>(providerName, lifetime);
    }

    /// <summary>
    /// Register a geocoding provider with a factory using consolidated pattern.
    /// </summary>
    public static IServiceCollection AddGeocodeProvider(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, IGeocodeProvider> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        // Use consolidated pattern for factory-based provider registration
        return services.AddProviderService(providerName, factory, lifetime);
    }
}

/// <summary>
/// Extension methods for provider service registration (part of consolidation framework).
/// </summary>
internal static class ProviderServiceExtensions
{
    /// <summary>
    /// Register a provider service with consolidated pattern.
    /// </summary>
    public static IServiceCollection AddProviderService<TProvider, TInterface>(
        this IServiceCollection services,
        string providerName,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class, TInterface
        where TInterface : class
    {
        services.Add(new ServiceDescriptor(typeof(TProvider), typeof(TProvider), lifetime));

        services.AddOptions<GeocodeProviderRegistrationOptions>()
            .Configure(options =>
            {
                options.Providers[providerName] = serviceProvider => serviceProvider.GetRequiredService<TProvider>();
            });

        return services;
    }

    /// <summary>
    /// Register a provider service with factory using consolidated pattern.
    /// </summary>
    public static IServiceCollection AddProviderService<TInterface>(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, TInterface> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TInterface : class
    {
        services.AddOptions<GeocodeProviderRegistrationOptions>()
            .Configure(options =>
            {
                options.Providers[providerName] = factory;
            });

        return services;
    }
}

/// <summary>
/// Consolidated geocoding configuration validator.
/// </summary>
public class GeocodingConfigurationValidator : ConfigurationValidator<GeocodingConfiguration>
{
    protected override void PerformFeatureSpecificValidation(GeocodingConfiguration options, List<string> errors)
    {
        // Validate default provider
        ValidateRequired(options.DefaultProvider, nameof(options.DefaultProvider), errors);

        // Validate timeout is reasonable
        ValidateRange(options.TimeoutSeconds, 1, 300, nameof(options.TimeoutSeconds), errors);

        // Validate max results
        ValidateRange(options.MaxResults, 1, 100, nameof(options.MaxResults), errors);

        // Validate rate limiting if configured
        if (options.RateLimitPerSecond.HasValue)
        {
            ValidateRange(options.RateLimitPerSecond.Value, 1, 1000, nameof(options.RateLimitPerSecond), errors);
        }

        // Validate enabled providers collection
        ValidateCollectionNotEmpty(options.EnabledProviders, nameof(options.EnabledProviders), errors);
    }
}