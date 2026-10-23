// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Drafts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Selects the package-draft store from what the host actually composed.
/// Redis when <see cref="IConnectionMultiplexer"/> is registered; otherwise the
/// in-memory store. A store the host already supplied is left alone.
/// </summary>
internal static class PackageDraftStoreComposition
{
    /// <summary>
    /// Replaces the default <see cref="InMemoryPackageDraftStore"/> registration
    /// with a factory that resolves Redis or in-memory at first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void PreferRedisWhenConfigured(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var defaults = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IPackageDraftStore)
                && descriptor.ImplementationType == typeof(InMemoryPackageDraftStore))
            .ToList();
        if (defaults.Count == 0 && services.Any(descriptor => descriptor.ServiceType == typeof(IPackageDraftStore)))
        {
            return;
        }

        foreach (var descriptor in defaults)
        {
            services.Remove(descriptor);
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IPackageDraftStore)))
        {
            return;
        }

        services.TryAddSingleton<IPackageDraftStore>(Create);
    }

    private static IPackageDraftStore Create(IServiceProvider serviceProvider)
    {
        var retention = serviceProvider.GetRequiredService<PackageDraftRetentionOptions>();
        var time = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var redis = serviceProvider.GetService<IConnectionMultiplexer>();
        return redis is null
            ? new InMemoryPackageDraftStore(retention, time)
            : new RedisPackageDraftStore(redis, retention, time);
    }
}
