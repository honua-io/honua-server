// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Editing;

/// <summary>
/// Registration for owner-based edit-policy enforcement at the shared edit-pipeline boundary
/// (SEC-5).
/// </summary>
public static class OwnerEditPolicyEnforcementServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the currently-registered <see cref="IFeatureWriter"/> with
    /// <see cref="OwnerEditPolicyEnforcingFeatureWriter"/> so no protocol adapter, present or
    /// future, can write around a resource's owner-based edit policy.
    /// </summary>
    /// <remarks>
    /// Call this <i>after</i> the active data provider has registered its
    /// <see cref="IFeatureWriter"/>; the previously-registered descriptor becomes the decorated
    /// inner writer. If no writer is registered the call is a no-op.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOwnerEditPolicyEnforcingFeatureWriter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var innerDescriptor = services.LastOrDefault(static d => d.ServiceType == typeof(IFeatureWriter));
        if (innerDescriptor is null)
        {
            return services;
        }

        services.AddHttpContextAccessor();
        services.Remove(innerDescriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IFeatureWriter),
            sp => new OwnerEditPolicyEnforcingFeatureWriter(
                (IFeatureWriter)CreateInner(sp, innerDescriptor),
                sp.GetRequiredService<IMetadataV2GraphProvider>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp),
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
