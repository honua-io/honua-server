// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Service collection and application builder extensions for the tenant context rail
/// (issue #1144).
/// </summary>
public static class TenantContextExtensions
{
    /// <summary>
    /// Registers tenant context resolution services. Binds <see cref="TenantContextOptions"/>
    /// from the <c>MultiTenancy</c> configuration section and registers a scoped
    /// <see cref="ITenantContext"/> per request.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root used to bind options.</param>
    /// <param name="configure">Optional callback to further customize the options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaTenantContext(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TenantContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var optionsBuilder = services
            .AddOptions<TenantContextOptions>()
            .Bind(configuration.GetSection(TenantContextOptions.SectionName));

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Scoped so each request gets its own instance — guarantees no tenant id leaks
        // between concurrent requests, which is the core invariant of issue #1144.
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<RequestTenantContext>());

        return services;
    }

    /// <summary>
    /// Adds the <see cref="TenantContextMiddleware"/> to the request pipeline. Must be
    /// registered after authentication so that claims are available, and before any
    /// feature handler that reads <see cref="ITenantContext"/>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHonuaTenantContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
