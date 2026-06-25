// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Service collection and application builder extensions for the schema-per-tenant routing
/// and usage-metering increment (issue #346).
/// </summary>
public static class TenantSchemaRoutingExtensions
{
    /// <summary>
    /// Registers the tenant schema resolver and the per-tenant usage meter, and binds
    /// <see cref="TenantSchemaOptions"/> from the <c>MultiTenancy:SchemaRouting</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root used to bind options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// The usage meter is always registered (it is harmless when routing is disabled — it is
    /// only invoked by the routing middleware, which is not added unless routing is enabled).
    /// Schema routing itself is gated on <see cref="TenantSchemaOptions.Enabled"/> at the
    /// middleware registration site so the default single-tenant pipeline is unchanged.
    /// </remarks>
    public static IServiceCollection AddHonuaTenantSchemaRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TenantSchemaOptions>()
            .Bind(configuration.GetSection(TenantSchemaOptions.SectionName));

        services.TryAddSingleton<ITenantUsageMeter, InMemoryTenantUsageMeter>();
        services.TryAddSingleton<ITenantSchemaResolver, TenantSchemaResolver>();

        return services;
    }

    /// <summary>
    /// Determines whether schema-per-tenant routing is enabled in the supplied configuration.
    /// Used both to decide whether to register the routing middleware and to put the database
    /// connection pool into request-scoped schema mode.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><see langword="true"/> when routing is enabled; otherwise <see langword="false"/>.</returns>
    public static bool IsTenantSchemaRoutingEnabled(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>($"{TenantSchemaOptions.SectionName}:Enabled");
    }

    /// <summary>
    /// Adds the <see cref="TenantSchemaRoutingMiddleware"/> to the request pipeline when
    /// schema-per-tenant routing is enabled. Must run after <c>UseHonuaTenantContext</c> so the
    /// tenant is resolved, and before any feature handler that reads the database. When routing
    /// is disabled this is a no-op and the pipeline is byte-identical to single-tenant.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHonuaTenantSchemaRouting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetService<IOptions<TenantSchemaOptions>>();
        if (options?.Value.Enabled == true)
        {
            app.UseMiddleware<TenantSchemaRoutingMiddleware>();
        }

        return app;
    }
}
