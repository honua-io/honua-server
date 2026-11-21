// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.FeatureStore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Middleware;

public static class AuditInstrumentationServiceCollectionExtensions
{
    private static readonly AuditPolicyOptions DefaultPolicy = new();

    public static IServiceCollection AddHonuaAuditActionResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAuditActionResolver, DefaultAuditActionResolver>();
        return services;
    }

    // Binds the operator audit switches (AuditLog:Policy). Both default to off.
    public static IServiceCollection AddHonuaAuditPolicy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // ValidateOnStart binds at boot, so a malformed value fails startup instead of the first write.
        services.AddOptions<AuditPolicyOptions>()
            .Bind(configuration.GetSection(AuditPolicyOptions.SectionName))
            .ValidateOnStart();
        return services;
    }

    // Reads the current policy. A reload that cannot bind keeps the default policy rather than
    // failing a request or a write that has already been applied.
    internal static AuditPolicyOptions CurrentPolicy(IOptionsMonitor<AuditPolicyOptions>? monitor)
    {
        try
        {
            return monitor?.CurrentValue ?? DefaultPolicy;
        }
        catch (InvalidOperationException)
        {
            return DefaultPolicy;
        }
    }

    // Call this after the provider has registered IFeatureWriter, or later writes miss this audit decorator.
    public static IServiceCollection AddAuditingFeatureWriter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        return services.DecorateFeatureWriter(static (sp, inner) =>
        {
            var auditLog = sp.GetService<IAuditLog>() ?? NullAuditLog.Instance;
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            var policy = sp.GetService<IOptionsMonitor<AuditPolicyOptions>>();
            return new AuditingFeatureWriter(
                inner,
                auditLog,
                new FeatureWriteAuditScope(httpContextAccessor, () => CurrentPolicy(policy)));
        });
    }
}
