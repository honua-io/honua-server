// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Geocoding.Features.Geocoding;

/// <summary>
/// Service collection extensions for geocoding infrastructure (basic implementation).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core geocoding services and configuration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddGeocodingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GeocodingConfiguration>()
            .Bind(configuration.GetSection(GeocodingConfiguration.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeocodingConfiguration>, GeocodingConfigurationValidator>();

        services.AddScoped<IGeocodeProviderRegistry, GeocodeProviderRegistry>();
        services.AddScoped<IGeocodeProviderFactory>(sp => (GeocodeProviderRegistry)sp.GetRequiredService<IGeocodeProviderRegistry>());
        services.AddScoped<IGeocodeCoordinatorService, GeocodeCoordinatorService>();
        services.AddScoped<IGeocodeProviderCoordinator, GeocodeProviderCoordinator>();

        return services;
    }

    /// <summary>
    /// Register a geocoding provider.
    /// </summary>
    /// <typeparam name="TProvider">Provider implementation type</typeparam>
    /// <param name="services">Service collection</param>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodeProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(
        this IServiceCollection services,
        string providerName,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class, IGeocodeProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        services.Add(new ServiceDescriptor(typeof(TProvider), typeof(TProvider), lifetime));
        services.Add(new ServiceDescriptor(typeof(IGeocodeProvider), sp => sp.GetRequiredService<TProvider>(), lifetime));
        return services;
    }

    /// <summary>
    /// Register a geocoding provider with a factory.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="factory">Factory function</param>
    /// <param name="lifetime">Service lifetime</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddGeocodeProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, TProvider> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class, IGeocodeProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        services.Add(new ServiceDescriptor(typeof(TProvider), factory, lifetime));
        services.Add(new ServiceDescriptor(typeof(IGeocodeProvider), sp => sp.GetRequiredService<TProvider>(), lifetime));
        return services;
    }
}
