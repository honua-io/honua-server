// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Middleware;

public static class AuditInstrumentationServiceCollectionExtensions
{
    public static IServiceCollection AddHonuaAuditActionResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAuditActionResolver, DefaultAuditActionResolver>();
        return services;
    }

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
