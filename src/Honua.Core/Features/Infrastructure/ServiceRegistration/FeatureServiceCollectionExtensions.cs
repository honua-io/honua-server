// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

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
