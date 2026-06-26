// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Billing;
using Honua.Core.Features.MultiTenancy.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Service collection and application builder extensions for tenant lifecycle/provisioning and
/// billing wiring (issue #2156).
/// </summary>
public static class TenantLifecycleExtensions
{
    /// <summary>
    /// Registers the tenant catalog, lifecycle service, default billing sink, and billing exporter.
    /// Provisioning is opt-in: the catalog starts empty so single-tenant deployments are unaffected
    /// until an operator creates tenants through the admin surface.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaTenantLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITenantCatalog, InMemoryTenantCatalog>();
        services.TryAddSingleton<IBillingUsageSink, LoggingBillingUsageSink>();
        services.TryAddSingleton<TenantLifecycleService>();
        services.TryAddSingleton<TenantBillingExporter>();

        return services;
    }

    /// <summary>
    /// Adds the <see cref="TenantStatusEnforcementMiddleware"/> so suspended/deleted tenants are
    /// blocked. Must run after <c>UseHonuaTenantContext</c>. It is a no-op for tenants not present
    /// in the catalog, so the default pipeline is unchanged until tenants are provisioned.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHonuaTenantStatusEnforcement(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<TenantStatusEnforcementMiddleware>();
        return app;
    }
}
