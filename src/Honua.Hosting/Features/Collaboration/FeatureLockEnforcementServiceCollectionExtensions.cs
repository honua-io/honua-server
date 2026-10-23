// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Collaboration;

/// <summary>
/// Registration for collaborative-editing lease enforcement at the shared edit-pipeline
/// boundary (#4402).
/// </summary>
public static class FeatureLockEnforcementServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the currently-registered <see cref="IFeatureWriter"/> with
    /// <see cref="FeatureLockEnforcingFeatureWriter"/> so no protocol adapter — present or
    /// future — can write through another editor's lease.
    /// </summary>
    /// <remarks>
    /// Call this <i>after</i> the active data provider has registered its
    /// <see cref="IFeatureWriter"/>; the previously-registered descriptor becomes the
    /// decorated inner writer. If no writer is registered the call is a no-op. Mirrors
    /// <c>AddAuditingFeatureWriter</c>, which decorates the same seam for audit events.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
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
