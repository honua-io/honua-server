// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.MultiTenancy;

public static class TenantContextExtensions
{
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

    // Register tenant resolution after authentication and before any handler that reads the tenant.
    public static IApplicationBuilder UseHonuaTenantContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
