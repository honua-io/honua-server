// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// DI registration for the middleware-driven audit instrumentation (#507):
/// the route-to-action resolver consumed by the audit middleware and the shared
/// edit-pipeline <see cref="IFeatureWriter"/> audit decorator.
/// </summary>
public static class AuditInstrumentationServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="IAuditActionResolver"/> used by the audit
    /// middleware to map matched routes to audit descriptors. Uses
    /// <c>TryAdd</c> so a host can override the matrix.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaAuditActionResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAuditActionResolver, DefaultAuditActionResolver>();
        return services;
    }

    /// <summary>
    /// Wrap the currently-registered <see cref="IFeatureWriter"/> with the
    /// <see cref="AuditingFeatureWriter"/> so that destructive feature writes
    /// (deletes and bulk/transactional edits) emit audit events at the shared
    /// edit-pipeline boundary, covering every protocol adapter uniformly.
    /// </summary>
    /// <remarks>
    /// Call this <i>after</i> the active data provider has registered its
    /// <see cref="IFeatureWriter"/>. The previously-registered descriptor becomes
    /// the decorated inner writer. If no writer is registered the call is a no-op.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuditingFeatureWriter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var innerDescriptor = services.LastOrDefault(static d => d.ServiceType == typeof(IFeatureWriter));
        if (innerDescriptor is null)
        {
            // No provider writer registered yet; nothing to decorate.
            return services;
        }

        services.AddHttpContextAccessor();
        services.Remove(innerDescriptor);

        services.Add(ServiceDescriptor.Describe(
            typeof(IFeatureWriter),
            sp =>
            {
                var inner = (IFeatureWriter)CreateInner(sp, innerDescriptor);
                var auditLog = sp.GetService<IAuditLog>() ?? NullAuditLog.Instance;
                var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
                return new AuditingFeatureWriter(inner, auditLog, httpContextAccessor);
            },
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

        if (descriptor.ImplementationType is not null)
        {
            return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            "The registered IFeatureWriter descriptor has no resolvable implementation to decorate.");
    }
}
