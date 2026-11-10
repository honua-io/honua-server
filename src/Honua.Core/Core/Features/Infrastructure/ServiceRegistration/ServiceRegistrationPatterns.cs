// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ObjectPool;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

public static class ServiceRegistrationPatterns
{
    public static IServiceCollection AddPerformanceOptimizedObjectPools(this IServiceCollection services)
    {
        var poolProvider = new DefaultObjectPoolProvider();

        services.TryAddSingleton<ObjectPool<System.Text.StringBuilder>>(provider =>
            poolProvider.Create(new DefaultPooledObjectPolicy<System.Text.StringBuilder>()));

        services.TryAddSingleton<ObjectPool<Dictionary<string, object?>>>(provider =>
            poolProvider.Create(new DictionaryPooledObjectPolicy()));

        return services;
    }

    public static IServiceCollection AddSimpleCoreFeature<
        TService,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
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
}

public class DictionaryPooledObjectPolicy : PooledObjectPolicy<Dictionary<string, object?>>
{
    public override Dictionary<string, object?> Create() => new(StringComparer.OrdinalIgnoreCase);

    public override bool Return(Dictionary<string, object?> obj)
    {
        obj.Clear();
        return true;
    }
}

public class DefaultPooledObjectPolicy<T> : PooledObjectPolicy<T> where T : class, new()
{
    public override T Create() => new();

    public override bool Return(T obj) => true;
}
