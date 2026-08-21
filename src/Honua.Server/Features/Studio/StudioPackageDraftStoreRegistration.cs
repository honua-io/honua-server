// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Drafts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Features.Studio;

internal static class StudioPackageDraftStoreRegistration
{
    /// <summary>
    /// Selects the shared Redis draft store when Redis infrastructure is configured.
    /// The Core registration remains the explicit single-process fallback.
    /// </summary>
    public static IServiceCollection UseDurableStudioPackageDraftStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
        {
            return services;
        }

        services.Replace(ServiceDescriptor.Singleton<IPackageDraftStore>(provider =>
            new RedisPackageDraftStore(
                provider.GetRequiredService<IConnectionMultiplexer>(),
                provider.GetRequiredService<PackageDraftRetentionOptions>(),
                provider.GetRequiredService<TimeProvider>())));
        return services;
    }
}
