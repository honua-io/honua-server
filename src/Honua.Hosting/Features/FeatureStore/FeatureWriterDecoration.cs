// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.FeatureStore;

public static class FeatureWriterDecoration
{
    // Wraps the last registered IFeatureWriter, keeping its lifetime. With no writer registered yet there is
    // nothing to decorate, so call this after the provider has registered its writer.
    public static IServiceCollection DecorateFeatureWriter(
        this IServiceCollection services,
        Func<IServiceProvider, IFeatureWriter, IFeatureWriter> decorate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var innerDescriptor = services.LastOrDefault(static d => d.ServiceType == typeof(IFeatureWriter));
        if (innerDescriptor is null)
        {
            return services;
        }

        services.Remove(innerDescriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IFeatureWriter),
            sp => decorate(sp, (IFeatureWriter)CreateInner(sp, innerDescriptor)),
            innerDescriptor.Lifetime));
        return services;
    }

    // Writes to a layer published over a source table are refused at the shared edit boundary.
    public static IServiceCollection AddSourceBackedWriteGuard(this IServiceCollection services)
        => services.DecorateFeatureWriter(static (sp, inner) =>
            new SourceBackedWriteGuardFeatureWriter(inner, sp.GetRequiredService<IMetadataV2GraphProvider>()));

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

        if (descriptor.ImplementationType is not null)
        {
            return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            "The registered IFeatureWriter descriptor has no resolvable implementation to decorate.");
    }
}
