// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ObjectPool;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Common service registration patterns to eliminate duplication across features.
/// </summary>
public static class ServiceRegistrationPatterns
{
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
    /// Register simple core feature services pattern.
    /// Used by Import, AutoDocs, Styling core features.
    /// </summary>
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
