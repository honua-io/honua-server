// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Collaboration;

public static class FeatureLockEnforcementServiceCollectionExtensions
{
    // Call this after the provider has registered IFeatureWriter, or the decorator is not installed and lock enforcement is omitted.
    public static IServiceCollection AddFeatureLockEnforcingFeatureWriter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(static d => d.ServiceType == typeof(IFeatureWriter)))
        {
            return services;
        }

        services.AddHttpContextAccessor();
        // The lease store MUST be a singleton shared with the /feature-locks endpoints; a
        // second instance here would enforce against an always-empty store and silently do
        // nothing. TryAdd keeps whichever registration ran first — they name the same types.
        services.TryAddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
        services.TryAddSingleton<IFeatureEditGuard, FeatureEditGuard>();
        return services.DecorateFeatureWriter(static (sp, inner) => new FeatureLockEnforcingFeatureWriter(
            inner,
            sp.GetRequiredService<IFeatureLockService>(),
            sp.GetRequiredService<IFeatureEditGuard>(),
            sp.GetRequiredService<IMetadataV2GraphProvider>(),
            sp.GetRequiredService<IHttpContextAccessor>()));
    }
}
