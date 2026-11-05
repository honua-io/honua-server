// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
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

        var innerDescriptor = services.LastOrDefault(static d => d.ServiceType == typeof(IFeatureWriter));
        if (innerDescriptor is null)
        {
            return services;
        }

        services.AddHttpContextAccessor();
        // The lease store MUST be a singleton shared with the /feature-locks endpoints; a
        // second instance here would enforce against an always-empty store and silently do
        // nothing. TryAdd keeps whichever registration ran first — they name the same types.
        services.TryAddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
        services.TryAddSingleton<IFeatureEditGuard, FeatureEditGuard>();
        services.Remove(innerDescriptor);

        services.Add(ServiceDescriptor.Describe(
            typeof(IFeatureWriter),
            sp => new FeatureLockEnforcingFeatureWriter(
                (IFeatureWriter)CreateInner(sp, innerDescriptor),
                sp.GetRequiredService<IFeatureLockService>(),
                sp.GetRequiredService<IFeatureEditGuard>(),
                sp.GetRequiredService<IMetadataV2GraphProvider>(),
                sp.GetRequiredService<IHttpContextAccessor>()),
            innerDescriptor.Lifetime));

        return services;
    }

    private static object CreateInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return descriptor.ImplementationFactory(sp);
        }

        return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }
}
