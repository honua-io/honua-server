// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

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
    public static IServiceCollection AddScopedService<
        TInterface,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
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
    public static IServiceCollection AddSingletonService<
        TInterface,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
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
}
