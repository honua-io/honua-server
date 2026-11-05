// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Billing;
using Honua.Core.Features.MultiTenancy.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.MultiTenancy;

public static class TenantLifecycleExtensions
{
    public static IServiceCollection AddHonuaTenantLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITenantCatalog, InMemoryTenantCatalog>();
        services.TryAddSingleton<IBillingUsageSink, LoggingBillingUsageSink>();
        // Scoped: TenantLifecycleService consumes the scoped IAuditLog sink, so it must
        // not be a singleton or it captures a scoped service and fails ValidateOnBuild/
        // ValidateScopes at host startup. It is only resolved from scoped admin endpoint
        // handlers (TenantAdminEndpoints), so request-scoped lifetime is correct.
        services.TryAddScoped<TenantLifecycleService>();
        services.TryAddSingleton<TenantBillingExporter>();

        return services;
    }

    public static IApplicationBuilder UseHonuaTenantStatusEnforcement(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<TenantStatusEnforcementMiddleware>();
        return app;
    }
}
