// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Drafts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

internal static class PackageDraftStoreComposition
{
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
