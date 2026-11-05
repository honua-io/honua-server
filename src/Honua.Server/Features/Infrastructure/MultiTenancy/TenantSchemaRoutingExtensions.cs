// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

public static class TenantSchemaRoutingExtensions
{
    public static IServiceCollection AddHonuaTenantSchemaRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TenantSchemaOptions>()
            .Bind(configuration.GetSection(TenantSchemaOptions.SectionName));

        if (configuration.IsTenantSchemaRoutingEnabled())
        {
            // Production routing needs the same request-scoped context as the database
            // search-path applier, independently of optional test schema headers.
            services.TryAddScoped<SchemaContext>();
            services.TryAddScoped<ISchemaContext>(provider => provider.GetRequiredService<SchemaContext>());
        }

        services.TryAddSingleton<ITenantUsageMeter, InMemoryTenantUsageMeter>();
        services.TryAddSingleton<ITenantSchemaResolver, TenantSchemaResolver>();

        return services;
    }

    public static bool IsTenantSchemaRoutingEnabled(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>($"{TenantSchemaOptions.SectionName}:Enabled");
    }

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
