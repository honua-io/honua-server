// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Geocoding;

/// <summary>
/// Service collection extensions for geocoding infrastructure (basic implementation).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a geocoding provider.
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
        where TProvider : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        services.Add(new ServiceDescriptor(typeof(TProvider), typeof(TProvider), lifetime));
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
    public static IServiceCollection AddGeocodeProvider<TProvider>(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, TProvider> factory,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TProvider : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(factory);

        services.Add(new ServiceDescriptor(typeof(TProvider), factory, lifetime));
        return services;
    }
}