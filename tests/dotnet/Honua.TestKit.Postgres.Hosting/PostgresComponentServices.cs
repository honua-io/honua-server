// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Infrastructure.Licensing;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Db.Postgres;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Server.Startup;
using Microsoft.Extensions.Options;

namespace Honua.TestKit.Postgres.Hosting;

/// <summary>Production request services over an already provisioned component database.</summary>
public static class PostgresComponentServices
{
    public static void Configure(WebApplicationBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;
        if (string.IsNullOrWhiteSpace(builder.Configuration["Security:ConnectionEncryption:MasterKey"]))
        {
            throw new InvalidOperationException("Security:ConnectionEncryption:MasterKey is required.");
        }
        // This composition exercises supported disabled licensing; signed-license
        // bootstrap, capacity and refresh are retained in full-host tests.
        builder.Services.AddSingleton<DisabledLicenseService>();
        builder.Services.AddSingleton<ILicenseEntitlementService>(sp => sp.GetRequiredService<DisabledLicenseService>());
        builder.Services.AddSingleton<ILicenseStatusProvider>(sp => sp.GetRequiredService<DisabledLicenseService>());
        builder.Services.AddMemoryCache();
        builder.Services.AddOptions<LimitsOptions>()
            .Bind(builder.Configuration.GetSection(LimitsOptions.SectionName));
        builder.Services.AddSingleton<IValidateOptions<LimitsOptions>, LimitsOptionsValidator>();
        builder.Services.AddOptions<CacheOptions>().Bind(builder.Configuration.GetSection(CacheOptions.SectionName));
        builder.Services.AddPostgreSqlServices(builder.Configuration, ServerCoreSchemaMigrations.Manifest);
        builder.Services.AddSingleton<IGeometryService, GeometryService>();
        builder.Services.AddScoped<IGeometryValidator>(sp => new WkbGeometryValidator(
            sp.GetRequiredService<IOptions<LimitsOptions>>(),
            sp.GetRequiredService<IGeometryTopologyValidator>(),
            sp.GetRequiredService<ILogger<WkbGeometryValidator>>()));
        builder.Services.AddValidationServices();
        builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();
        builder.Services.AddScoped<IRowLevelSecurityFilterSource, RowLevelSecurityFilterSource>();
        builder.Services.AddScoped<IFieldMaskSource, FieldMaskSource>();
        builder.Services.AddSingleton<IETagService, ETagService>();
        builder.Services.AddSingleton<IResponseCache, MemoryResponseCache>();
        builder.Services.AddSingleton(sp => new OutputCacheInvalidationService(
            null, sp.GetRequiredService<IResponseCache>(), null,
            sp.GetRequiredService<IServiceScopeFactory>(), null,
            sp.GetRequiredService<ILogger<OutputCacheInvalidationService>>()));
        // Supported node-local production event store/publisher. Streaming transports,
        // webhooks and background outbox delivery are verified by the full host lanes.
        builder.Services.AddOptions<FeatureChangeEventOptions>();
        builder.Services.AddSingleton<IFeatureChangeEventStore, InMemoryFeatureChangeEventStore>();
        builder.Services.AddSingleton<IFeatureChangeEventPublisher, FeatureChangeEventPublisher>();
        builder.Services.AddSingleton<FeatureMutationEventService>();
        builder.Services.AddApiKeyAuthentication(builder.Configuration);
        builder.Services.Configure<ApiKeyAuthenticationOptions>(options =>
        {
            options.AdminPassword = builder.Configuration["HONUA_ADMIN_PASSWORD"];
            options.EnvironmentName = builder.Environment.EnvironmentName;
            options.IsDevelopmentMode = false;
            options.IsTestMode = false;
        });
        builder.Services.AddInputValidation(builder.Configuration);
        OutputCachingRegistration.ConfigureOutputCaching(builder.Services, builder.Configuration);
    }

    public static void UseRequestPipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseGlobalExceptionHandling();
        app.UseMiddleware<AuthenticationResponseCacheMiddleware>();
        app.UseLimitsEnforcement();
        app.UseInputValidation();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWhen(context => OutputCachingRegistration.HasLiveOutputCacheEntitlements(context.RequestServices, false),
            static entitled => entitled.UseOutputCache());
    }
}
