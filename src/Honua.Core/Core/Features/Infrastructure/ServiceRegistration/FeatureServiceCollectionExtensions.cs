// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

public static class ServiceRegistrationHelpers
{
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

    public static IServiceCollection AddScopedService<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        services.TryAddScoped(factory);
        return services;
    }

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

    public static IServiceCollection AddSingletonService<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        services.TryAddSingleton(factory);
        return services;
    }
}
